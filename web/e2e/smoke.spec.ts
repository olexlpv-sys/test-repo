import { expect, test } from '@playwright/test';

// Create a folder → add a document → see it in the folder's list (T14 smoke test). The seeded default user is an admin.
test('create folder, add document, see it in the list', async ({ page }) => {
  const suffix = Date.now().toString(36);
  const folder = `Smoke ${suffix}`;
  const title = `Smoke document ${suffix}`;

  await page.goto('/');
  await page.getByRole('button', { name: 'New folder' }).click();
  const folderDialog = page.getByRole('dialog', { name: 'New folder' });
  await folderDialog.getByLabel('Name').fill(folder);
  await folderDialog.getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('.dh-folder-row').filter({ hasText: folder })).toBeVisible();
  await expect(page.locator('.dh-section-title')).toHaveText(folder);

  await page.getByRole('button', { name: 'Create' }).click();
  const documentDialog = page.getByRole('dialog', { name: 'New document' });
  await documentDialog.getByLabel('Title').fill(title);
  await documentDialog.getByRole('button', { name: 'Create' }).click();
  await expect(page).toHaveURL(/\/documents\/\d+$/);

  await page.goBack();
  const card = page.locator('article').filter({ hasText: title });
  await expect(card).toBeVisible();
  await expect(card.locator('.dh-chip').first()).toHaveText('Draft');

  if (process.env.DOCHUB_SCREENSHOTS) {
    await page.screenshot({ path: `${process.env.DOCHUB_SCREENSHOTS}/main.png` });
    await page.getByRole('button', { name: 'Create' }).click();
    await page.getByRole('dialog', { name: 'New document' }).getByLabel('Title').fill('Quarterly report');
    await page.waitForTimeout(400); // modal transition
    await page.screenshot({ path: `${process.env.DOCHUB_SCREENSHOTS}/create-modal.png` });
  }
});
