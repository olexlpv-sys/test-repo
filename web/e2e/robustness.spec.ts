import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

// Editor robustness (Q16 review round 1): no lost edits, no invalid saves, conflicts must be decided, fresh text for readers.
const apiBase = process.env.DOCHUB_API ?? 'http://localhost:5080';
const [Alice, Carol] = [2, 4];
const as = (userId: number) => ({ 'X-User-Id': String(userId) });
const paragraph = (text: string) => ({
  type: 'doc',
  content: [{ type: 'paragraph', content: [{ type: 'text', text }] }],
});

async function newSection(request: APIRequestContext, texts: string[]) {
  const doc = await (
    await request.post(`${apiBase}/api/documents`, {
      headers: as(Alice),
      data: { folderId: 1, title: `Robust ${Date.now().toString(36)}` },
    })
  ).json();
  const node = await (
    await request.post(`${apiBase}/api/versions/${doc.draftVersionId}/nodes`, {
      headers: as(Alice),
      data: { title: 'Text', nodeTypeId: 2 },
    })
  ).json();
  for (const text of texts) {
    await setText(request, node.id, text);
  }

  return { doc, node };
}

async function setText(request: APIRequestContext, nodeId: number, text: string) {
  const current = await (await request.get(`${apiBase}/api/nodes/${nodeId}/content`, { headers: as(Alice) })).json();
  expect(
    (
      await request.put(`${apiBase}/api/nodes/${nodeId}/content`, {
        headers: as(Alice),
        data: { contentJson: paragraph(text), rowVersion: current.rowVersion },
      })
    ).ok(),
  ).toBe(true);
}

const stored = async (request: APIRequestContext, nodeId: number) =>
  JSON.stringify(
    (await (await request.get(`${apiBase}/api/nodes/${nodeId}/content`, { headers: as(Alice) })).json()).contentJson,
  );

async function openAs(page: Page, userId: number, url: string) {
  await page.goto('/');
  await page.evaluate((id) => localStorage.setItem('dochub.actingUserId', String(id)), userId);
  await page.goto(url);
}

test('a restored text is applied once: later edits survive viewing history again', async ({ page, request }) => {
  const { doc, node } = await newSection(request, ['Text 0.', 'Text 1.']);
  await openAs(page, Alice, `/documents/${doc.id}`);
  const section = page.getByTestId(`section-${node.id}`);
  await section.locator('.dh-history-toggle').click();
  await section
    .locator('.dh-timeline-item', { hasText: 'content' })
    .last()
    .getByRole('button', { name: 'Restore this text' })
    .click();
  await expect(section.getByTestId('save-status')).toContainText('Saved', { timeout: 10_000 });

  await section.getByLabel('Section text').click();
  await page.keyboard.press('End');
  await page.keyboard.type(' LATER EDIT');
  await expect(section.getByTestId('save-status')).toContainText('Saved', { timeout: 10_000 });
  await expect.poll(() => stored(request, node.id)).toContain('LATER EDIT');

  await section.getByRole('button', { name: 'View' }).first().click();
  await section.getByRole('button', { name: 'Back to current text' }).click();
  await expect(section.getByLabel('Section text')).toContainText('LATER EDIT');
  await page.waitForTimeout(2500);
  expect(await stored(request, node.id)).toContain('LATER EDIT');
});

test('an edit typed while a save is in flight is saved when the user leaves', async ({ page, request }) => {
  const { doc, node } = await newSection(request, ['Text 0.']);
  await openAs(page, Alice, `/documents/${doc.id}`);
  // A slow network: every content save takes 1.5 s.
  await page.route(`**/api/nodes/${node.id}/content`, async (route) => {
    if (route.request().method() === 'PUT') {
      await new Promise((resolve) => setTimeout(resolve, 1500));
    }

    await route.continue();
  });
  const text = page.getByTestId(`section-${node.id}`).getByLabel('Section text');
  await text.click();
  await page.keyboard.press('End');
  await page.keyboard.type(' one');
  await expect(page.getByTestId('save-status')).toContainText('Saving', { timeout: 5000 });
  await page.keyboard.type(' two');
  await page.getByRole('button', { name: '← Back' }).click();
  await expect.poll(() => stored(request, node.id), { timeout: 10_000 }).toContain('Text 0. one two');
});

test('the editor only builds content the schema accepts: no table or page break inside a table cell', async ({
  page,
  request,
}) => {
  const { doc, node } = await newSection(request, []);
  await openAs(page, Alice, `/documents/${doc.id}`);
  await page.getByTestId(`section-${node.id}`).getByLabel('Section text').click();
  await page.getByRole('button', { name: '⊞ Table' }).click();
  await page.getByRole('button', { name: '2 by 2' }).click();
  await page.keyboard.type('Cell');
  // Inside the cell there is nothing to insert a table or a page break with.
  await expect(page.getByRole('button', { name: '⊞ Table' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: '⤓ Page break' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: '🔗 Link' })).toBeVisible(); // links are allowed in cells
  // Pasting a table into a cell still yields valid content (ProseMirror places it where the schema allows).
  await page
    .getByTestId(`section-${node.id}`)
    .getByLabel('Section text')
    .evaluate((element) => {
      const data = new DataTransfer();
      data.setData('text/html', '<table><tr><td>Pasted</td></tr></table><hr>');
      element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
    });
  await expect(page.getByTestId('save-status')).toContainText('Saved', { timeout: 10_000 });
  expect(await stored(request, node.id)).toContain('Pasted');
});

test('the conflict dialog cannot be dismissed without a decision; after it, edits are saved again', async ({
  page,
  request,
}) => {
  const { doc, node } = await newSection(request, ['Start.']);
  await openAs(page, Alice, `/documents/${doc.id}`);
  const text = page.getByTestId(`section-${node.id}`).getByLabel('Section text');
  await text.click();
  await page.keyboard.press('End');
  await page.keyboard.type(' Mine.');
  await setText(request, node.id, 'Saved elsewhere.'); // another session saves before the local changes do
  const dialog = page.getByRole('dialog', { name: 'Changed by someone else' });
  await expect(dialog).toBeVisible({ timeout: 10_000 });
  await page.keyboard.press('Escape');
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Overwrite' }).click();
  await expect(page.getByTestId('save-status')).toContainText('Saved', { timeout: 10_000 });
  await expect.poll(() => stored(request, node.id)).toContain('Start. Mine.');
  await text.click();
  await page.keyboard.press('End');
  await page.keyboard.type(' Again.');
  await expect.poll(() => stored(request, node.id), { timeout: 10_000 }).toContain('Again.');
});

test('an approver sees text someone else saved meanwhile when opening Sign', async ({ page, request }) => {
  const { doc, node } = await newSection(request, ['Text 0.']);
  await request.post(`${apiBase}/api/documents/${doc.id}/permissions`, {
    headers: as(Alice),
    data: { userId: Carol, role: 'Approver' },
  });
  await openAs(page, Carol, `/documents/${doc.id}`);
  const text = page.getByTestId(`section-${node.id}`).getByLabel('Section text');
  await expect(text).toHaveText('Text 0.');
  await setText(request, node.id, 'Changed by Alice.');
  await page.getByRole('button', { name: 'Sign', exact: true }).click(); // opening Sign reloads the texts
  await expect(text).toHaveText('Changed by Alice.', { timeout: 10_000 });
});
