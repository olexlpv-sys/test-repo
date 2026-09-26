import { execFileSync } from 'node:child_process';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

// T15 acceptance: lifecycle, rights, conflicts, change tracking against v1, script changes. Needs the running stack.
const apiBase = process.env.DOCHUB_API ?? 'http://localhost:5080';
const [Alice, Bob, Carol, Dave] = [2, 3, 4, 5];
const as = (userId: number) => ({ 'X-User-Id': String(userId) });
const paragraph = (text: string) => ({
  type: 'doc',
  content: [{ type: 'paragraph', content: [{ type: 'text', text }] }],
});

interface Doc {
  id: number;
  draftVersionId: number;
  nodes: { id: number; logicalNodeId: string; title: string }[];
}

async function newDocument(
  request: APIRequestContext,
  sections: { title: string; text?: string; parent?: number }[],
): Promise<Doc> {
  const created = await (
    await request.post(`${apiBase}/api/documents`, {
      headers: as(Alice),
      data: { folderId: 1, title: `E2E ${Date.now().toString(36)}` },
    })
  ).json();
  const nodes: Doc['nodes'] = [];
  for (const s of sections) {
    const parentNodeId = s.parent === undefined ? null : (nodes[s.parent]?.id ?? null);
    const node = await (
      await request.post(`${apiBase}/api/versions/${created.draftVersionId}/nodes`, {
        headers: as(Alice),
        data: { title: s.title, nodeTypeId: 2, parentNodeId },
      })
    ).json();
    nodes.push(node);
    if (s.text) {
      await setText(request, node.id, s.text, Alice);
    }
  }

  return { ...created, nodes };
}

async function setText(request: APIRequestContext, nodeId: number, text: string, userId: number) {
  const current = await (await request.get(`${apiBase}/api/nodes/${nodeId}/content`, { headers: as(userId) })).json();
  const saved = await request.put(`${apiBase}/api/nodes/${nodeId}/content`, {
    headers: as(userId),
    data: { contentJson: paragraph(text), rowVersion: current.rowVersion },
  });
  expect(saved.ok()).toBe(true);
}

async function grant(
  request: APIRequestContext,
  doc: Doc,
  userId: number,
  role: 'Editor' | 'Approver',
  logicalNodeId?: string,
) {
  const response = await request.post(`${apiBase}/api/documents/${doc.id}/permissions`, {
    headers: as(Alice),
    data: { userId, role, logicalNodeId: logicalNodeId ?? null },
  });
  expect(response.ok()).toBe(true);
}

async function signAll(request: APIRequestContext, versionId: number, approvers: number[]) {
  for (const userId of approvers) {
    expect(
      (await request.post(`${apiBase}/api/versions/${versionId}/signatures`, { headers: as(userId), data: {} })).ok(),
    ).toBe(true);
  }
}

async function openAs(page: Page, userId: number, url: string) {
  await page.goto('/');
  await page.evaluate((id) => localStorage.setItem('dochub.actingUserId', String(id)), userId);
  await page.goto(url);
}

const sectionText = (page: Page, nodeId: number) => page.getByTestId(`section-${nodeId}`).getByLabel('Section text');

test('two windows editing the same section: the second save gets the conflict dialog', async ({ browser, request }) => {
  const doc = await newDocument(request, [{ title: 'Shared', text: 'Start.' }]);
  const nodeId = doc.nodes[0]?.id ?? 0;
  const [first, second] = [await (await browser.newContext()).newPage(), await (await browser.newContext()).newPage()];
  await openAs(first, Alice, `/documents/${doc.id}`);
  await openAs(second, Alice, `/documents/${doc.id}`);

  await sectionText(first, nodeId).click();
  await first.keyboard.press('End');
  await first.keyboard.type(' From window one.');
  await expect(first.getByTestId('save-status')).toContainText('Saved', { timeout: 10_000 });

  await sectionText(second, nodeId).click();
  await second.keyboard.press('End');
  await second.keyboard.type(' From window two.');
  const dialog = second.getByRole('dialog', { name: 'Changed by someone else' });
  await expect(dialog).toBeVisible({ timeout: 10_000 });
  await dialog.getByRole('button', { name: 'Reload theirs' }).click();
  await expect(sectionText(second, nodeId)).toHaveText('Start. From window one.');
});

test('an owner edit outdates a signature (with a warning first); approvers sign; the owner never sees Sign', async ({
  page,
  request,
}) => {
  const doc = await newDocument(request, [{ title: 'Terms', text: 'Draft terms.' }]);
  const nodeId = doc.nodes[0]?.id ?? 0;
  await grant(request, doc, Carol, 'Approver');
  await grant(request, doc, Dave, 'Approver');
  await signAll(request, doc.draftVersionId, [Carol]);

  await openAs(page, Alice, `/documents/${doc.id}`);
  await expect(page.getByTestId('signature-progress')).toHaveText('Signatures 1 / 2');
  await expect(page.getByRole('button', { name: 'Sign', exact: true })).toHaveCount(0);
  await sectionText(page, nodeId).click();
  const warning = page.getByRole('dialog', { name: 'This draft already has signatures' });
  await expect(warning).toContainText('Editing will outdate 1 signature');
  await warning.getByRole('button', { name: 'Edit anyway' }).click();
  await sectionText(page, nodeId).click();
  await page.keyboard.press('End');
  await page.keyboard.type(' Changed.');
  await expect(page.getByTestId('save-status')).toContainText('Saved', { timeout: 10_000 });
  await expect(page.getByTestId('signature-progress')).toHaveText('Signatures 0 / 2');
  await page.getByTestId('signature-progress').click();
  await expect(page.getByTestId(`approver-${Carol}`)).toContainText('outdated');
  await expect(page.getByTestId(`approver-${Dave}`)).toContainText('pending');
});

test('a node-scoped editor edits only their node and its descendants, with no structure actions', async ({
  page,
  request,
}) => {
  const doc = await newDocument(request, [
    { title: 'Mine', text: 'Editable.' },
    { title: 'Mine child', text: 'Also editable.', parent: 0 },
    { title: 'Theirs', text: 'Read only.' },
  ]);
  await grant(request, doc, Bob, 'Editor', doc.nodes[0]?.logicalNodeId);
  await openAs(page, Bob, `/documents/${doc.id}`);

  const [mine, child, theirs] = doc.nodes.map((n) => n.id);
  await expect(sectionText(page, mine ?? 0)).toHaveAttribute('contenteditable', 'true');
  await expect(sectionText(page, child ?? 0)).toHaveAttribute('contenteditable', 'true');
  await expect(sectionText(page, theirs ?? 0)).toHaveAttribute('contenteditable', 'false');
  await expect(page.getByTestId(`tree-node-${theirs}`)).toHaveAttribute('data-muted', 'true');
  await expect(page.getByRole('button', { name: '+ Node' })).toHaveCount(0);
  // The node menu offers only the PDF export (T20), no structure actions.
  await page.getByRole('button', { name: 'Actions for Theirs' }).click();
  await expect(page.getByRole('menuitem')).toHaveText(['Export this section to PDF']);
  await page.keyboard.press('Escape');
  await expect(page.getByRole('button', { name: 'Discard draft' })).toHaveCount(0);

  await sectionText(page, child ?? 0).click();
  await page.keyboard.press('End');
  await page.keyboard.type(' By Bob.');
  await expect(page.getByTestId(`section-${child}`).getByTestId('save-status')).toContainText('Saved', {
    timeout: 10_000,
  });
});

test('signed versions are read-only; restore is offered only on the draft; discard returns to v1', async ({
  page,
  request,
}) => {
  const doc = await newDocument(request, [{ title: 'Scope', text: 'Version one.' }]);
  await grant(request, doc, Carol, 'Approver');
  await signAll(request, doc.draftVersionId, [Carol]);
  const draft = await (await request.post(`${apiBase}/api/documents/${doc.id}/drafts`, { headers: as(Alice) })).json();
  const tree = await (await request.get(`${apiBase}/api/versions/${draft.id}/tree`, { headers: as(Alice) })).json();
  const draftNode = tree[0].id as number;
  await setText(request, draftNode, 'Version two.', Alice);

  // Draft: restore offered in the section history; restoring v1's text is a normal (audited) edit.
  await openAs(page, Alice, `/documents/${doc.id}`);
  const section = page.getByTestId(`section-${draftNode}`);
  await section.locator('.dh-history-toggle').click();
  const oldEntry = section.locator('.dh-timeline-item', { hasText: 'content' }).last();
  await oldEntry.getByRole('button', { name: 'Restore this text' }).click();
  await expect(section.getByTestId('save-status')).toContainText('Saved', { timeout: 10_000 });
  await expect(sectionText(page, draftNode)).toHaveText('Version one.');
  const history = await (
    await request.get(`${apiBase}/api/documents/${doc.id}/nodes/${tree[0].logicalNodeId}/history`, {
      headers: as(Alice),
    })
  ).json();
  expect(history.items[0].kind).toBe('ContentChanged');

  // v1: read-only, no restore.
  await page.getByRole('combobox', { name: 'Version' }).click();
  await page.getByRole('option', { name: 'v1', exact: true }).click();
  await expect(page.getByText('v1 is signed and read-only.')).toBeVisible();
  const signedSection = page.locator('.dh-page-section').first();
  await expect(signedSection.getByLabel('Section text')).toHaveAttribute('contenteditable', 'false');
  await signedSection.locator('.dh-history-toggle').click();
  await expect(signedSection.getByRole('button', { name: 'Compare' }).first()).toBeVisible();
  await expect(signedSection.getByRole('button', { name: 'Restore this text' })).toHaveCount(0);

  // Discard the draft → v1 is shown, New draft is offered again.
  await page.getByRole('combobox', { name: 'Version' }).click();
  await page.getByRole('option', { name: /Draft/ }).click();
  await page.getByRole('button', { name: 'Discard draft' }).click();
  await page.getByRole('dialog').getByRole('button', { name: 'Discard' }).click();
  await expect(page.getByRole('button', { name: 'New draft' })).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Version' })).toHaveValue('v1');
});

test('"Show changes since v1" marks changed, moved, renamed and deleted sections, attributed per user', async ({
  page,
  request,
}) => {
  const doc = await newDocument(request, [
    { title: 'Alpha', text: 'Alpha text stays.' },
    { title: 'Beta', text: 'Beta text here.' },
    { title: 'Gamma', text: 'Gamma text.' },
    { title: 'Delta', text: 'Delta goes away.' },
  ]);
  await grant(request, doc, Bob, 'Editor');
  await grant(request, doc, Carol, 'Approver');
  await signAll(request, doc.draftVersionId, [Carol]);
  const draft = await (await request.post(`${apiBase}/api/documents/${doc.id}/drafts`, { headers: as(Alice) })).json();
  const tree: { id: number; title: string; rowVersion: string }[] = await (
    await request.get(`${apiBase}/api/versions/${draft.id}/tree`, { headers: as(Alice) })
  ).json();
  const byTitle = Object.fromEntries(tree.map((n) => [n.title, n]));
  const [alpha, beta, gamma, delta] = ['Alpha', 'Beta', 'Gamma', 'Delta'].map(
    (t) => byTitle[t] ?? { id: 0, rowVersion: '' },
  );

  await setText(request, alpha?.id ?? 0, 'Alpha text stays. Added by Alice.', Alice);
  await setText(request, alpha?.id ?? 0, 'Alpha text stays. Added by Alice. Bob adds this.', Bob);
  await request.patch(`${apiBase}/api/nodes/${beta?.id}`, {
    headers: as(Alice),
    data: { title: 'Beta renamed', rowVersion: beta?.rowVersion },
  });
  await request.post(`${apiBase}/api/nodes/${gamma?.id}/move`, {
    headers: as(Alice),
    data: { newParentNodeId: null, position: 0, rowVersion: gamma?.rowVersion },
  });
  await request.delete(`${apiBase}/api/nodes/${delta?.id}?rowVersion=${encodeURIComponent(delta?.rowVersion ?? '')}`, {
    headers: as(Alice),
  });

  await openAs(page, Alice, `/documents/${doc.id}`);
  await page.getByLabel('Show changes').check({ force: true });
  const alphaSection = page.getByTestId(`section-${alpha?.id}`);
  await expect(alphaSection.locator('ins', { hasText: 'Added by Alice.' })).toHaveAttribute(
    'title',
    /Inserted by Alice/,
  );
  await expect(alphaSection.locator('ins', { hasText: 'Bob adds this.' })).toHaveAttribute('title', /Inserted by Bob/);
  await expect(page.getByTestId(`section-${beta?.id}`)).toContainText("renamed from 'Beta'");
  await expect(page.getByTestId(`section-${gamma?.id}`)).toContainText('moved from 3');
  const removed = page.locator('.dh-removed-section', { hasText: 'Delta' });
  await expect(removed).toBeVisible();
  await removed.getByRole('button').click();
  await expect(removed).toContainText('Delta goes away.');
  await expect(page.getByTestId(`tree-node-${alpha?.id}`)).toContainText('●2');
});

test('a support-script change shows as "Script · login · ticket" with ⚠ in the section timeline', async ({
  page,
  request,
}) => {
  const doc = await newDocument(request, [{ title: 'Scripted', text: 'Before the script.' }]);
  const nodeId = doc.nodes[0]?.id ?? 0;
  const password = process.env.MSSQL_SA_PASSWORD ?? 'ChangeMe_Str0ng!Passw0rd';
  const sql = `SET NOCOUNT ON; EXEC audit.usp_SetSupportContext @Ticket = N'INC-4242', @Reason = N'e2e'; UPDATE app.NodeContent SET ContentJson = N'${JSON.stringify(paragraph('After the script.'))}' WHERE NodeId = ${nodeId};`;
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
      sql,
    ]);
  } catch {
    test.skip(true, 'No local SQL container (docker exec dochub-sql) to run a support script');
  }

  await openAs(page, Alice, `/documents/${doc.id}`);
  const section = page.getByTestId(`section-${nodeId}`);
  await expect(section.getByLabel('Script or after-signing change')).toBeVisible();
  await section.locator('.dh-history-toggle').click();
  const scripted = section.locator('.dh-timeline-item', { hasText: 'Script' }).first();
  await expect(scripted).toContainText('Script · sa · INC-4242');
  await expect(scripted).toContainText('⚠');
});
