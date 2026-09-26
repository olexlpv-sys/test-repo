import { expect, test, type APIRequestContext, type Page } from '@playwright/test';

// T16 acceptance: version comparison, comments, permissions dialog. Needs the running stack.
const apiBase = process.env.DOCHUB_API ?? 'http://localhost:5080';
const [Alice, Bob, Carol] = [2, 3, 4];
const as = (userId: number) => ({ 'X-User-Id': String(userId) });
const paragraph = (text: string) => ({
  type: 'doc',
  content: [{ type: 'paragraph', content: [{ type: 'text', text }] }],
});

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

/** A document with nodes (parent = index of an earlier node), optionally signed as v1 with a new draft. */
async function newDocument(request: APIRequestContext, sections: { title: string; text: string; parent?: number }[]) {
  const doc = await (
    await request.post(`${apiBase}/api/documents`, {
      headers: as(Alice),
      data: { folderId: 1, title: `T16 ${Date.now().toString(36)}` },
    })
  ).json();
  const nodes: { id: number; logicalNodeId: string; title: string; rowVersion: string }[] = [];
  for (const s of sections) {
    const node = await (
      await request.post(`${apiBase}/api/versions/${doc.draftVersionId}/nodes`, {
        headers: as(Alice),
        data: { title: s.title, nodeTypeId: 2, parentNodeId: s.parent === undefined ? null : nodes[s.parent]?.id },
      })
    ).json();
    nodes.push(node);
    await setText(request, node.id, s.text);
  }

  return { ...doc, nodes } as { id: number; draftVersionId: number; nodes: typeof nodes };
}

async function openAs(page: Page, userId: number, url: string) {
  await page.goto('/');
  await page.evaluate((id) => localStorage.setItem('dochub.actingUserId', String(id)), userId);
  await page.goto(url);
}

test('compare v1 with the draft: the summary matches the API and a changed node shows its diff', async ({
  page,
  request,
}) => {
  const doc = await newDocument(request, [
    { title: 'Scope', text: 'The old scope.' },
    { title: 'Terms', text: 'Terms stay.' },
    { title: 'Gone', text: 'Removed later.' },
  ]);
  await request.post(`${apiBase}/api/documents/${doc.id}/permissions`, {
    headers: as(Alice),
    data: { userId: Carol, role: 'Approver' },
  });
  await request.post(`${apiBase}/api/versions/${doc.draftVersionId}/signatures`, { headers: as(Carol), data: {} });
  const draft = await (await request.post(`${apiBase}/api/documents/${doc.id}/drafts`, { headers: as(Alice) })).json();
  const tree: { id: number; title: string; logicalNodeId: string; rowVersion: string }[] = await (
    await request.get(`${apiBase}/api/versions/${draft.id}/tree`, { headers: as(Alice) })
  ).json();
  const byTitle = Object.fromEntries(tree.map((n) => [n.title, n]));
  await setText(request, byTitle.Scope?.id ?? 0, 'The new scope.');
  await request.patch(`${apiBase}/api/nodes/${byTitle.Terms?.id}`, {
    headers: as(Alice),
    data: { title: 'Conditions', rowVersion: byTitle.Terms?.rowVersion },
  });
  await request.delete(
    `${apiBase}/api/nodes/${byTitle.Gone?.id}?rowVersion=${encodeURIComponent(byTitle.Gone?.rowVersion ?? '')}`,
    { headers: as(Alice) },
  );
  await request.post(`${apiBase}/api/versions/${draft.id}/nodes`, {
    headers: as(Alice),
    data: { title: 'Annex', nodeTypeId: 2 },
  });

  const api = await (
    await request.get(`${apiBase}/api/documents/${doc.id}/compare?base=latestSigned&target=draft`, {
      headers: as(Alice),
    })
  ).json();

  await openAs(page, Alice, `/documents/${doc.id}`);
  await page.getByRole('button', { name: 'Compare…' }).click();
  await expect(page).toHaveURL(/\/documents\/\d+\/compare/);
  const summary = page.getByTestId('compare-summary');
  await expect(summary).toContainText(`+${api.summary.added} added`);
  await expect(summary).toContainText(`−${api.summary.removed} removed`);
  await expect(summary).toContainText(`${api.summary.renamed} renamed`);
  await expect(summary).toContainText(`${api.summary.contentChanged} content changed`);

  await page.getByTestId(`compare-${byTitle.Scope?.logicalNodeId}`).click();
  const diff = page.getByTestId('track-changes');
  await expect(diff.locator('del', { hasText: 'old' })).toBeVisible();
  await expect(diff.locator('ins', { hasText: 'new' })).toBeVisible();
  await expect(page.getByTestId(`compare-${byTitle.Gone?.logicalNodeId}`)).toHaveAttribute('data-status', 'Removed');
  await expect(page.getByTestId(`compare-${byTitle.Terms?.logicalNodeId}`)).toContainText('Conditions');

  await page.getByRole('button', { name: 'Swap base and target' }).click();
  await expect(summary).toContainText(`+${api.summary.removed} added`);
});

test('an approver comments on a node and on the document; the owner replies and resolves; badges update', async ({
  page,
  request,
}) => {
  const doc = await newDocument(request, [{ title: 'Scope', text: 'Scope text.' }]);
  const node = doc.nodes[0] ?? { id: 0, logicalNodeId: '' };
  await request.post(`${apiBase}/api/documents/${doc.id}/permissions`, {
    headers: as(Alice),
    data: { userId: Carol, role: 'Approver' },
  });

  await openAs(page, Carol, `/documents/${doc.id}?node=${node.id}`);
  await page.getByRole('button', { name: /💬 Comments/ }).click();
  const panel = page.getByRole('region', { name: 'Comments' });
  const documentComposer = panel.getByRole('group', { name: 'Comment on the document' });
  await documentComposer.getByRole('textbox').fill('Please check the whole document.');
  await documentComposer.getByRole('button', { name: 'Comment', exact: true }).click();
  await expect(panel.getByText('Please check the whole document.')).toBeVisible();
  const nodeComposer = panel.getByRole('group', { name: /Comment on 1 Scope/ });
  await nodeComposer.getByRole('textbox').fill('Is this scope final?');
  await nodeComposer.getByRole('button', { name: 'Comment', exact: true }).click();
  await expect(panel.getByText('Is this scope final?')).toBeVisible();
  await expect(page.getByTestId(`tree-node-${node.id}`)).toContainText('💬1');
  await expect(page.getByRole('button', { name: /💬 Comments \(1\)/ })).toBeVisible();

  // Owner: reply and resolve.
  await openAs(page, Alice, `/documents/${doc.id}?node=${node.id}`);
  await page.getByRole('button', { name: /💬 Comments/ }).click();
  const ownerPanel = page.getByRole('region', { name: 'Comments' });
  const thread = ownerPanel.locator('.dh-thread', { hasText: 'Is this scope final?' });
  const reply = thread.getByRole('group', { name: /Reply to/ });
  await reply.getByRole('textbox').fill('Yes, final.');
  await reply.getByRole('button', { name: 'Reply', exact: true }).click();
  await expect(thread.getByText('Yes, final.')).toBeVisible();
  await thread.getByRole('button', { name: 'Resolve' }).click();
  await expect(thread).toContainText('resolved by Alice');
  await expect(page.getByTestId(`tree-node-${node.id}`)).toContainText('💬2'); // the reply counts too
  await ownerPanel.getByLabel('Show resolved').uncheck({ force: true });
  await expect(ownerPanel.getByText('Is this scope final?')).toHaveCount(0);
});

test('a reader cannot comment: the input is disabled with an explanation', async ({ page, request }) => {
  const doc = await newDocument(request, [{ title: 'Scope', text: 'Scope text.' }]);
  await openAs(page, Bob, `/documents/${doc.id}`);
  await page.getByRole('button', { name: /💬 Comments/ }).click();
  await expect(
    page
      .getByRole('region', { name: 'Comments' })
      .getByRole('group', { name: 'Comment on the document' })
      .getByRole('textbox'),
  ).toBeDisabled();
});

test('the owner grants a node-scoped editor in the dialog (never the owner as approver); that editor edits only the subtree', async ({
  page,
  request,
}) => {
  const doc = await newDocument(request, [
    { title: 'Chapter', text: 'Chapter text.' },
    { title: 'Sub', text: 'Sub text.', parent: 0 },
    { title: 'Other', text: 'Other text.' },
  ]);
  const [chapter, sub, other] = doc.nodes;

  await openAs(page, Alice, `/documents/${doc.id}`);
  await page.getByRole('button', { name: 'Share / Permissions' }).click();
  const dialog = page.getByRole('dialog', { name: 'Share / Permissions' });
  await dialog.getByText('Approver', { exact: true }).click();
  await dialog.getByRole('combobox', { name: 'User' }).fill('ali');
  await expect(page.getByText('No users')).toBeVisible(); // the owner is never offered (dropdown in a portal)
  await expect(page.getByRole('option', { name: /Alice/ })).toHaveCount(0);
  await dialog.getByText('Editor', { exact: true }).click();
  await dialog.getByRole('combobox', { name: 'User' }).fill('bob');
  await page.getByRole('option', { name: /Bob/ }).click();
  await dialog.getByRole('combobox', { name: 'Scope' }).click();
  await page.getByRole('option', { name: /1 Chapter/ }).click();
  await dialog.getByRole('button', { name: 'Add' }).click();
  await expect(dialog.getByTestId('grants')).toContainText('Bob');
  await expect(dialog.getByTestId('grants')).toContainText('1 Chapter');

  await openAs(page, Bob, `/documents/${doc.id}`);
  await expect(page.getByTestId(`section-${chapter?.id}`).getByLabel('Section text')).toHaveAttribute(
    'contenteditable',
    'true',
  );
  await expect(page.getByTestId(`section-${sub?.id}`).getByLabel('Section text')).toHaveAttribute(
    'contenteditable',
    'true',
  );
  await expect(page.getByTestId(`section-${other?.id}`).getByLabel('Section text')).toHaveAttribute(
    'contenteditable',
    'false',
  );
  await expect(page.getByRole('button', { name: '+ Node' })).toHaveCount(0);

  // Non-owner: the dialog is read-only.
  await page.getByRole('button', { name: 'Permissions', exact: true }).click();
  const readOnly = page.getByRole('dialog', { name: 'Permissions' });
  await expect(readOnly).toContainText('Only the owner changes roles.');
  await expect(readOnly.getByTestId('grants')).toContainText('Bob');
  await expect(readOnly.getByRole('button', { name: /Remove/ })).toHaveCount(0);
  await expect(readOnly.getByRole('button', { name: 'Add' })).toHaveCount(0);
});

test('granting a role in the dialog updates the editor at once (my-permissions refreshed)', async ({
  page,
  request,
}) => {
  const doc = await newDocument(request, [{ title: 'Scope', text: 'Scope text.' }]);
  await openAs(page, Alice, `/documents/${doc.id}`);
  await page.getByRole('button', { name: 'Share / Permissions' }).click();
  const dialog = page.getByRole('dialog', { name: 'Share / Permissions' });
  await dialog.getByText('Approver', { exact: true }).click();
  await dialog.getByRole('combobox', { name: 'User' }).fill('carol');
  await page.getByRole('option', { name: /Carol/ }).click();
  await dialog.getByRole('button', { name: 'Add' }).click();
  await expect(dialog.getByTestId('grants')).toContainText('Approver');
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('signature-progress')).toHaveText('Signatures 0 / 1');
  await dialog.isHidden();
  await page.getByRole('button', { name: 'Share / Permissions' }).click();
  await page
    .getByRole('dialog', { name: 'Share / Permissions' })
    .getByRole('button', { name: /Remove Approver/ })
    .click();
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('signature-progress')).toHaveText('Signatures 0 / 0');
});

test('before any signed version, Compare… is disabled and the compare page explains why', async ({ page, request }) => {
  const doc = await newDocument(request, [{ title: 'Only', text: 'Draft only.' }]);
  await openAs(page, Alice, `/documents/${doc.id}`);
  await expect(page.getByRole('button', { name: 'Compare…' })).toBeDisabled();
  await page.goto(`/documents/${doc.id}/compare`);
  await expect(page.getByText(/No signed version to compare yet/)).toBeVisible();
});

test('the permission scope comes from the current draft even while an older version is on screen', async ({
  page,
  request,
}) => {
  const doc = await newDocument(request, [
    { title: 'Kept', text: 'Kept.' },
    { title: 'Dropped', text: 'Dropped in v2.' },
  ]);
  await request.post(`${apiBase}/api/documents/${doc.id}/permissions`, {
    headers: as(Alice),
    data: { userId: Carol, role: 'Approver' },
  });
  await request.post(`${apiBase}/api/versions/${doc.draftVersionId}/signatures`, { headers: as(Carol), data: {} });
  const v2 = await (await request.post(`${apiBase}/api/documents/${doc.id}/drafts`, { headers: as(Alice) })).json();
  const tree: { id: number; title: string; rowVersion: string }[] = await (
    await request.get(`${apiBase}/api/versions/${v2.id}/tree`, { headers: as(Alice) })
  ).json();
  const dropped = tree.find((n) => n.title === 'Dropped');
  await request.delete(
    `${apiBase}/api/nodes/${dropped?.id}?rowVersion=${encodeURIComponent(dropped?.rowVersion ?? '')}`,
    { headers: as(Alice) },
  );
  await request.post(`${apiBase}/api/versions/${v2.id}/signatures`, { headers: as(Carol), data: {} });
  await request.post(`${apiBase}/api/documents/${doc.id}/drafts`, { headers: as(Alice) });

  await openAs(page, Alice, `/documents/${doc.id}?version=${doc.draftVersionId}`); // v1, which still has "Dropped"
  await expect(page.getByText('v1 is signed and read-only.')).toBeVisible();
  await page.getByRole('button', { name: 'Share / Permissions' }).click();
  await page.getByRole('dialog', { name: 'Share / Permissions' }).getByRole('combobox', { name: 'Scope' }).click();
  await expect(page.getByRole('option', { name: /Kept/ })).toBeVisible();
  await expect(page.getByRole('option', { name: /Dropped/ })).toHaveCount(0);
});
