import { defineConfig, devices } from '@playwright/test';

// Smoke tests against a running stack: API (http://localhost:5080, test auth mode, published database) + `npm run dev`.
// DOCHUB_WEB overrides the web address; PLAYWRIGHT_CHROMIUM points at a preinstalled browser instead of a downloaded one.
export default defineConfig({
  testDir: './e2e',
  timeout: 30_000,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: process.env.DOCHUB_WEB ?? 'http://localhost:5173',
    viewport: { width: 1440, height: 900 },
    trace: 'retain-on-failure',
    launchOptions: process.env.PLAYWRIGHT_CHROMIUM ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM } : {},
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } }],
});
