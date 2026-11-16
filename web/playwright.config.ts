import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  retries: 0,
  use: {
    baseURL: 'http://localhost:4200',
    headless: true,
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: { browserName: 'chromium' },
    },
  ],
  webServer: [
    {
      command: 'dotnet run --project ../src/SealedTransfer.Api --no-build',
      // Playwright considers 2xx and 3xx as "ready". The health endpoint returns 404
      // for a zero GUID, so instead we match the stdout message.
      port: 5181,
      reuseExistingServer: !process.env['CI'],
      timeout: 30_000,
    },
    {
      command: 'npx ng serve --port 4200',
      port: 4200,
      reuseExistingServer: !process.env['CI'],
      timeout: 120_000,
    },
  ],
});
