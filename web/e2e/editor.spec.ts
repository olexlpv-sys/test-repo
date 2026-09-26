import { expect, test, type Page } from '@playwright/test';

// T15 smoke: owner adds a node → types content → grants two approvers → both sign → owner creates a new draft → edits a
// section → the section's inline history shows the edit and track changes vs v1 highlight it. Needs the running stack.
const apiBase = process.env.DOCHUB_API ?? 'http://localhost:5080';
const Alice = 2; // owner
const Carol = 4; // approvers
const Dave = 5;

async function actAs(page: Page, userId: number) {
  await page.evaluate((id) => localStorage.setItem('dochub.actingUserId', String(id)), userId);
  await page.reload();
}

test('owner writes, approvers sign, new draft edit shows in the section history and track changes', async ({
  page,
  request,
}) => {
  test.setTimeout(90_000);
  const created = await (
    await request.post(`${apiBase}/api/documents`, {
      headers: { 'X-User-Id': String(Alice) },
      data: { folderId: 1, title: `Smoke NDA ${Date.now().toString(36)}` },
    })
  ).json();

  await page.goto('/');
  await actAs(page, Alice);
  await page.goto(`/documents/${created.id}`);

  // Owner adds a node and types its text (autosaved).
  await page.getByRole('button', { name: '+ Node' }).click();
  const dialog = page.getByRole('dialog', { name: 'New node' });
  await dialog.getByLabel('Title').fill('Scope');
  await dialog.getByRole('button', { name: 'Add' }).click();
  await expect(page.getByRole('treeitem', { name: /Scope/ })).toBeVisible();
  const text = page.getByLabel('Section text').first();
  await text.click();
  await page.keyboard.type('The old scope of the agreement.');
  await expect(page.getByTestId('save-status').first()).toContainText('Saved', { timeout: 10_000 });

  // Two approvers (the permissions dialog is T16; granted through the API here).
  for (const userId of [Carol, Dave]) {
    const grant = await request.post(`${apiBase}/api/documents/${created.id}/permissions`, {
      headers: { 'X-User-Id': String(Alice) },
      data: { userId, role: 'Approver' },
    });
    expect(grant.ok()).toBe(true);
  }

  // Carol signs (1/2), Dave signs → v1. The owner never sees Sign.
  await page.reload();
  await expect(page.getByRole('button', { name: 'Sign', exact: true })).toHaveCount(0);
  for (const [userId, progress] of [
    [Carol, 'Signatures 1 / 2'],
    [Dave, null],
  ] as const) {
    await actAs(page, userId);
    await page.getByRole('button', { name: 'Sign', exact: true }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Sign', exact: true }).click();
    if (progress) {
      await expect(page.getByTestId('signature-progress')).toHaveText(progress);
    }
  }

  await expect(page.getByText('Signed as v1')).toBeVisible();
  await expect(page.getByText('v1 is signed and read-only.')).toBeVisible();

  // Owner: new draft, edit the section.
  await actAs(page, Alice);
  await page.getByRole('button', { name: 'New draft' }).click();
  await expect(page.locator('.dh-chip', { hasText: 'Draft' }).first()).toBeVisible();
  const draftText = page.getByLabel('Section text').first();
  await draftText.click();
  await page.keyboard.press('ControlOrMeta+a');
  await page.keyboard.type('The new scope of the agreement.');
  await expect(page.getByTestId('save-status').first()).toContainText('Saved', { timeout: 10_000 });

  // The section's inline history shows the edit (by Alice), and "Compare" shows track changes vs v1.
  const section = page.locator('.dh-page-section').first();
  await expect(section.locator('.dh-history-toggle')).toContainText('🕘 1');
  await section.locator('.dh-history-toggle').click();
  const history = section.locator('.dh-section-history');
  const edit = history.locator('.dh-timeline-item', { hasText: 'content' }).first();
  await expect(edit).toContainText('Alice');
  await expect(history).toContainText('v1 signed');

  await page.getByLabel('Show changes').check({ force: true }); // Mantine's track covers the input
  const changes = section.getByTestId('track-changes');
  await expect(changes.locator('ins', { hasText: 'new' })).toHaveAttribute('title', /Inserted by Alice/);
  await expect(changes.locator('del', { hasText: 'old' })).toHaveAttribute('title', /Deleted by Alice/);
});
