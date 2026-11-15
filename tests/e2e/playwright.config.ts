import { defineConfig, devices } from '@playwright/test';

// Always `localhost`, never `127.0.0.1`: Keycloak accepts only the realm's `localhost` redirect URIs, and the issuer, FakePay's
// checkout URL and the session cookie's host are all `localhost`.
export default defineConfig({
  testDir: '.',
  timeout: 90_000,
  expect: { timeout: 10_000 },
  retries: process.env.CI ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: {
    baseURL: 'http://localhost:8100',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'chromium', testMatch: 'checkout.spec.ts', use: { ...devices['Desktop Chrome'] } },
    // "Chromium does not have all the codecs that Google Chrome or Microsoft Edge are bundling", and the page says to use the official
    // channel for them (https://playwright.dev/docs/browsers). The video test plays H.264, so it runs in Chrome.
    { name: 'chrome', testMatch: 'video.spec.ts', use: { ...devices['Desktop Chrome'], channel: 'chrome' } },
  ],
});
