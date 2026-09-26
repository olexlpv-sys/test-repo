import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

// T15 acceptance: formatting round-trip and structure editing through the UI. Needs the running stack.
const apiBase = process.env.DOCHUB_API ?? 'http://localhost:5080';
const Alice = 2;
const owner = { 'X-User-Id': String(Alice) };

async function newDocument(request: APIRequestContext, sections: string[]) {
  const created = (await (
    await request.post(`${apiBase}/api/documents`, {
      headers: owner,
      data: { folderId: 1, title: `E2E ${Date.now().toString(36)}` },
    })
  ).json()) as {
    id: number;
    draftVersionId: number;
  };
  const nodes: { id: number }[] = [];
  for (const title of sections) {
    nodes.push(
      await (
        await request.post(`${apiBase}/api/versions/${created.draftVersionId}/nodes`, {
          headers: owner,
          data: { title, nodeTypeId: 1 },
        })
      ).json(),
    );
  }

  return { ...created, nodes };
}

async function openAs(page: Page, userId: number, url: string) {
  await page.goto('/');
  await page.evaluate((id) => localStorage.setItem('dochub.actingUserId', String(id)), userId);
  await page.goto(url);
}

async function pick(page: Page, label: string, option: string) {
  await page.getByRole('combobox', { name: label, exact: true }).click();
  await page.getByRole('option', { name: option, exact: true }).click();
}

async function saved(page: Page) {
  await expect(page.getByTestId('save-status').first()).toContainText('Saved', { timeout: 10_000 });
}

test('formatting is autosaved in the schema and survives a reload', async ({ page, request }) => {
  test.setTimeout(90_000);
  const doc = await newDocument(request, ['Terms']);
  const nodeId = doc.nodes[0]?.id ?? 0;
  await openAs(page, Alice, `/documents/${doc.id}`);

  const text = page.getByLabel('Section text').first();
  await text.click();
  await page.keyboard.type('Definitions');
  await pick(page, 'Style', 'Heading 2');
  await text.click();
  await page.keyboard.press('End');
  await page.keyboard.press('Enter');
  await page.keyboard.type('Styled words');
  await page.keyboard.press('Shift+Home');
  await pick(page, 'Font', 'Arial');
  await pick(page, 'Font size', '14');
  await page.getByRole('button', { name: 'Font color' }).click();
  await page.getByRole('button', { name: 'Font color #C00000' }).click();
  await page.getByRole('button', { name: '↕ Spacing' }).click();
  await page.getByRole('menuitem', { name: '1.5' }).click();

  await text.click();
  await page.keyboard.press('End'); // collapse the formatted selection first
  await page.keyboard.press('ControlOrMeta+End');
  await page.keyboard.press('Enter');
  await page.getByRole('button', { name: 'Numbering' }).click();
  await page.keyboard.type('First');
  await page.keyboard.press('Enter');
  await page.keyboard.type('Second');
  await page.keyboard.press('Enter');
  await page.keyboard.press('Enter'); // leave the list

  await page.getByRole('button', { name: '⊞ Table' }).click();
  await page.getByRole('button', { name: '3 by 3' }).click();
  await page.keyboard.type('A1');
  // Two cells selected by dragging across them, as a user does.
  const cells = page.locator('.dh-section-text td');
  const [from, to] = [await cells.nth(0).boundingBox(), await cells.nth(1).boundingBox()];
  await page.mouse.move((from?.x ?? 0) + 10, (from?.y ?? 0) + 8);
  await page.mouse.down();
  await page.mouse.move((to?.x ?? 0) + 20, (to?.y ?? 0) + 8, { steps: 5 });
  await page.mouse.up();
  await page.getByRole('button', { name: 'Table ▾' }).click();
  await page.getByRole('menuitem', { name: 'Merge cells' }).click();
  await page.getByRole('button', { name: 'Cell shading' }).click();
  await page.getByRole('button', { name: 'Cell shading #FFF2CC' }).click();
  await saved(page);

  const stored = await (await request.get(`${apiBase}/api/nodes/${nodeId}/content`, { headers: owner })).json();
  const json = JSON.stringify(stored.contentJson);
  expect(json).toContain('"type":"heading"');
  expect(json).toContain('"level":2');
  expect(json).toContain('"fontFamily":"Arial"');
  expect(json).toContain('"fontSize":28');
  expect(json).toContain('"color":"#C00000"');
  expect(json).toContain('"lineSpacing":360');
  expect(json).toContain('"type":"orderedList"');
  expect(json).toContain('"colspan":2');
  expect(json).toContain('"shading":"#FFF2CC"');

  await page.reload();
  await expect(page.getByLabel('Section text').first()).toContainText('Styled words');
  await page.getByLabel('Section text').first().click(); // focus + blur must not change anything
  await page.getByTestId('document-title').click();
  await page.waitForTimeout(2000);
  const after = await (await request.get(`${apiBase}/api/nodes/${nodeId}/content`, { headers: owner })).json();
  expect(after.contentJson).toEqual(stored.contentJson);
  expect(after.rowVersion).toBe(stored.rowVersion);
});

test('paste from Word keeps headings, bold and tables within the schema', async ({ page, request }) => {
  const doc = await newDocument(request, ['Pasted']);
  const nodeId = doc.nodes[0]?.id ?? 0;
  await openAs(page, Alice, `/documents/${doc.id}`);
  const text = page.getByLabel('Section text').first();
  await text.click();
  const html = `<html xmlns:o="urn:schemas-microsoft-com:office:office"><body>
    <h1 style="mso-style-name:'Heading 1'">Word heading</h1>
    <p class="MsoNormal" style="margin-left:36.0pt;line-height:150%;font-family:Calibri,sans-serif"><b>Bold part</b><span style="font-size:14.0pt;color:#FF0000;font-family:'Comic Sans MS'"> red text</span><o:p></o:p></p>
    <table class="MsoTableGrid" border="1"><tr><td style="background:#FFFF00">Cell 1</td><td>Cell 2</td></tr></table>
  </body></html>`;
  await text.evaluate((element, pasted) => {
    const data = new DataTransfer();
    data.setData('text/html', pasted);
    data.setData('text/plain', 'Word heading Bold part red text Cell 1 Cell 2');
    element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
  }, html);
  await saved(page);

  const stored = await (await request.get(`${apiBase}/api/nodes/${nodeId}/content`, { headers: owner })).json();
  const json = JSON.stringify(stored.contentJson);
  expect(json).toContain('"type":"heading"');
  expect(json).toContain('"type":"bold"');
  expect(json).toContain('"type":"table"');
  expect(json).toContain('"fontSize":28');
  expect(json).toContain('"color":"#FF0000"');
  expect(json).toContain('"indentLeft":720');
  expect(json).toContain('"lineSpacing":360');
  expect(json).toContain('"shading":"#FFFF00"');
  expect(json).not.toContain('Comic Sans'); // not in the font list: dropped, the save still valid
});

test('the FR-T1 tree is built from the UI, including a drag-and-drop move, and survives a reload', async ({
  page,
  request,
}) => {
  test.setTimeout(90_000);
  const doc = await newDocument(request, []);
  await openAs(page, Alice, `/documents/${doc.id}`);

  const add = async (title: string, parent?: string) => {
    if (parent) {
      await page
        .getByRole('treeitem', { name: new RegExp(`\\b${parent}$`) })
        .getByRole('button', { name: `Actions for ${parent}` })
        .click();
      await page.getByRole('menuitem', { name: 'Add child' }).click();
    } else {
      await page.getByRole('button', { name: '+ Node' }).click();
    }

    const dialog = page.getByRole('dialog', { name: 'New node' });
    await dialog.getByLabel('Title').fill(title);
    await dialog.getByRole('button', { name: 'Add' }).click();
    await expect(dialog).toBeHidden();
  };

  // Unique titles make the rows addressable; the shape is FR-T1's.
  await add('Chapter 1');
  await add('Chapter 2');
  await add('Section 1.1', 'Chapter 1');
  await add('Section 1.2', 'Chapter 1');
  await add('Subsection 1.2.1', 'Section 1.2');
  await add('Subsection 1.2.2', 'Section 1.2');
  await add('Section 2.1', 'Chapter 2');
  await add('Subsection 2.1.1', 'Section 2.1');
  // Built in the wrong place on purpose, then dragged under Section 2.1.
  await add('Subsection 2.1.2', 'Chapter 2');

  const row = (title: string) =>
    page.getByRole('treeitem', { name: new RegExp(`\\b${title.replace(/\./g, '\\.')}\\b`) });
  const source = row('Subsection 2.1.2');
  const target = row('Subsection 2.1.1');
  const box = await target.boundingBox();
  await source.dragTo(target, { targetPosition: { x: 40, y: (box?.height ?? 30) - 3 } }); // lower edge: after it
  await expect(row('Subsection 2.1.2')).toHaveAttribute('aria-level', '3');

  const expected = [
    '1 Chapter 1',
    '1.1 Section 1.1',
    '1.2 Section 1.2',
    '1.2.1 Subsection 1.2.1',
    '1.2.2 Subsection 1.2.2',
    '2 Chapter 2',
    '2.1 Section 2.1',
    '2.1.1 Subsection 2.1.1',
    '2.1.2 Subsection 2.1.2',
  ];
  const read = async () =>
    await page
      .locator('.dh-tree-row')
      .evaluateAll((rows) =>
        rows.map(
          (r) => `${r.querySelector('.dh-tree-number')?.textContent} ${r.querySelector('.dh-tree-title')?.textContent}`,
        ),
      );
  await expect.poll(read).toEqual(expected);
  await page.reload();
  await expect.poll(read).toEqual(expected);
});
