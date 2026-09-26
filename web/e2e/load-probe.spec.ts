import { expect, test } from '@playwright/test';

// NFR-L6 under load (T19): while the load run targets the same stack (tests/DocHub.LoadTests with DOCHUB_LOAD_TARGET), 20 probes
// measure "main window ready" (folder tree and document list shown) and "document form first section visible"; the p95 of each
// must be ≤ 3 s. Opt-in: DOCHUB_LOAD_PROBE=1, against the generated database (tools/DocHub.DataGen) and the production build.
const apiBase = process.env.DOCHUB_API ?? 'http://localhost:5080';
const probes = Number(process.env.DOCHUB_PROBES ?? 20);
const budgetMs = Number(process.env.DOCHUB_PROBE_BUDGET_MS ?? 3000);

const p95 = (values: number[]) => {
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.ceil(0.95 * sorted.length) - 1)] ?? 0;
};

test('main window and first document section are ready within 3 s (p95) under load', async ({ page, request }) => {
  test.skip(!process.env.DOCHUB_LOAD_PROBE, 'Load probe: set DOCHUB_LOAD_PROBE=1 while a load run targets this stack');
  test.setTimeout(probes * 30_000);

  // A generated reader and the generated documents of the first folder that has some.
  const users = await (
    await request.get(`${apiBase}/api/users?Search=Reader&PageSize=1`, { headers: { 'X-User-Id': '1' } })
  ).json();
  const reader = String(users.items[0].id);
  const folders: { id: number; name: string; documentCount: number }[] = await (
    await request.get(`${apiBase}/api/folders/tree`, { headers: { 'X-User-Id': reader } })
  ).json();
  // Top-level generated folders (the tree is nested; the probe opens a root folder that has documents).
  const folder = folders.find((f) => f.name.startsWith('Load folder') && f.documentCount > 0);
  expect(folder, 'generated folders').toBeTruthy();
  const list = await (
    await request.get(`${apiBase}/api/folders/${folder?.id}/documents?PageSize=50`, {
      headers: { 'X-User-Id': reader },
    })
  ).json();
  const documents: number[] = list.items.map((d: { id: number }) => d.id);
  expect(documents.length).toBeGreaterThan(0);

  await page.goto('/');
  await page.evaluate((id) => localStorage.setItem('dochub.actingUserId', id), reader);
  const main: number[] = [];
  const form: number[] = [];
  for (let i = 0; i < probes; i++) {
    let started = Date.now();
    await page.goto(`/?folder=${folder?.id}`);
    await expect(page.getByTestId(`folder-${folder?.id}`)).toBeVisible();
    await expect(page.locator('article').first()).toBeVisible();
    main.push(Date.now() - started);

    started = Date.now();
    await page.goto(`/documents/${documents[i % documents.length]}`);
    await expect(page.getByTestId('document-title')).toBeVisible();
    await expect(page.getByLabel('Section text').first()).toBeVisible();
    form.push(Date.now() - started);
  }

  console.log(`NFR-L6 probe: main window p95 ${p95(main)} ms, first section p95 ${p95(form)} ms (${probes} probes)`);
  expect(p95(main), 'main window ready, p95').toBeLessThanOrEqual(budgetMs);
  expect(p95(form), 'document form first section, p95').toBeLessThanOrEqual(budgetMs);
});
