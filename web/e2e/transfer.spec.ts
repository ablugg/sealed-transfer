import { test, expect } from '@playwright/test';
import * as path from 'path';
import * as fs from 'fs';
import * as os from 'os';

test.describe('Transfer flow', () => {
  let tempFile: string;

  test.beforeAll(() => {
    // Create a small temp file to upload.
    tempFile = path.join(os.tmpdir(), `sealed-transfer-e2e-${Date.now()}.txt`);
    fs.writeFileSync(tempFile, 'Hello from Playwright! This is test data for Sealed Transfer e2e.');
  });

  test.afterAll(() => {
    if (fs.existsSync(tempFile)) {
      fs.unlinkSync(tempFile);
    }
  });

  test('send page loads and shows the form', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Send a file' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Encrypt and upload' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Encrypt and upload' })).toBeDisabled();
  });

  test('selecting a file enables the upload button', async ({ page }) => {
    await page.goto('/');
    const fileInput = page.locator('input[type="file"]');
    await fileInput.setInputFiles(tempFile);

    await expect(page.getByRole('button', { name: 'Encrypt and upload' })).toBeEnabled();
  });

  test('full send and receive round trip', async ({ page, context }) => {
    // --- Send ---
    await page.goto('/');
    const fileInput = page.locator('input[type="file"]');
    await fileInput.setInputFiles(tempFile);
    await page.getByRole('button', { name: 'Encrypt and upload' }).click();

    // Wait for the share link to appear.
    await expect(page.getByRole('heading', { name: 'Your file is ready to share' })).toBeVisible({ timeout: 30_000 });
    const linkInput = page.locator('input.link');
    const shareLink = await linkInput.inputValue();
    expect(shareLink).toContain('/t/');
    expect(shareLink).toContain('#');

    // --- Receive ---
    // Extract the path from the full URL.
    const url = new URL(shareLink);
    const receivePage = await context.newPage();
    await receivePage.goto(url.pathname + url.hash);

    // Should show the file name and a download button.
    const fileName = path.basename(tempFile);
    await expect(receivePage.getByText(fileName)).toBeVisible({ timeout: 15_000 });
    await expect(receivePage.getByRole('button', { name: 'Download' })).toBeVisible();

    // Click download and wait for the download event.
    const downloadPromise = receivePage.waitForEvent('download');
    await receivePage.getByRole('button', { name: 'Download' }).click();
    const download = await downloadPromise;

    // Verify the downloaded content matches.
    const downloadPath = path.join(os.tmpdir(), `sealed-transfer-e2e-dl-${Date.now()}.txt`);
    await download.saveAs(downloadPath);
    const downloaded = fs.readFileSync(downloadPath, 'utf-8');
    expect(downloaded).toBe('Hello from Playwright! This is test data for Sealed Transfer e2e.');
    fs.unlinkSync(downloadPath);
  });

  test('blocked file type shows an error', async ({ page }) => {
    // Create a fake .exe file.
    const exeFile = path.join(os.tmpdir(), `sealed-transfer-e2e-${Date.now()}.exe`);
    // MZ header for a Windows executable.
    fs.writeFileSync(exeFile, Buffer.from([0x4d, 0x5a, 0x90, 0x00]));

    await page.goto('/');
    const fileInput = page.locator('input[type="file"]');
    await fileInput.setInputFiles(exeFile);

    await expect(page.getByText('.exe files are not allowed')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Encrypt and upload' })).toBeDisabled();

    fs.unlinkSync(exeFile);
  });

  test('revoke flow works', async ({ page }) => {
    await page.goto('/');
    const fileInput = page.locator('input[type="file"]');
    await fileInput.setInputFiles(tempFile);
    await page.getByRole('button', { name: 'Encrypt and upload' }).click();

    await expect(page.getByRole('heading', { name: 'Your file is ready to share' })).toBeVisible({ timeout: 30_000 });

    // Revoke the transfer.
    await page.getByRole('button', { name: 'Revoke' }).click();
    await expect(page.getByRole('heading', { name: 'Transfer revoked' })).toBeVisible({ timeout: 10_000 });
  });

  test('my transfers page is accessible', async ({ page }) => {
    await page.goto('/transfers');
    await expect(page.getByRole('heading', { name: /transfers/i })).toBeVisible();
  });
});
