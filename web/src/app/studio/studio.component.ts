import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { MyVideo, Problem, TERMINAL_UPLOAD_STATUSES, UploadSummary } from '../api/models';
import { Uploader, VIDEO_ACCEPT } from './uploader';

/** Polling: every 5 s for the first minute of waiting, then every 15 s, with no cap. */
const FAST_POLL_MS = 5000;
const SLOW_POLL_MS = 15000;
const FAST_POLL_WINDOW_MS = 60000;

const FAILURE_REASONS: Record<string, string> = {
  unreadable: "The file isn't a video FFmpeg can read",
  'unsupported-format': 'The file format is not supported',
  'no-video-stream': 'The file has no video stream',
  'zero-duration': 'The video has no duration',
  'encode-failed': 'The video could not be converted for streaming. Try another file',
  'timed-out': 'Converting the video took too long. Try a shorter or smaller file',
  'retries-exhausted': 'Storage was unavailable while the video was processed. Upload it again',
  'size-mismatch': 'The uploaded file is not the size you chose. Upload it again',
};

@Component({
  selector: 'app-studio',
  template: `
    <h1>Studio</h1>
    <form (submit)="$event.preventDefault(); create()">
      <input placeholder="Title" aria-label="Title" data-testid="new-video-title" [value]="title()" (input)="title.set($any($event.target).value)" />
      <textarea placeholder="Description" aria-label="Description" data-testid="new-video-description" [value]="description()" (input)="description.set($any($event.target).value)"></textarea>
      <button type="submit" data-testid="create-video" [disabled]="!title().trim()">Create video</button>
    </form>
    @if (error(); as message) {
      <p class="error" role="alert">{{ message }}</p>
    }
    @for (video of videos(); track video.id) {
      @let upload = uploads()[video.id];
      <div class="card" data-testid="studio-row" [attr.data-video-id]="video.id">
        <strong>{{ video.title }}</strong>
        @let busy = progress()[video.id];
        <span data-testid="upload-status">{{ busy ? 'Uploading' : upload ? upload.status : 'Draft' }}</span>
        @if (upload?.status === 'Failed') {
          <span data-testid="failure-reason">{{ reason(upload!.failureReason) }}</span>
        }
        @if (busy) {
          <!-- An upload or resume is running: no second one may start. -->
        } @else if (!upload || upload.status === 'Failed' || upload.status === 'Expired') {
          <input type="file" [accept]="accept" aria-label="Choose a video file" data-testid="upload-file" (change)="start(video, $any($event.target).files?.[0])" />
        } @else if (upload.status === 'Uploading') {
          <label>
            Resume
            <input type="file" [accept]="accept" data-testid="resume-file" (change)="resume(upload, $any($event.target).files?.[0])" />
          </label>
        }
        @if (upload?.status === 'Uploading') {
          <!-- Also while a run is in flight: cancel stops it first, then ends the upload. -->
          <button type="button" data-testid="cancel-upload" (click)="cancel(upload!)">Cancel upload</button>
        }
        @if (progress()[video.id]; as p) {
          <progress data-testid="upload-progress" [max]="p.total" [value]="p.sent"></progress>
        }
        @if (rowErrors()[video.id]; as message) {
          <p class="error" role="alert" data-testid="upload-error">{{ message }}</p>
        }
      </div>
    } @empty {
      <p>You have no videos yet.</p>
    }
  `,
})
export class StudioComponent {
  private readonly http = inject(HttpClient);
  private readonly uploader = inject(Uploader);

  protected readonly accept = VIDEO_ACCEPT;
  protected readonly title = signal('');
  protected readonly description = signal('');
  protected readonly videos = signal<MyVideo[]>([]);
  /** The latest upload of each video, by video id. */
  protected readonly uploads = signal<Record<string, UploadSummary>>({});
  protected readonly progress = signal<Record<string, { sent: number; total: number }>>({});
  protected readonly rowErrors = signal<Record<string, string>>({});
  protected readonly error = signal<string | null>(null);

  private timer: ReturnType<typeof setTimeout> | undefined;
  private waitedMs = 0;
  private destroyed = false;
  private readonly destroyRef = inject(DestroyRef);
  /** Aborted on destroy: stops retry timers and in-flight PUTs. The parts already on S3 stay, so the upload can be resumed later. */
  private readonly abort = new AbortController();
  /** The run in flight for each video: its signal follows `abort` and also stops when the creator cancels the upload. */
  private readonly runs = new Map<string, AbortController>();

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.destroyed = true;
      this.abort.abort();
      clearTimeout(this.timer);
    });
    void this.loadAll();
  }

  protected reason(code: string | null): string {
    return code ? (FAILURE_REASONS[code] ?? code) : 'Processing failed';
  }

  protected async create(): Promise<void> {
    this.error.set(null);
    try {
      await firstValueFrom(this.http.post('/api/videos', { title: this.title().trim(), description: this.description().trim() }));
      this.title.set('');
      this.description.set('');
      await this.loadAll();
    } catch {
      this.error.set('Could not create the video. Try again.');
    }
  }

  protected start(video: MyVideo, file: File | undefined): void {
    if (file) {
      void this.run(video.id, file.size, (progress, signal) => this.uploader.upload(video.id, file, progress, signal));
    }
  }

  protected resume(upload: UploadSummary, file: File | undefined): void {
    if (file) {
      void this.run(upload.videoId, file.size, (progress, signal) => this.uploader.resume(upload, file, progress, signal));
    }
  }

  private async run(
    videoId: string, total: number,
    send: (progress: (sent: number, total: number) => void, signal: AbortSignal) => Promise<unknown>,
  ): Promise<void> {
    this.setRowError(videoId, null);
    this.progress.update((p) => ({ ...p, [videoId]: { sent: 0, total } }));
    // One signal per run: it ends with the component (destroy) or when the creator cancels this video's upload.
    const own = new AbortController();
    this.runs.set(videoId, own);
    try {
      await send((sent, total) => this.progress.update((p) => ({ ...p, [videoId]: { sent, total } })), AbortSignal.any([this.abort.signal, own.signal]));
    } catch (error) {
      // A cancel is the creator's own choice, not a failure to show.
      if (!this.destroyed && !own.signal.aborted) {
        this.setRowError(videoId, uploadErrorMessage(error));
      }
    }
    if (this.runs.get(videoId) === own) {
      this.runs.delete(videoId);
    }
    if (this.destroyed || own.signal.aborted) {
      return; // After a cancel, `cancel` clears the progress and reloads the uploads.
    }
    this.progress.update(({ [videoId]: _done, ...rest }) => rest);
    await this.loadUploads();
  }

  /**
   * Ends an upload the creator no longer wants (`DELETE /api/uploads/{id}`, which leaves it `Expired`: the status that offers a
   * new file). A run in flight is aborted first, so no PUT goes on after the upload is gone.
   */
  protected async cancel(upload: UploadSummary): Promise<void> {
    this.runs.get(upload.videoId)?.abort();
    this.setRowError(upload.videoId, null);
    try {
      await firstValueFrom(this.http.delete(`/api/uploads/${upload.id}`));
    } catch {
      if (!this.destroyed) {
        this.setRowError(upload.videoId, 'Could not cancel this upload. Try again.');
      }
    }
    if (!this.destroyed) {
      this.progress.update(({ [upload.videoId]: _cancelled, ...rest }) => rest);
      await this.loadUploads();
    }
  }

  private setRowError(videoId: string, message: string | null): void {
    this.rowErrors.update(({ [videoId]: _old, ...rest }) => (message ? { ...rest, [videoId]: message } : rest));
  }

  private async loadAll(): Promise<void> {
    await Promise.all([this.loadVideos(), this.loadUploads()]);
  }

  private async loadVideos(): Promise<void> {
    try {
      this.videos.set(await firstValueFrom(this.http.get<MyVideo[]>('/api/me/videos').pipe(takeUntilDestroyed(this.destroyRef))));
    } catch {
      this.error.set('Could not load your videos.');
    }
  }

  /** Loads the uploads, then keeps polling while one is `Processing` (the worker changes it, not the browser). */
  private async loadUploads(): Promise<void> {
    clearTimeout(this.timer);
    try {
      const list = await firstValueFrom(this.http.get<UploadSummary[]>('/api/me/uploads').pipe(takeUntilDestroyed(this.destroyRef)));
      const before = this.uploads();
      this.uploads.set(Object.fromEntries(list.map((u) => [u.videoId, u])));
      // A video that just reached a terminal status has a new status and thumbnail in Catalog.
      if (list.some((u) => before[u.videoId]?.status === 'Processing' && TERMINAL_UPLOAD_STATUSES.includes(u.status))) {
        void this.loadVideos();
      }
    } catch {
      // Keep the last list; the next poll tries again.
    }
    this.schedulePoll();
  }

  private schedulePoll(): void {
    if (this.destroyed || !Object.values(this.uploads()).some((u) => u.status === 'Processing')) {
      this.waitedMs = 0;
      return;
    }
    const delay = this.waitedMs < FAST_POLL_WINDOW_MS ? FAST_POLL_MS : SLOW_POLL_MS;
    this.waitedMs += delay;
    this.timer = setTimeout(() => void this.loadUploads(), delay);
  }
}

function uploadErrorMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    const type = (error.error as Problem | null)?.type;
    if (type === 'urn:newstore:problem:upload-lost') {
      return 'Storage lost this upload. It must start over.';
    }
    if (type === 'urn:newstore:problem:storage-unavailable') {
      return 'Storage is not reachable. Try again later.';
    }
  }
  return error instanceof Error && !(error instanceof HttpErrorResponse) ? error.message : 'The upload failed. Try again.';
}
