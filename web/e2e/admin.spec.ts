import { execFileSync } from 'node:child_process';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

// T17 acceptance: the Admin tab. Needs the running stack; SQL steps use `docker exec dochub-sql` (skipped without it).
const apiBase = process.env.DOCHUB_API ?? 'http://localhost:5080';
const [Admin, Alice] = [1, 2];
const as = (userId: number) => ({ 'X-User-Id': String(userId) });
const suffix = () => Date.now().toString(36).toUpperCase();

function sql(query: string): boolean {
  const password = process.env.MSSQL_SA_PASSWORD ?? 'ChangeMe_Str0ng!Passw0rd';
  try {
    execFileSync('docker', [
      'exec',
      'dochub-sql',
      '/opt/mssql-tools18/bin/sqlcmd',
      '-S',
      'localhost',
      '-U',
      'sa',
      '-P',
      password,
      '-C',
      '-I',
      '-d',
      'DocHub',
      '-b',
      '-Q',
      `SET NOCOUNT ON; ${query}`,
    ]);
    return true;
  } catch {
    return false;
  }
}

async function openAs(page: Page, userId: number, url: string) {
  await page.goto('/');
  await page.evaluate((id) => localStorage.setItem('dochub.actingUserId', String(id)), userId);
  await page.goto(url);
}

async function newDocument(request: APIRequestContext, folderId = 1) {
  return (await (
    await request.post(`${apiBase}/api/documents`, {
      headers: as(Alice),
      data: { folderId, title: `Admin E2E ${suffix()}` },
    })
  ).json()) as { id: number; draftVersionId: number };
}

test('non-admins see no Admin tab and get "Access denied" on /admin', async ({ page }) => {
  await openAs(page, Alice, '/admin');
  await expect(page.getByRole('alert')).toHaveText(/Access denied/);
  await expect(page.getByRole('tab', { name: 'Admin' })).toHaveCount(0);
});

test('node types: create, edit, deactivate (not offered in the editor), delete only when unused', async ({
  page,
  request,
}) => {
  const code = `E2E${suffix()}`;
  await openAs(page, Admin, '/admin?tab=node-types');
  await page.getByRole('button', { name: 'Add node type' }).click();
  let dialog = page.getByRole('dialog', { name: 'New node type' });
  await dialog.getByLabel('Code').fill(code);
  await dialog.getByLabel('Name').fill('E2E type');
  await dialog.getByRole('button', { name: 'Save' }).click();
  const row = page.getByTestId(`node-type-${code}`);
  await expect(row).toContainText('E2E type');

  // Duplicate code: the error is shown on the field.
  await page.getByRole('button', { name: 'Add node type' }).click();
  dialog = page.getByRole('dialog', { name: 'New node type' });
  await dialog.getByLabel('Code').fill(code);
  await dialog.getByLabel('Name').fill('Duplicate');
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog.getByText(/already used|already exists|duplicate/i)).toBeVisible();
  await dialog.getByRole('button', { name: 'Cancel' }).click();

  await row.getByRole('button', { name: 'Edit' }).click();
  dialog = page.getByRole('dialog', { name: 'Edit node type' });
  await dialog.getByLabel('Name').fill('E2E type renamed');
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(row).toContainText('E2E type renamed');

  // In use → delete disabled; deactivated → not offered for new nodes.
  const doc = await newDocument(request);
  const types: { id: number; code: string }[] = await (
    await request.get(`${apiBase}/api/node-types`, { headers: as(Admin) })
  ).json();
  const typeId = types.find((t) => t.code === code)?.id;
  const node = await (
    await request.post(`${apiBase}/api/versions/${doc.draftVersionId}/nodes`, {
      headers: as(Alice),
      data: { title: 'Uses it', nodeTypeId: typeId },
    })
  ).json();
  await page.reload();
  await expect(row.getByRole('button', { name: 'Delete' })).toBeDisabled();
  await row.getByRole('switch', { name: /Deactivate/ }).click({ force: true });
  await expect(row).toHaveAttribute('data-inactive', 'true');

  await openAs(page, Alice, `/documents/${doc.id}`);
  await page.getByRole('button', { name: '+ Node' }).click();
  await page.getByRole('dialog', { name: 'New node' }).getByRole('combobox', { name: 'Type' }).click();
  await expect(page.getByRole('option', { name: 'Section', exact: true })).toBeVisible();
  await expect(page.getByRole('option', { name: 'E2E type renamed' })).toHaveCount(0);
  await page.keyboard.press('Escape');

  // Unused again → delete works.
  await request.delete(`${apiBase}/api/nodes/${node.id}?rowVersion=${encodeURIComponent(node.rowVersion)}`, {
    headers: as(Alice),
  });
  await openAs(page, Admin, '/admin?tab=node-types');
  await row.getByRole('button', { name: 'Delete' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Delete' }).click();
  await expect(row).toHaveCount(0);
});

test('users reflect the seed', async ({ page }) => {
  await openAs(page, Admin, '/admin?tab=users');
  await expect(page.getByText('Users are managed by seed data.')).toBeVisible();
  for (const login of ['admin', 'alice', 'bob', 'carol', 'dave', 'erin']) {
    await expect(page.getByTestId(`user-${login}`)).toBeVisible();
  }

  await expect(page.getByTestId('user-admin')).toContainText('Yes');
});

test('changing Heading1 font size shows in an open editor after refresh', async ({ page, request }) => {
  const styles: {
    id: number;
    styleId: string;
    name: string;
    properties: Record<string, unknown>;
    rowVersion: string;
    basedOnStyleId: string | null;
  }[] = await (await request.get(`${apiBase}/api/content-styles`, { headers: as(Admin) })).json();
  const heading = styles.find((s) => s.styleId === 'Heading1');
  const original = heading?.properties ?? {};
  const doc = await newDocument(request);
  const node = await (
    await request.post(`${apiBase}/api/versions/${doc.draftVersionId}/nodes`, {
      headers: as(Alice),
      data: { title: 'Styled', nodeTypeId: 2 },
    })
  ).json();
  const content = await (await request.get(`${apiBase}/api/nodes/${node.id}/content`, { headers: as(Alice) })).json();
  await request.put(`${apiBase}/api/nodes/${node.id}/content`, {
    headers: as(Alice),
    data: {
      contentJson: {
        type: 'doc',
        content: [{ type: 'heading', attrs: { level: 1 }, content: [{ type: 'text', text: 'Big heading' }] }],
      },
      rowVersion: content.rowVersion,
    },
  });

  const editor = await page.context().newPage();
  await openAs(editor, Alice, `/documents/${doc.id}`);
  const h1 = editor.getByTestId(`section-${node.id}`).locator('.dh-section-text h1');
  await expect(h1).toHaveText('Big heading');
  const before = await h1.evaluate((el) => getComputedStyle(el).fontSize);

  try {
    await openAs(page, Admin, '/admin?tab=styles');
    await page.getByTestId('style-Heading1').getByRole('button', { name: 'Edit' }).click();
    const dialog = page.getByRole('dialog', { name: /Edit/ });
    await dialog.getByLabel('Font size (pt)').fill('30');
    await dialog.getByRole('button', { name: 'Save' }).click();
    await expect(dialog).toBeHidden();

    await editor.reload();
    await expect(h1).toHaveText('Big heading');
    await expect.poll(() => h1.evaluate((el) => getComputedStyle(el).fontSize)).toBe('40px'); // 30pt
    expect(before).not.toBe('40px');
  } finally {
    const current = (await (await request.get(`${apiBase}/api/content-styles`, { headers: as(Admin) })).json()).find(
      (s: { styleId: string }) => s.styleId === 'Heading1',
    );
    await request.put(`${apiBase}/api/content-styles/${current.id}`, {
      headers: as(Admin),
      data: {
        name: current.name,
        basedOnStyleId: current.basedOnStyleId,
        properties: original,
        isActive: true,
        rowVersion: current.rowVersion,
      },
    });
  }
});

test('folders are managed here: create, move a deleted document out, delete the empty folder', async ({
  page,
  request,
}) => {
  const name = `Admin folder ${suffix()}`;
  await openAs(page, Admin, '/admin?tab=folders');
  await page.getByRole('button', { name: 'New folder' }).click();
  let dialog = page.getByRole('dialog', { name: 'New folder' });
  await dialog.getByLabel('Name').fill(name);
  await dialog.getByRole('button', { name: 'Save' }).click();
  const folders: { id: number; name: string }[] = await (
    await request.get(`${apiBase}/api/folders/tree`, { headers: as(Admin) })
  ).json();
  const folderId = folders.find((f) => f.name === name)?.id ?? 0;

  // A deleted document keeps the folder in use until it is moved out.
  const doc = await newDocument(request, folderId);
  const details = await (await request.get(`${apiBase}/api/documents/${doc.id}`, { headers: as(Alice) })).json();
  await request.delete(`${apiBase}/api/documents/${doc.id}?rowVersion=${encodeURIComponent(details.rowVersion)}`, {
    headers: as(Alice),
  });

  await page.reload(); // the new folder was selected (and counted) before the document existed
  await page.getByTestId(`folder-${folderId}`).getByText(name).click();
  await expect(page.getByTestId('folder-details')).toContainText('1');
  const card = page.locator('article').filter({ hasText: 'Admin E2E' });
  await expect(card).toContainText('Deleted');
  await card.getByRole('button', { name: /Actions for/ }).click();
  await page.getByRole('menuitem', { name: 'Move to…' }).click();
  await page.getByRole('dialog', { name: /Move/ }).getByRole('button', { name: 'General' }).click();
  await expect(card).toHaveCount(0);

  await page
    .getByTestId(`folder-${folderId}`)
    .getByRole('button', { name: `Actions for ${name}` })
    .click();
  await page.getByRole('menuitem', { name: 'Delete' }).click();
  dialog = page.getByRole('dialog', { name: /Delete folder/ });
  await dialog.getByRole('button', { name: 'Delete' }).click();
  await expect(page.getByTestId(`folder-${folderId}`)).toHaveCount(0);
});

test('the audit log shows a support-script change with login and ticket, and filters by source', async ({
  page,
  request,
}) => {
  const doc = await newDocument(request);
  const node = await (
    await request.post(`${apiBase}/api/versions/${doc.draftVersionId}/nodes`, {
      headers: as(Alice),
      data: { title: 'Scripted', nodeTypeId: 2 },
    })
  ).json();
  const ticket = `INC-${suffix()}`;
  test.skip(
    !sql(
      `EXEC audit.usp_SetSupportContext @Ticket = N'${ticket}', @Reason = N'e2e'; UPDATE app.NodeContent SET ContentJson = N'{"type":"doc","content":[]}' WHERE NodeId = ${node.id};`,
    ),
    'No local SQL container to run a support script',
  );

  await openAs(page, Admin, '/admin?tab=audit');
  await page.getByRole('combobox', { name: 'Source' }).click();
  await page.getByRole('option', { name: 'Script' }).click();
  await page.getByRole('textbox', { name: 'Ticket' }).fill(ticket);
  const audit = page.getByTestId('audit');
  const row = audit.locator('tr[data-source="Script"]').first();
  await expect(row).toContainText(ticket);
  await expect(row).toContainText('sa');
  await expect(row).toContainText('app.NodeContent');
  await expect(audit.locator('tr[data-source="App"]')).toHaveCount(0);
  await row.getByRole('button').first().click();
  await expect(page.getByText('Reason: e2e')).toBeVisible();
  await expect(page.locator('.dh-json').first()).toBeVisible();

  // The API allows at most 31 days: a longer range is explained, not sent.
  await page.getByRole('textbox', { name: 'From' }).fill('2020-01-01');
  await expect(page.getByText('The date range can be at most 31 days.')).toBeVisible();
});

test('tamper findings are listed and reconciliation can be run now', async ({ page }) => {
  const detail = `e2e finding ${suffix()}`;
  test.skip(
    !sql(
      `INSERT INTO audit.ReconciliationFinding (Kind, TableName, EntityId, LedgerTransactionId, Principal, Detail) VALUES ('TriggerBypass', N'app.NodeContent', 1, ${Date.now()}, N'dbo', N'${detail}');`,
    ),
    'No local SQL container to add a finding',
  );
  await openAs(page, Admin, '/admin?tab=audit');
  const finding = page.getByTestId('findings').locator('tr', { hasText: detail });
  await expect(finding).toContainText('TriggerBypass');
  await expect(finding).toContainText('dbo');
  await page.getByRole('button', { name: 'Run reconciliation now' }).click();
  await expect(page.getByText('Reconciliation finished')).toBeVisible({ timeout: 30_000 });
});

test('a pasted heading takes no explicit style from the fallback class, so changing its level restyles it', async ({
  page,
  request,
}) => {
  const doc = await newDocument(request);
  const node = await (
    await request.post(`${apiBase}/api/versions/${doc.draftVersionId}/nodes`, {
      headers: as(Alice),
      data: { title: 'Paste', nodeTypeId: 2 },
    })
  ).json();
  await openAs(page, Alice, `/documents/${doc.id}`);
  const text = page.getByTestId(`section-${node.id}`).getByLabel('Section text');
  await text.click();
  await text.evaluate((element) => {
    const data = new DataTransfer();
    data.setData('text/html', '<h1 class="ds-style-Heading1">Alpha</h1>'); // what copying a heading from the editor puts on the clipboard
    element.dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
  });
  await page.keyboard.press('Control+Alt+2');
  await expect(page.getByTestId('save-status')).toContainText('Saved', { timeout: 10_000 });
  const stored = JSON.stringify(
    (await (await request.get(`${apiBase}/api/nodes/${node.id}/content`, { headers: as(Alice) })).json()).contentJson,
  );
  expect(stored).toContain('"level":2');
  expect(stored).not.toContain('styleId');
  await expect(text.locator('h2')).toHaveClass(/ds-style-Heading2/);
});

test('admin details: whole seed incl. inactive users, folder path once, stored line spacing shown, inherited bold switched off', async ({
  page,
  request,
}) => {
  await openAs(page, Admin, '/admin?tab=users');
  await expect(page.getByTestId('user-system')).toContainText('No'); // inactive

  await page.getByRole('tab', { name: 'Folders' }).click();
  await page.getByTestId('folder-1').getByText('General').click();
  await expect(page.getByTestId('folder-details')).toContainText(/Path\s*General\s*Documents/);

  await page.getByRole('tab', { name: 'Content styles' }).click();
  await page.getByTestId('style-Normal').getByRole('button', { name: 'Edit' }).click();
  await expect(page.getByRole('dialog').getByRole('combobox', { name: 'Line spacing' })).toHaveValue(
    '1.08 (Word default)',
  );
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click();

  const styleId = `E2eChar${suffix()}`;
  await page.getByRole('button', { name: 'New character style' }).click();
  const dialog = page.getByRole('dialog', { name: 'New character style' });
  await dialog.getByLabel('Style id').fill(styleId);
  await dialog.getByLabel('Name').fill('E2E not bold');
  await dialog.getByRole('combobox', { name: 'Based on' }).click();
  await page.getByRole('option', { name: 'Strong' }).click();
  await dialog.getByRole('combobox', { name: 'Bold' }).click();
  await page.getByRole('option', { name: 'Off' }).click();
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect(dialog).toBeHidden();
  const styles: { id: number; styleId: string; properties: Record<string, unknown>; rowVersion: string }[] = await (
    await request.get(`${apiBase}/api/content-styles`, { headers: as(Admin) })
  ).json();
  const created = styles.find((s) => s.styleId === styleId);
  expect(created?.properties).toEqual({ bold: false });
  await expect(page.getByTestId(`style-${styleId}`).locator(`.ds-style-${styleId}`)).toHaveCSS('font-weight', '400');
  await request.delete(
    `${apiBase}/api/content-styles/${created?.id}?rowVersion=${encodeURIComponent(created?.rowVersion ?? '')}`,
    { headers: as(Admin) },
  );
});

test('deleting the selected folder clears the Folders view without an error', async ({ page, request }) => {
  const created = await (
    await request.post(`${apiBase}/api/folders`, {
      headers: as(Admin),
      data: { parentFolderId: null, name: `To delete ${suffix()}` },
    })
  ).json();
  await openAs(page, Admin, '/admin?tab=folders');
  await page.getByTestId(`folder-${created.id}`).getByText(created.name).click();
  await expect(page.getByTestId('folder-details')).toBeVisible();
  await page
    .getByTestId(`folder-${created.id}`)
    .getByRole('button', { name: `Actions for ${created.name}` })
    .click();
  await page.getByRole('menuitem', { name: 'Delete' }).click();
  await page
    .getByRole('dialog', { name: /Delete folder/ })
    .getByRole('button', { name: 'Delete' })
    .click();
  await expect(page.getByTestId('folder-details')).toHaveCount(0);
  await expect(page.getByText(/was not found/)).toHaveCount(0);
});

test.describe('audit log in a time zone with daylight saving', () => {
  test.use({ timezoneId: 'Europe/Berlin' });

  test('a 31-day range across the October change is accepted by the API', async ({ page }) => {
    await openAs(page, Admin, '/admin?tab=audit');
    const answers: number[] = [];
    page.on('response', (r) => r.url().includes('/api/admin/audit?') && answers.push(r.status()));
    await page.getByRole('textbox', { name: 'From' }).fill('2026-10-01');
    await page.getByRole('textbox', { name: 'To' }).fill('2026-10-31');
    await expect.poll(() => answers.at(-1)).toBe(200);
    await expect(page.getByText(/at most 31 days/)).toHaveCount(0);
  });
});
