import { expect, test } from '@playwright/test';

// NFR-L6 (T15 acceptance): a 2 000-section document loads its first screen in ≤ 3 s and scrolls smoothly.
// Opt-in (DOCHUB_PERF=1) and measured against the production build: `npm run build && npx vite preview`, then
// DOCHUB_WEB=http://localhost:4173. The document is built through the API (40 chapters × 49 sections + 40 = 2 000 nodes).
const apiBase = process.env.DOCHUB_API ?? 'http://localhost:5080';
const owner = { 'X-User-Id': '2' };
const budgetMs = Number(process.env.DOCHUB_FIRST_SCREEN_MS ?? 3000);

test('a 2 000-section document shows its first screen within the budget and scrolls to the end', async ({
  page,
  request,
}) => {
  test.skip(!process.env.DOCHUB_PERF, 'Performance run: set DOCHUB_PERF=1 (against the production build)');
  test.setTimeout(600_000);
  // DOCHUB_PERF_DOC reuses an existing 2 000-section document (building one takes a minute).
  const reuse = Number(process.env.DOCHUB_PERF_DOC ?? 0);
  const doc = reuse
    ? { id: reuse, draftVersionId: 0 }
    : await (
        await request.post(`${apiBase}/api/documents`, {
          headers: owner,
          data: { folderId: 1, title: `Perf 2000 ${Date.now().toString(36)}` },
        })
      ).json();
  const add = async (title: string, parentNodeId: number | null) =>
    (await (
      await request.post(`${apiBase}/api/versions/${doc.draftVersionId}/nodes`, {
        headers: owner,
        data: { title, nodeTypeId: parentNodeId ? 2 : 1, parentNodeId },
      })
    ).json()) as {
      id: number;
    };
  for (let c = 1; c <= (reuse ? 0 : 40); c++) {
    const chapter = await add(`Chapter ${c}`, null);
    for (let start = 1; start <= 49; start += 7) {
      await Promise.all(
        Array.from({ length: Math.min(7, 50 - start) }, (_, i) => add(`Section ${c}.${start + i}`, chapter.id)),
      );
    }
  }

  await page.goto('/');
  await page.evaluate(() => localStorage.setItem('dochub.actingUserId', '2'));
  const started = Date.now();
  await page.goto(`/documents/${doc.id}`);
  await expect(page.getByTestId('document-title')).toBeVisible();
  await expect(page.locator('.dh-page-section').first()).toBeVisible();
  await expect(page.getByLabel('Section text').first()).toBeVisible();
  await expect(page.getByRole('treeitem').first()).toBeVisible();
  const firstScreen = Date.now() - started;
  console.log(`first screen: ${firstScreen} ms`);
  expect(firstScreen).toBeLessThanOrEqual(budgetMs);

  // Only sections near the viewport are mounted.
  expect(await page.locator('.dh-page-section').count()).toBeLessThan(40);

  // Jumping to the last section from the tree keeps the page responsive.
  const jump = Date.now();
  await page.getByRole('treeitem').first().click();
  await page.keyboard.press('End'); // the tree is virtualized too: keyboard navigation reaches rows not mounted yet
  await expect(page.locator('.dh-page-section', { hasText: 'Section 40.49' })).toBeVisible();
  console.log(`jump to the end: ${Date.now() - jump} ms`);
  expect(Date.now() - jump).toBeLessThan(budgetMs);

  // Scrolling frames: measured with requestAnimationFrame while scrolling the page.
  const worstFrame = await page.getByTestId('page-scroll').evaluate(async (element) => {
    let last = performance.now();
    let worst = 0;
    for (let i = 0; i < 60; i++) {
      element.scrollTop -= 400;
      await new Promise((resolve) => requestAnimationFrame(resolve));
      const now = performance.now();
      worst = Math.max(worst, now - last);
      last = now;
    }

    return worst;
  });
  console.log(`worst scroll frame: ${worstFrame.toFixed(0)} ms`);
  expect(worstFrame).toBeLessThan(250);
});
