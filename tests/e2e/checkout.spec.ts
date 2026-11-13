import { expect, test } from '@playwright/test';

test('a new buyer signs up, buys a copy and sees it in my copies', async ({ page }) => {
  // The realm refuses a duplicate email and the stack is not reset between retries, so every run is a new person.
  const suffix = `${Date.now()}-${Math.floor(Math.random() * 1e6)}`;
  const username = `buyer-${suffix}`;
  const password = `Pw-${suffix}`;

  // Sign up on Keycloak's page. The ids are the ones Keycloak's default registration form uses.
  await page.goto('/');
  await page.getByTestId('sign-in').click();
  await page.getByRole('link', { name: 'Register' }).click();
  await page.locator('#firstName').fill('Ada');
  await page.locator('#lastName').fill('Buyer');
  await page.locator('#email').fill(`${username}@example.test`);
  await page.locator('#username').fill(username);
  await page.locator('#password').fill(password);
  await page.locator('#password-confirm').fill(password);
  await page.getByRole('button', { name: 'Register' }).click();

  // user-name shows the token's `name` claim, "First Last".
  await expect(page.getByTestId('user-name')).toContainText('Ada');

  // The session is an HttpOnly, SameSite=Strict cookie without Secure (ADR 0015).
  const cookie = (await page.context().cookies()).find((c) => c.name === 'newstore-session');
  expect(cookie).toBeDefined();
  expect(cookie).toMatchObject({ sameSite: 'Strict', httpOnly: true, secure: false });

  // Find the video. The edition reaches Catalog through Kafka, so give it time to appear.
  await page.getByTestId('search').fill('sourdough');
  await page.getByTestId('video-card').getByRole('link', { name: 'Sourdough in Zero Gravity' }).click();
  const digital = page.getByTestId('edition').and(page.locator('[data-format=Digital]'));
  await expect(digital).toBeVisible({ timeout: 30_000 });
  await digital.getByTestId('buy').click();

  // Pay on FakePay's page; it sends the browser back to the order page.
  await page.getByTestId('pay').click();
  await page.getByTestId('fakepay-pay').click();
  await expect(page.getByTestId('copy-number')).toBeVisible({ timeout: 30_000 });
  const copyNumber = (await page.getByTestId('copy-number').textContent())?.trim();
  expect(copyNumber).toMatch(/^\d+$/);

  // The copy is in "my copies": one row, which prefixes the number with '#'.
  await page.getByTestId('my-copies-link').click();
  await expect(page.getByTestId('copy-row')).toHaveCount(1);
  await expect(page.getByTestId('copy-row')).toContainText(`#${copyNumber}`);
});
