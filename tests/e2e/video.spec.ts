import { expect, test, type Page } from '@playwright/test';

const PROCESSING_TIMEOUT = 3 * 60_000;

/** Creates a video in the studio and returns its row. The title is unique, so a retry or an earlier run never matches. */
async function createVideo(page: Page, title: string) {
  await page.getByTestId('new-video-title').fill(title);
  await page.getByTestId('create-video').click();
  const row = page.getByTestId('studio-row').filter({ hasText: title });
  await expect(row).toHaveCount(1);
  return row;
}

test('a creator uploads a video, it plays, and a broken file fails', async ({ page }) => {
  // Two waits of up to 3 minutes (the encode on a CI runner), a 30 s wait for Catalog and the playback: more than the 90 s default.
  test.setTimeout(10 * 60_000);

  // Sign in on Keycloak's form as the seeded creator; a sample user's password is its channel slug.
  await page.goto('/');
  await page.getByTestId('sign-in').click();
  await page.locator('#username').fill('orbital-kitchen');
  await page.locator('#password').fill('orbital-kitchen');
  await page.locator('#kc-login').click();
  await page.getByTestId('studio-link').click();

  // A good file: Uploading, Processing, Published.
  const row = await createVideo(page, `E2E ${Date.now()}`);
  const videoId = await row.getAttribute('data-video-id');
  expect(videoId).toBeTruthy();
  await row.getByTestId('upload-file').setInputFiles('fixtures/sample.mp4');
  await expect(row.getByTestId('upload-status')).toHaveText('Published', { timeout: PROCESSING_TIMEOUT });

  // Catalog publishes the video when it consumes VideoPublished, so its page can be a 404 for a moment.
  // The gateway refuses a cookie-authenticated call without `X-CSRF: 1` (403, ADR 0015), which the page's own calls add.
  await expect
    .poll(async () => (await page.request.get(`/api/videos/${videoId}`, { headers: { 'X-CSRF': '1' } })).status(), { timeout: 30_000 })
    .toBe(200);

  // Open the video's page and watch what the player fetches: the master playlist and a segment, both from the public bucket.
  const fetched: { url: string; status: number }[] = [];
  page.on('response', (response) => fetched.push({ url: response.url(), status: response.status() }));
  await page.goto(`/videos/${videoId}`);
  await expect.poll(() => fetched.find((r) => r.url.includes('master.m3u8'))?.status).toBe(200);
  await expect.poll(() => fetched.find((r) => /\.ts(\?|$)/.test(r.url))?.status).toBe(200);

  // Muted, because a browser refuses to start sound without a user gesture. The `chrome` project is used for its codecs (playwright.config.ts).
  // `play()` rejects until hls.js has attached its source, so the whole step retries until the video has moved.
  const player = page.getByTestId('player');
  await expect(async () => {
    await player.evaluate((video: HTMLVideoElement) => {
      video.muted = true;
      return video.play();
    });
    expect(await player.evaluate((video: HTMLVideoElement) => video.currentTime)).toBeGreaterThan(0.5);
  }).toPass({ timeout: 15_000 });

  // A broken file: random bytes are no video, so the worker fails the upload and the row says why.
  await page.getByTestId('studio-link').click();
  const broken = await createVideo(page, `E2E broken ${Date.now()}`);
  await broken.getByTestId('upload-file').setInputFiles('fixtures/broken.mp4');
  await expect(broken.getByTestId('upload-status')).toHaveText('Failed', { timeout: PROCESSING_TIMEOUT });
  // Random bytes make ffprobe fail, which the worker records as `unreadable` (ProcessingTests), and the studio words that code.
  await expect(broken.getByTestId('failure-reason')).toHaveText("The file isn't a video FFmpeg can read");
});
