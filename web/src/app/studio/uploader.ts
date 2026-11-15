import { HttpClient, HttpErrorResponse, HttpEventType, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, firstValueFrom } from 'rxjs';
import { PartUrl, UploadState, UploadSummary } from '../api/models';

export type Progress = (sent: number, total: number) => void;

/** How many parts are on the wire at once. */
const CONCURRENCY = 3;
/** The pauses before the 1st, 2nd and 3rd retry of a part that failed on the network. */
const RETRY_DELAYS_MS = [1000, 2000, 4000];

/** What the parts of one run share: the part list (which a refresh replaces) and the ETags collected so far. */
interface Run {
  state: UploadState;
  file: File;
  etags: Map<number, string>;
  urls: Map<number, PartUrl>;
  /** The `GET` that fetches fresh URLs while one is running: parts that get a 403 together await this one promise. */
  refreshing?: Promise<void>;
  /** Cancels the run: pending retries, the request in flight and the next steps. */
  signal?: AbortSignal;
  /** The bytes each part has sent so far, for the progress bar. */
  inFlight: Map<number, number>;
  progress: Progress;
}

/**
 * Sends a file straight to S3 in parts, on presigned URLs from Media, and tells Media to assemble it.
 *
 * The browser keeps nothing: after a reload `resume` asks Media (`GET /api/uploads/{id}`) which parts S3 already has and
 * sends only the rest. The requests to S3 go through `HttpClient` like any other, but `csrfInterceptor` only touches
 * our own origin's `/api` and `/auth`, and a cross-origin request carries no cookie unless `withCredentials` is set
 * (MDN, "Using XMLHttpRequest": https://developer.mozilla.org/en-US/docs/Web/API/XMLHttpRequest/withCredentials): S3
 * sees neither on these requests. (Thumbnail `<img>` requests and the download navigation do carry the cookie to S3 on this
 * local stack: `localhost:8100` and `localhost:4566` are the same site and "Cookies do not provide isolation by port",
 * RFC 6265 section 8.5, https://www.rfc-editor.org/rfc/rfc6265#section-8.5. Production needs a separate S3 or CDN domain.) An `AbortSignal` stops the run; the parts already on S3 stay there for `resume`.
 */
@Injectable({ providedIn: 'root' })
export class Uploader {
  private readonly http = inject(HttpClient);

  async upload(videoId: string, file: File, progress: Progress, signal?: AbortSignal): Promise<UploadState> {
    const created = await firstValueFrom(
      guard(this.http.post<UploadState>('/api/uploads', { videoId, fileName: file.name, contentType: file.type, sizeBytes: file.size }), signal),
    );
    return this.send(created, file, progress, signal);
  }

  async resume(upload: UploadSummary, file: File, progress: Progress, signal?: AbortSignal): Promise<UploadState> {
    if (file.name !== upload.fileName || file.size !== upload.sizeBytes) {
      throw new Error('Choose the same file you started with');
    }
    return this.send(await this.getState(upload.id, signal), file, progress, signal);
  }

  private getState(id: string, signal?: AbortSignal): Promise<UploadState> {
    return firstValueFrom(guard(this.http.get<UploadState>(`/api/uploads/${id}`), signal));
  }

  private async send(state: UploadState, file: File, progress: Progress, signal?: AbortSignal): Promise<UploadState> {
    const run: Run = { state, file, etags: new Map(), urls: new Map(), inFlight: new Map(), progress, signal };
    this.adopt(run, state);
    this.report(run);

    // A small pool: CONCURRENCY workers take the next missing part until none is left. One failed part rejects the
    // whole send; the parts already sent stay on S3 and `resume` finds them.
    const queue = [...run.urls.keys()].sort((a, b) => a - b);
    const worker = async (): Promise<void> => {
      for (let part = queue.shift(); part !== undefined; part = queue.shift()) {
        if (!run.etags.has(part)) {
          await this.sendPart(run, part);
        }
      }
    };
    await Promise.all(Array.from({ length: Math.min(CONCURRENCY, queue.length) }, worker));

    // Every part's ETag: the ones S3 already listed and the ones our own PUTs returned.
    const parts = [...run.etags].sort(([a], [b]) => a - b).map(([partNumber, eTag]) => ({ partNumber, eTag }));
    return firstValueFrom(guard(this.http.post<UploadState>(`/api/uploads/${state.id}/complete`, { parts }), signal));
  }

  /** Takes in what Media says: the parts S3 has (their ETags) and the URLs for the rest. */
  private adopt(run: Run, state: UploadState): void {
    for (const p of state.uploadedParts) {
      run.etags.set(p.partNumber, p.eTag);
    }
    run.urls = new Map(state.partUrls.map((u) => [u.partNumber, u]));
  }

  /** Parts with an ETag are finished (S3 had them or we sent them); the others count the bytes they have sent so far. */
  private report(run: Run): void {
    let sent = 0;
    for (const part of run.etags.keys()) {
      sent += this.partSize(run, part);
    }
    for (const loaded of run.inFlight.values()) {
      sent += loaded;
    }
    run.progress(Math.min(sent, run.file.size), run.file.size);
  }

  private partSize(run: Run, part: number): number {
    return Math.min(run.state.partSize, run.file.size - (part - 1) * run.state.partSize);
  }

  private async sendPart(run: Run, part: number): Promise<void> {
    let retries = 0;
    let refreshed = false;
    for (;;) {
      const target = run.urls.get(part);
      if (!target) {
        return; // A refresh showed that S3 has the part by now.
      }
      try {
        run.etags.set(part, await this.put(run, target));
        this.report(run);
        return;
      } catch (error) {
        run.inFlight.delete(part);
        this.report(run);
        const status = error instanceof HttpErrorResponse ? error.status : -1;
        if (status === 403 && !refreshed) {
          // The URL expired: ask Media for fresh ones, once per part, and retry at once.
          refreshed = true;
          await this.refresh(run);
        } else if (status === 0 && retries < RETRY_DELAYS_MS.length) {
          await delay(RETRY_DELAYS_MS[retries++], run.signal);
        } else {
          throw error;
        }
      }
    }
  }

  /**
   * All part URLs expire together, so several parts can get a 403 at once. They share one `GET`: the first starts it,
   * the others await the same promise, and each then retries with the new URLs.
   */
  private refresh(run: Run): Promise<void> {
    run.refreshing ??= this.getState(run.state.id, run.signal)
      .then((fresh) => {
        run.state = fresh;
        this.adopt(run, fresh);
      })
      .finally(() => (run.refreshing = undefined));
    return run.refreshing;
  }

  /** PUTs one slice and returns the `ETag` S3 answers with. */
  private async put(run: Run, target: PartUrl): Promise<string> {
    const start = (target.partNumber - 1) * run.state.partSize;
    const body = run.file.slice(start, start + target.size);
    const response = await new Promise<HttpResponse<unknown>>((resolve, reject) => {
      guard(this.http.put(target.url, body, { observe: 'events', reportProgress: true }), run.signal).subscribe({
        next: (event) => {
          if (event.type === HttpEventType.UploadProgress) {
            run.inFlight.set(target.partNumber, event.loaded);
            this.report(run);
          } else if (event.type === HttpEventType.Response) {
            resolve(event);
          }
        },
        error: reject,
      });
    });
    run.inFlight.delete(target.partNumber);
    const etag = response.headers.get('ETag');
    if (!etag) {
      throw new Error('S3 sent no ETag: its CORS rule must expose the ETag header.');
    }
    return etag;
  }
}

function abortError(): Error {
  return new DOMException('The upload was cancelled.', 'AbortError');
}

/** Errors with an `AbortError` when `signal` aborts, which unsubscribes the request (and so cancels it). */
function guard<T>(source: Observable<T>, signal?: AbortSignal): Observable<T> {
  if (!signal) {
    return source;
  }
  return new Observable<T>((subscriber) => {
    if (signal.aborted) {
      subscriber.error(abortError());
      return;
    }
    // Forward by hand: subscribing with `subscriber` itself would make the unsubscribe below close it before it errors.
    const inner = source.subscribe({ next: (v) => subscriber.next(v), error: (e) => subscriber.error(e), complete: () => subscriber.complete() });
    const onAbort = () => {
      inner.unsubscribe();
      subscriber.error(abortError());
    };
    signal.addEventListener('abort', onAbort, { once: true });
    return () => {
      signal.removeEventListener('abort', onAbort);
      inner.unsubscribe();
    };
  });
}

/** A pause that an abort cuts short. */
function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(abortError());
      return;
    }
    const onAbort = () => {
      clearTimeout(timer);
      reject(abortError());
    };
    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}
