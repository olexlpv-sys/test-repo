import { readFile } from 'node:fs/promises';
import { expect, test, type APIRequestContext, type Download, type Page } from '@playwright/test';

// T20 acceptance: export a document (and a section) to PDF from the document page; the file downloads and is a valid PDF
// with the expected page count. Needs the running stack with the export worker (Chromium) and blob storage (Azurite).
const apiBase = process.env.DOCHUB_API ?? 'http://localhost:5080';
const Alice = 2;
const as = (userId: number) => ({ 'X-User-Id': String(userId) });

async function newDocument(request: APIRequestContext, titles: string[]) {
  const title = `T20 ${Date.now().toString(36)}`;
  const doc = await (
    await request.post(`${apiBase}/api/documents`, { headers: as(Alice), data: { folderId: 1, title } })
  ).json();
  for (const title of titles) {
    const node = await (
      await request.post(`${apiBase}/api/versions/${doc.draftVersionId}/nodes`, {
        headers: as(Alice),
        data: { title, nodeTypeId: 2, parentNodeId: null },
      })
    ).json();
    const current = await (await request.get(`${apiBase}/api/nodes/${node.id}/content`, { headers: as(Alice) })).json();
    await request.put(`${apiBase}/api/nodes/${node.id}/content`, {
      headers: as(Alice),
      data: {
        contentJson: {
          type: 'doc',
          content: [{ type: 'paragraph', content: [{ type: 'text', text: `${title} text.` }] }],
        },
        rowVersion: current.rowVersion,
      },
    });
  }

  return { ...(doc as { id: number; draftVersionId: number }), title };
}

async function openAs(page: Page, userId: number, url: string) {
  await page.goto('/');
  await page.evaluate((id) => localStorage.setItem('dochub.actingUserId', String(id)), userId);
  await page.goto(url);
}

/** The PDF's bytes after checking the header; its page count from the page tree. */
async function pdfOf(download: Download) {
  const bytes = await readFile(await download.path());
  expect(bytes.subarray(0, 5).toString('latin1')).toBe('%PDF-');
  const text = bytes.toString('latin1');
  const pages = text.match(/\/Type\s*\/Page(?![s\w])/g)?.length ?? 0;
  return { bytes, pages };
}

test('export the whole document to PDF: progress toast, automatic download, valid PDF with the expected pages', async ({
  page,
  request,
}) => {
  test.setTimeout(90_000);
  const doc = await newDocument(request, ['Scope', 'Terms']);
  await openAs(page, Alice, `/documents/${doc.id}`);
  await expect(page.getByTestId('document-title')).toHaveText(doc.title);

  await page.getByRole('button', { name: 'Export PDF' }).click();
  const dialog = page.getByRole('dialog', { name: 'Export PDF' });
  await expect(dialog.getByRole('radio', { name: 'Whole document (Draft)' })).toBeChecked();
  const downloading = page.waitForEvent('download', { timeout: 60_000 });
  await dialog.getByRole('button', { name: 'Export' }).click();
  await expect(page.getByText('Exporting Draft')).toBeVisible();

  const download = await downloading;
  expect(download.suggestedFilename()).toBe(`${doc.title} - Draft.pdf`);
  const pdf = await pdfOf(download);
  // Title page, table of contents, and both short sections on one page.
  expect(pdf.pages).toBe(3);
  await expect(page.getByText('PDF ready')).toBeVisible();
});

test('export a section from its menu: only that section, without the title page', async ({ page, request }) => {
  test.setTimeout(90_000);
  const doc = await newDocument(request, ['Scope', 'Terms']);
  await openAs(page, Alice, `/documents/${doc.id}`);

  await page.getByRole('button', { name: 'Actions for Terms' }).click();
  await page.getByRole('menuitem', { name: 'Export this section to PDF' }).click();
  const dialog = page.getByRole('dialog', { name: 'Export PDF' });
  await expect(dialog.getByRole('radio', { name: /Section 2 Terms/ })).toBeChecked();
  await dialog.getByRole('checkbox', { name: 'Title page' }).uncheck();
  const downloading = page.waitForEvent('download', { timeout: 60_000 });
  await dialog.getByRole('button', { name: 'Export' }).click();

  const download = await downloading;
  expect(download.suggestedFilename()).toBe(`${doc.title} - Terms - Draft.pdf`);
  // Table of contents and the section.
  expect((await pdfOf(download)).pages).toBe(2);
});
