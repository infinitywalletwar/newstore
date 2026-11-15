import { HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { BROWSER_LOCATION } from '../api/browser-location';
import { UploadState, UploadSummary } from '../api/models';
import { csrfInterceptor } from '../auth/csrf.interceptor';
import { Uploader } from './uploader';

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
