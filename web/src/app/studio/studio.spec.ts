import { HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BROWSER_LOCATION } from '../api/browser-location';
import { MyVideo, UploadState, UploadSummary } from '../api/models';
import { csrfInterceptor } from '../auth/csrf.interceptor';
import { VideoPlayerComponent } from '../videos/video-player.component';
import { StudioComponent } from './studio.component';
import { Uploader } from './uploader';

// hls.js needs a browser (MediaSource); jsdom has none. The fake records what the player does with it.
const hls = vi.hoisted(() => ({ failToLoad: false, supported: true, instances: [] as Array<Record<string, any>> }));
vi.mock('hls.js', () => {
  class FakeHls {
    static Events = { MANIFEST_PARSED: 'hlsManifestParsed', ERROR: 'hlsError' };
    static isSupported = () => hls.supported;
    levels = [{ height: 360 }, { height: 720 }];
    currentLevel = -1;
    loadSource = vi.fn();
    attachMedia = vi.fn();
    destroy = vi.fn();
    on = vi.fn();
    constructor() {
      hls.instances.push(this);
    }
  }
  // A getter that throws makes `import('hls.js').then(({ default }) => ...)` reject, as a chunk that fails to load does.
  return {
    get default() {
      if (hls.failToLoad) {
        throw new Error('Failed to fetch dynamically imported module');
      }
      return FakeHls;
    },
  };
});

const fakeLocation = { origin: 'http://localhost:8100', pathname: '/', search: '', href: '' };
const s3 = (part: number, n = 1) => `http://localhost:4566/newstore-media/originals/u1?partNumber=${part}&sig=${n}`;

/** 25 bytes, each byte equal to its index, so a slice shows which bytes a part carried. Parts of 10 make three: 10, 10 and 5. */
const file = () => new File([Uint8Array.from({ length: 25 }, (_, i) => i)], 'a.mp4', { type: 'video/mp4' });

const state = (over: Partial<UploadState> = {}): UploadState => ({
  id: 'u1', videoId: 'v1', fileName: 'a.mp4', sizeBytes: 25, status: 'Uploading', failureReason: null,
  partSize: 10, partCount: 3, uploadedParts: [],
  partUrls: [{ partNumber: 1, url: s3(1), size: 10 }, { partNumber: 2, url: s3(2), size: 10 }, { partNumber: 3, url: s3(3), size: 5 }],
  partUrlsExpireAt: '2026-01-01T01:00:00Z', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', ...over,
});

const summary = (over: Partial<UploadSummary> = {}): UploadSummary => ({
  id: 'u1', videoId: 'v1', fileName: 'a.mp4', sizeBytes: 25, status: 'Uploading', failureReason: null,
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', ...over,
});

function setup() {
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([csrfInterceptor])), provideHttpClientTesting(),
      { provide: BROWSER_LOCATION, useValue: fakeLocation },
    ],
  });
  return { backend: TestBed.inject(HttpTestingController), uploader: TestBed.inject(Uploader) };
}

/** Lets the promises between two HTTP calls run (and fires timers due within `ms`). */
const tick = (ms = 0) => vi.advanceTimersByTimeAsync(ms);
const etag = (value: string) => ({ headers: { ETag: value } });
const bytes = async (req: TestRequest) => Array.from(new Uint8Array(await (req.request.body as Blob).arrayBuffer()));
const range = (from: number, to: number) => Array.from({ length: to - from }, (_, i) => from + i);

describe('uploader', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('Uploads_parts_and_completes_with_their_etags', async () => {
    const { backend, uploader } = setup();
    const progress = vi.fn();
    const done = uploader.upload('v1', file(), progress);

    const create = backend.expectOne('/api/uploads');
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual({ videoId: 'v1', fileName: 'a.mp4', contentType: 'video/mp4', sizeBytes: 25 });
    create.flush(state());
    await tick();

    const puts = [1, 2, 3].map((n) => backend.expectOne(s3(n)));
    expect(puts.map((p) => p.request.method)).toEqual(['PUT', 'PUT', 'PUT']);
    expect(await bytes(puts[0])).toEqual(range(0, 10));
    expect(await bytes(puts[1])).toEqual(range(10, 20));
    expect(await bytes(puts[2])).toEqual(range(20, 25));
    puts.forEach((p, i) => p.flush(null, etag(`"e${i + 1}"`)));
    await tick();

    const complete = backend.expectOne('/api/uploads/u1/complete');
    expect(complete.request.body).toEqual({ parts: [{ partNumber: 1, eTag: '"e1"' }, { partNumber: 2, eTag: '"e2"' }, { partNumber: 3, eTag: '"e3"' }] });
    complete.flush(state({ status: 'Processing', partUrls: [] }));

    expect((await done).status).toBe('Processing');
    expect(progress).toHaveBeenLastCalledWith(25, 25);
    backend.verify();
  });

  it('Retries_a_failed_part_then_completes', async () => {
    const { backend, uploader } = setup();
    const done = uploader.upload('v1', file(), vi.fn());
    backend.expectOne('/api/uploads').flush(state({ partCount: 1, partSize: 25, partUrls: [{ partNumber: 1, url: s3(1), size: 25 }] }));
    await tick();

    backend.expectOne(s3(1)).error(new ProgressEvent('error'));
    await tick(999);
    backend.expectNone(s3(1));
    await tick(1);
    backend.expectOne(s3(1)).error(new ProgressEvent('error'));
    await tick(1999);
    backend.expectNone(s3(1));
    await tick(1);
    backend.expectOne(s3(1)).flush(null, etag('"e1"'));
    await tick();

    backend.expectOne('/api/uploads/u1/complete').flush(state({ status: 'Processing', partUrls: [] }));
    expect((await done).status).toBe('Processing');
    backend.verify();
  });

  it('Gives_up_after_three_retries', async () => {
    const { backend, uploader } = setup();
    const done = uploader.upload('v1', file(), vi.fn());
    const failure = done.catch((e: unknown) => e);
    backend.expectOne('/api/uploads').flush(state({ partCount: 1, partSize: 25, partUrls: [{ partNumber: 1, url: s3(1), size: 25 }] }));
    await tick();

    backend.expectOne(s3(1)).error(new ProgressEvent('error'));
    for (const wait of [1000, 2000, 4000]) {
      await tick(wait);
      backend.expectOne(s3(1)).error(new ProgressEvent('error'));
    }
    await tick(60_000);

    backend.expectNone(s3(1));
    expect(await failure).toBeInstanceOf(HttpErrorResponse);
    backend.expectNone('/api/uploads/u1/complete');
  });

  it('Resume_uploads_only_the_missing_parts', async () => {
    const { backend, uploader } = setup();
    const progress = vi.fn();
    const done = uploader.resume(summary(), file(), progress);

    backend.expectOne('/api/uploads/u1').flush(state({
      uploadedParts: [{ partNumber: 1, eTag: '"e1"', size: 10 }],
      partUrls: [{ partNumber: 2, url: s3(2), size: 10 }, { partNumber: 3, url: s3(3), size: 5 }],
    }));
    await tick();

    expect(progress).toHaveBeenCalledWith(10, 25);
    const puts = [2, 3].map((n) => backend.expectOne(s3(n)));
    expect(await bytes(puts[0])).toEqual(range(10, 20));
    expect(await bytes(puts[1])).toEqual(range(20, 25));
    puts[0].flush(null, etag('"e2"'));
    puts[1].flush(null, etag('"e3"'));
    await tick();

    const complete = backend.expectOne('/api/uploads/u1/complete');
    expect(complete.request.body).toEqual({ parts: [{ partNumber: 1, eTag: '"e1"' }, { partNumber: 2, eTag: '"e2"' }, { partNumber: 3, eTag: '"e3"' }] });
    complete.flush(state({ status: 'Processing', partUrls: [] }));
    await done;
    backend.verify();
  });

  it('Resume_of_an_assembled_upload_completes_with_the_listed_etags', async () => {
    const { backend, uploader } = setup();
    const done = uploader.resume(summary(), file(), vi.fn());
    backend.expectOne('/api/uploads/u1').flush(state({
      uploadedParts: [1, 2, 3].map((n) => ({ partNumber: n, eTag: '"assembled"', size: n === 3 ? 5 : 10 })), partUrls: [],
    }));
    await tick();

    const complete = backend.expectOne('/api/uploads/u1/complete');
    expect(complete.request.body).toEqual({ parts: [1, 2, 3].map((n) => ({ partNumber: n, eTag: '"assembled"' })) });
    complete.flush(state({ status: 'Processing', partUrls: [] }));
    await done;
    backend.verify();
  });

  it('Resume_refuses_a_different_file', async () => {
    const { backend, uploader } = setup();

    await expect(uploader.resume(summary({ fileName: 'other.mp4' }), file(), vi.fn())).rejects.toThrow('Choose the same file you started with');
    await expect(uploader.resume(summary({ sizeBytes: 26 }), file(), vi.fn())).rejects.toThrow('Choose the same file you started with');
    backend.verify();
  });

  it('Refreshes_part_urls_after_a_403', async () => {
    const { backend, uploader } = setup();
    const done = uploader.resume(summary(), file(), vi.fn());
    backend.expectOne('/api/uploads/u1').flush(state({ partCount: 1, partSize: 25, partUrls: [{ partNumber: 1, url: s3(1), size: 25 }] }));
    await tick();

    backend.expectOne(s3(1)).flush('expired', { status: 403, statusText: 'Forbidden' });
    await tick();
    backend.expectOne('/api/uploads/u1').flush(state({ partCount: 1, partSize: 25, partUrls: [{ partNumber: 1, url: s3(1, 2), size: 25 }] }));
    await tick();
    backend.expectNone(s3(1));
    backend.expectOne(s3(1, 2)).flush(null, etag('"e1"'));
    await tick();

    backend.expectOne('/api/uploads/u1/complete').flush(state({ status: 'Processing', partUrls: [] }));
    await done;
    backend.verify();
  });

  it('Refreshes_once_when_every_part_gets_a_403_together', async () => {
    const { backend, uploader } = setup();
    const done = uploader.resume(summary(), file(), vi.fn());
    backend.expectOne('/api/uploads/u1').flush(state());
    await tick();

    [1, 2, 3].forEach((n) => backend.expectOne(s3(n)).flush('expired', { status: 403, statusText: 'Forbidden' }));
    await tick();
    const get = backend.expectOne('/api/uploads/u1');
    get.flush(state({ partUrls: [1, 2, 3].map((n) => ({ partNumber: n, url: s3(n, 2), size: n === 3 ? 5 : 10 })) }));
    await tick();

    backend.expectNone('/api/uploads/u1');
    [1, 2, 3].forEach((n) => backend.expectOne(s3(n, 2)).flush(null, etag(`"e${n}"`)));
    await tick();
    backend.expectOne('/api/uploads/u1/complete').flush(state({ status: 'Processing', partUrls: [] }));
    await done;
    backend.verify();
  });

  it('Keeps_at_most_three_parts_in_flight', async () => {
    const { backend, uploader } = setup();
    const big = new File([new Uint8Array(70)], 'a.mp4', { type: 'video/mp4' });
    const urls = Array.from({ length: 7 }, (_, i) => ({ partNumber: i + 1, url: s3(i + 1), size: 10 }));
    const done = uploader.resume(summary({ sizeBytes: 70 }), big, vi.fn());
    backend.expectOne('/api/uploads/u1').flush(state({ sizeBytes: 70, partCount: 7, partUrls: urls }));
    await tick();

    const open: TestRequest[] = [];
    let max = 0;
    let finished = 0;
    const pump = () => {
      open.push(...backend.match((r) => r.url.startsWith('http://localhost:4566/')));
      max = Math.max(max, open.length);
    };
    pump();
    expect(open).toHaveLength(3);
    while (open.length > 0) {
      open.shift()!.flush(null, etag(`"e${++finished}"`));
      await tick();
      pump();
    }

    expect(max).toBe(3);
    expect(finished).toBe(7);
    backend.expectOne('/api/uploads/u1/complete').flush(state({ status: 'Processing', partUrls: [] }));
    await done;
  });

  it('Stops_when_aborted_and_makes_no_further_request', async () => {
    const { backend, uploader } = setup();
    const abort = new AbortController();
    const done = uploader.upload('v1', file(), vi.fn(), abort.signal);
    const failure = done.catch((e: unknown) => e);
    backend.expectOne('/api/uploads').flush(state({ partCount: 1, partSize: 25, partUrls: [{ partNumber: 1, url: s3(1), size: 25 }] }));
    await tick();
    const put = backend.expectOne(s3(1));
    put.error(new ProgressEvent('error'));
    await tick(10); // now waiting to retry

    abort.abort();
    await tick(60_000);

    expect(((await failure) as Error).name).toBe('AbortError');
    backend.expectNone(s3(1));
    backend.expectNone('/api/uploads/u1/complete');
  });

  it('Cancels_the_request_in_flight_when_aborted', async () => {
    const { backend, uploader } = setup();
    const abort = new AbortController();
    const failure = uploader.upload('v1', file(), vi.fn(), abort.signal).catch((e: unknown) => e);
    backend.expectOne('/api/uploads').flush(state({ partCount: 1, partSize: 25, partUrls: [{ partNumber: 1, url: s3(1), size: 25 }] }));
    await tick();
    const put = backend.expectOne(s3(1));

    abort.abort();
    await tick();

    expect(put.cancelled).toBe(true);
    expect(((await failure) as Error).name).toBe('AbortError');
  });

  it('S3_requests_carry_no_csrf_header', async () => {
    const { backend, uploader } = setup();
    const done = uploader.upload('v1', file(), vi.fn());
    const create = backend.expectOne('/api/uploads');
    create.flush(state({ partCount: 1, partSize: 25, partUrls: [{ partNumber: 1, url: s3(1), size: 25 }] }));
    await tick();

    const put = backend.expectOne(s3(1));
    put.flush(null, etag('"e1"'));
    await tick();
    const complete = backend.expectOne('/api/uploads/u1/complete');
    complete.flush(state({ status: 'Processing', partUrls: [] }));
    await done;

    expect(create.request.headers.get('X-CSRF')).toBe('1');
    expect(complete.request.headers.get('X-CSRF')).toBe('1');
    expect(put.request.headers.has('X-CSRF')).toBe(false);
    expect(put.request.withCredentials).toBe(false);
  });
});

const myVideo = (over: Partial<MyVideo> = {}): MyVideo => ({
  id: 'v1', title: 'Sourdough', status: 'Draft', createdAt: '2026-01-01T00:00:00Z', publishedAt: null, thumbnailUrl: null, ...over,
});
const q = (f: ComponentFixture<unknown>, id: string) => (f.nativeElement as HTMLElement).querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
const qa = (f: ComponentFixture<unknown>, id: string) => Array.from((f.nativeElement as HTMLElement).querySelectorAll(`[data-testid="${id}"]`)) as HTMLElement[];

function studioSetup() {
  const uploader = { upload: vi.fn(), resume: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(withInterceptors([csrfInterceptor])), provideHttpClientTesting(),
      { provide: BROWSER_LOCATION, useValue: fakeLocation },
      { provide: Uploader, useValue: uploader },
    ],
  });
  const backend = TestBed.inject(HttpTestingController);
  const open = async (videos: MyVideo[], uploads: UploadSummary[]) => {
    const f = TestBed.createComponent(StudioComponent);
    f.detectChanges();
    backend.expectOne('/api/me/videos').flush(videos);
    backend.expectOne('/api/me/uploads').flush(uploads);
    await tick();
    f.detectChanges();
    return f;
  };
  return { backend, uploader, open };
}

describe('studio', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('Studio_creates_a_video_and_lists_it_as_draft', async () => {
    const { backend, open } = studioSetup();
    const f = await open([], []);
    const title = q(f, 'new-video-title') as HTMLInputElement;
    title.value = 'Sourdough';
    title.dispatchEvent(new Event('input'));
    const description = q(f, 'new-video-description') as HTMLTextAreaElement;
    description.value = 'Bread';
    description.dispatchEvent(new Event('input'));
    f.detectChanges();
    q(f, 'create-video')!.click();

    const create = backend.expectOne('/api/videos');
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual({ title: 'Sourdough', description: 'Bread' });
    create.flush(myVideo());
    await tick();
    backend.expectOne('/api/me/videos').flush([myVideo()]);
    backend.expectOne('/api/me/uploads').flush([]);
    await tick();
    f.detectChanges();

    const row = q(f, 'studio-row')!;
    expect(row.dataset['videoId']).toBe('v1');
    expect(q(f, 'upload-status')!.textContent).toContain('Draft');
    expect(q(f, 'upload-file')).not.toBeNull();
  });

  it('Studio_shows_failed_with_its_reason', async () => {
    const { open } = studioSetup();
    const f = await open([myVideo()], [summary({ status: 'Failed', failureReason: 'unreadable' })]);

    expect(q(f, 'upload-status')!.textContent).toContain('Failed');
    expect(q(f, 'failure-reason')!.textContent).toContain("The file isn't a video FFmpeg can read");
  });

  it('Studio_resumes_with_the_chosen_file', async () => {
    const { backend, uploader, open } = studioSetup();
    uploader.resume.mockResolvedValue(state({ status: 'Processing', partUrls: [] }));
    const upload = summary();
    const f = await open([myVideo()], [upload]);

    const input = q(f, 'resume-file') as HTMLInputElement;
    const chosen = file();
    Object.defineProperty(input, 'files', { value: [chosen] });
    input.dispatchEvent(new Event('change'));
    await tick();

    expect(uploader.resume).toHaveBeenCalledWith(upload, chosen, expect.any(Function), expect.any(AbortSignal));
    backend.expectOne('/api/me/uploads').flush([summary({ status: 'Processing' })]);
  });

  it('Studio_polls_while_processing_until_published', async () => {
    const { backend, open } = studioSetup();
    const f = await open([myVideo()], [summary({ status: 'Processing' })]);
    const poll = async (ms: number, status: UploadSummary['status'] = 'Processing') => {
      await tick(ms);
      backend.expectOne('/api/me/uploads').flush([summary({ status })]);
    };

    // Every 5 s for the first minute: 12 polls.
    for (let i = 0; i < 12; i++) {
      await tick(4999);
      backend.expectNone('/api/me/uploads');
      await poll(1);
    }
    // Then every 15 s, with no cap: still polling after 10 minutes.
    await tick(14_999);
    backend.expectNone('/api/me/uploads');
    await poll(1);
    for (let i = 0; i < 40; i++) {
      await poll(15_000);
    }

    // A terminal status stops the polling and refreshes the videos (the thumbnail and status changed).
    await poll(15_000, 'Published');
    backend.expectOne('/api/me/videos').flush([myVideo({ status: 'Published' })]);
    await tick(60_000);
    backend.expectNone('/api/me/uploads');
    f.detectChanges();
    expect(q(f, 'upload-status')!.textContent).toContain('Published');
  });

  it('Studio_blocks_a_second_upload_while_one_is_in_flight', async () => {
    const { uploader, open } = studioSetup();
    uploader.upload.mockImplementation((_id: string, _file: File, progress: (s: number, t: number) => void) => {
      progress(5, 25);
      return new Promise(() => {});
    });
    const f = await open([myVideo()], []);

    const input = q(f, 'upload-file') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [file()] });
    input.dispatchEvent(new Event('change'));
    await tick();
    f.detectChanges();

    expect(q(f, 'upload-status')!.textContent).toContain('Uploading');
    expect(q(f, 'upload-progress')).not.toBeNull();
    expect(q(f, 'upload-file')).toBeNull();
    expect(q(f, 'resume-file')).toBeNull();
  });

  it('Studio_aborts_an_upload_when_destroyed', async () => {
    const { backend, uploader, open } = studioSetup();
    let signal: AbortSignal | undefined;
    uploader.upload.mockImplementation((_id: string, _file: File, _p: unknown, s: AbortSignal) => {
      signal = s;
      return new Promise(() => {});
    });
    const f = await open([myVideo()], []);
    const input = q(f, 'upload-file') as HTMLInputElement;
    Object.defineProperty(input, 'files', { value: [file()] });
    input.dispatchEvent(new Event('change'));
    await tick();

    f.destroy();

    expect(signal?.aborted).toBe(true);
    backend.verify();
  });

  it('Studio_stops_polling_when_destroyed', async () => {
    const { backend, open } = studioSetup();
    const f = await open([myVideo()], [summary({ status: 'Processing' })]);

    f.destroy();
    await tick(60_000);

    backend.expectNone('/api/me/uploads');
  });
});

describe('player', () => {
  beforeEach(() => {
    hls.supported = true;
    hls.failToLoad = false;
    hls.instances.length = 0;
    TestBed.configureTestingModule({});
  });

  /** hls.js loads with `import()`, so the player is ready one `whenStable` later. */
  async function player(src = 'http://localhost:4566/newstore-media/hls/v1/master.m3u8') {
    const f = TestBed.createComponent(VideoPlayerComponent);
    f.componentRef.setInput('src', src);
    f.detectChanges();
    await f.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));
    f.detectChanges();
    return f;
  }

  it('Player_loads_the_playback_url_with_hls_js_and_destroys_it', async () => {
    const f = await player();

    expect(hls.instances).toHaveLength(1);
    expect(hls.instances[0]['loadSource']).toHaveBeenCalledWith('http://localhost:4566/newstore-media/hls/v1/master.m3u8');
    expect(hls.instances[0]['attachMedia']).toHaveBeenCalledWith(q(f, 'player'));
    expect(q(f, 'player-error')).toBeNull();

    f.destroy();
    expect(hls.instances[0]['destroy']).toHaveBeenCalledTimes(1);
  });

  it('Player_uses_native_hls_when_hls_js_is_unsupported_and_errors_otherwise', async () => {
    hls.supported = false;
    vi.spyOn(HTMLMediaElement.prototype, 'canPlayType').mockReturnValue('maybe');
    const native = await player('http://x/master.m3u8');
    expect(hls.instances).toHaveLength(0);
    expect((q(native, 'player') as HTMLVideoElement).src).toBe('http://x/master.m3u8');
    expect(q(native, 'player-error')).toBeNull();

    vi.spyOn(HTMLMediaElement.prototype, 'canPlayType').mockReturnValue('');
    const none = await player();
    expect(q(none, 'player-error')).not.toBeNull();
  });

  it('Quality_menu_lists_the_variants_and_switches', async () => {
    const f = await player();
    const instance = hls.instances[0];
    const onParsed = instance['on'].mock.calls.find(([event]: [string]) => event === 'hlsManifestParsed')[1] as () => void;
    onParsed();
    f.detectChanges();

    const menu = q(f, 'quality') as HTMLSelectElement;
    expect(Array.from(menu.options).map((o) => o.textContent!.trim())).toEqual(['Auto', '360p', '720p']);

    menu.value = '1';
    menu.dispatchEvent(new Event('change'));
    expect(instance['currentLevel']).toBe(1);
    menu.value = '-1';
    menu.dispatchEvent(new Event('change'));
    expect(instance['currentLevel']).toBe(-1);
  });

  it('Player_shows_an_error_and_frees_hls_js_after_a_fatal_error', async () => {
    const f = await player();
    const instance = hls.instances[0];
    const onError = instance['on'].mock.calls.find(([event]: [string]) => event === 'hlsError')[1] as (e: string, d: { fatal: boolean }) => void;

    onError('hlsError', { fatal: false });
    f.detectChanges();
    expect(q(f, 'player-error')).toBeNull();

    onError('hlsError', { fatal: true });
    f.detectChanges();
    expect(q(f, 'player-error')).not.toBeNull();
    expect(instance['destroy']).toHaveBeenCalled();
  });

  it('Player_shows_an_error_when_the_hls_js_chunk_fails_to_load', async () => {
    hls.failToLoad = true;
    const f = await player();

    expect(q(f, 'player-error')).not.toBeNull();
    expect(hls.instances).toHaveLength(0);
  });
});
