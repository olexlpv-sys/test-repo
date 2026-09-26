import { expect, test } from '@playwright/test';

// API timestamps are UTC with a Z, so a browser outside UTC shows local times. In Berlin (UTC+1/+2) a wrong reading would
// be off by one or two hours. Needs the running stack.
const apiBase = process.env.DOCHUB_API ?? 'http://localhost:5080';
const Alice = 2;
const as = { 'X-User-Id': String(Alice) };

test.use({ timezoneId: 'Europe/Berlin', locale: 'en-US' });

/** "9/26/2026, 5" — the date and hour toLocaleString shows in Berlin for an instant. */
const berlinHour = (at: Date) => {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone: 'Europe/Berlin',
    year: 'numeric',
    month: 'numeric',
    day: 'numeric',
    hour: 'numeric',
    hour12: true,
  }).formatToParts(at);
  const part = (type: string) => parts.find((p) => p.type === type)?.value;
  return `${part('month')}/${part('day')}/${part('year')}, ${part('hour')}:`;
};

test('a change made just now shows the current local hour in the section history', async ({ page, request }) => {
  const before = new Date();
  const doc = await (
    await request.post(`${apiBase}/api/documents`, {
      headers: as,
      data: { folderId: 1, title: `TZ ${Date.now().toString(36)}` },
    })
  ).json();
  const node = await (
    await request.post(`${apiBase}/api/versions/${doc.draftVersionId}/nodes`, {
      headers: as,
      data: { title: 'Scope', nodeTypeId: 2, parentNodeId: null },
    })
  ).json();
  const content = await (await request.get(`${apiBase}/api/nodes/${node.id}/content`, { headers: as })).json();
  const saved = await request.put(`${apiBase}/api/nodes/${node.id}/content`, {
    headers: as,
    data: {
      contentJson: { type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'Now.' }] }] },
      rowVersion: content.rowVersion,
    },
  });
  expect(saved.ok()).toBe(true);
  const after = new Date();

  await page.goto('/');
  await page.evaluate((id) => localStorage.setItem('dochub.actingUserId', String(id)), Alice);
  await page.goto(`/documents/${doc.id}`);
  await page.getByTestId(`history-toggle-${node.id}`).click();
  const edit = page.locator('.dh-section-history .dh-timeline-item', { hasText: 'content' }).first();
  await expect(edit).toContainText('Alice');

  // The hour may roll over between the save and the check; either side is right, a UTC reading is not.
  const text = (await edit.textContent()) ?? '';
  expect(
    [berlinHour(before), berlinHour(after)].some((hour) => text.includes(hour)),
    `${text} shows no Berlin time`,
  ).toBe(true);
});
