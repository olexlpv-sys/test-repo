import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { baseRoutes, fakeApi, Reply } from '../test/fakeApi';
import { renderApp } from '../test/render';

const type = (id: number, code: string, usageCount: number, isActive = true) => ({
  id,
  code,
  name: `${code} name`,
  description: null,
  sortOrder: id * 10,
  isActive,
  rowVersion: `t${id}`,
  usageCount,
});

describe('admin tab (T17)', () => {
  it('node types: delete is disabled while a type is used; validation errors show on the fields', async () => {
    const { calls } = fakeApi({
      ...baseRoutes(),
      'GET /api/node-types': () => [type(1, 'CHAPTER', 3), type(2, 'SPARE', 0)],
      'POST /api/node-types': () =>
        new Reply(400, {
          type: 'validation-failed',
          title: 'Invalid',
          errors: { Code: ['Use letters, digits and underscores.'] },
        }),
      'DELETE /api/node-types/2': () => new Reply(204),
    });
    renderApp('/admin?tab=node-types');

    const used = await screen.findByTestId('node-type-CHAPTER');
    expect(within(used).getByRole('button', { name: 'Delete' })).toBeDisabled();
    await userEvent.click(within(screen.getByTestId('node-type-SPARE')).getByRole('button', { name: 'Delete' }));
    await userEvent.click(within(await screen.findByRole('dialog')).getByRole('button', { name: 'Delete' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE' && c.path === '/api/node-types/2')).toBe(true));

    await userEvent.click(screen.getByRole('button', { name: 'Add node type' }));
    const dialog = await screen.findByRole('dialog', { name: 'New node type' });
    await userEvent.type(within(dialog).getByLabelText('Code'), 'bad code');
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Bad');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await within(dialog).findByText('Use letters, digits and underscores.')).toBeInTheDocument();
  });

  it('audit log: asks the API for the last 7 days by default and explains a too long range', async () => {
    const { calls } = fakeApi({
      ...baseRoutes(),
      'GET /api/admin/audit': () => ({ items: [], page: 1, pageSize: 50, totalCount: 0 }),
      'GET /api/admin/audit/findings': () => ({ items: [], page: 1, pageSize: 50, totalCount: 0 }),
    });
    renderApp('/admin?tab=audit');
    await waitFor(() => expect(calls.some((c) => c.path === '/api/admin/audit')).toBe(true));
    const query = calls.find((c) => c.path === '/api/admin/audit')?.query;
    const days =
      (new Date(query?.get('to') ?? '').getTime() - new Date(query?.get('from') ?? '').getTime()) / 86_400_000;
    expect(days).toBeGreaterThan(7);
    expect(days).toBeLessThan(9);
    expect(await screen.findByText('No findings — the ledger and the audit log agree.')).toBeInTheDocument();

    const before = calls.filter((c) => c.path === '/api/admin/audit').length;
    const from = screen.getByLabelText('From');
    await userEvent.clear(from);
    await userEvent.type(from, '2020-01-01');
    expect(await screen.findByText('The date range can be at most 31 days.')).toBeInTheDocument();
    expect(calls.filter((c) => c.path === '/api/admin/audit').length).toBe(before);
  });
});

describe('audit date range', () => {
  it('counts calendar days, so a range across a daylight-saving change is not cut short', async () => {
    const { rangeError } = await import('./auditRange');
    expect(rangeError('2026-10-01', '2026-10-31')).toBeNull(); // 31 days incl. the October DST change
    expect(rangeError('2026-03-01', '2026-03-31')).toBeNull();
    expect(rangeError('2026-10-01', '2026-11-01')).toBe('The date range can be at most 31 days.');
    expect(rangeError('2026-10-05', '2026-10-01')).toBe('"From" is after "To".');
    expect(rangeError('', '2026-10-01')).toMatch(/Choose a date range/);
  });
});
