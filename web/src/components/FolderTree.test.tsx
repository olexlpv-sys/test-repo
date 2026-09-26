import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { alice, baseRoutes, fakeApi, Reply } from '../test/fakeApi';
import { renderApp } from '../test/render';

const tree = [
  {
    id: 10,
    name: 'Contracts',
    sortOrder: 0,
    documentCount: 1,
    children: [
      {
        id: 12,
        name: 'Archive',
        sortOrder: 0,
        documentCount: 0,
        children: [{ id: 13, name: '2025', sortOrder: 0, documentCount: 0, children: [] }],
      },
    ],
  },
  { id: 11, name: 'Policies', sortOrder: 1, documentCount: 0, children: [] },
];

const folder = (id: number, name: string, parentFolderId: number | null = null) => ({
  id,
  parentFolderId,
  name,
  sortOrder: 0,
  path: [],
  rowVersion: `f${id}`,
});

function routes(overrides: Record<string, Parameters<typeof fakeApi>[0][string]> = {}) {
  return {
    ...baseRoutes(),
    'GET /api/folders/tree': () => tree,
    'GET /api/folders/(\\d+)/documents': () => ({ items: [], page: 1, pageSize: 200, totalCount: 0 }),
    'GET /api/folders/(\\d+)': (_r: unknown, m: RegExpMatchArray) => folder(Number(m[1]), 'x'),
    ...overrides,
  };
}

async function openActions(name: string) {
  await userEvent.click(await screen.findByRole('button', { name: `Actions for ${name}` }));
}

describe('folder tree (FR-UI1, FR-F5)', () => {
  it('expands and collapses, shows document counts and selects a folder', async () => {
    fakeApi(routes());
    renderApp();
    const contracts = await screen.findByTestId('folder-10');
    expect(within(contracts).getByText('1')).toBeInTheDocument();
    expect(screen.queryByTestId('folder-12')).not.toBeInTheDocument();

    await userEvent.click(within(contracts).getByRole('button', { name: 'Expand' }));
    expect(await screen.findByTestId('folder-12')).toBeInTheDocument();
    await userEvent.click(within(screen.getByTestId('folder-12')).getByText('Archive'));
    expect(window.location.search).toBe('?folder=12');
    expect(screen.getByTestId('folder-12')).toHaveAttribute('data-selected', 'true');

    await userEvent.click(within(contracts).getByRole('button', { name: 'Collapse' }));
    await waitFor(() => expect(screen.queryByTestId('folder-12')).not.toBeInTheDocument());
  });

  it('expands every level down to a deep-linked folder, and the parent can still be collapsed', async () => {
    fakeApi(routes());
    renderApp('/?folder=13');
    expect(await screen.findByTestId('folder-13')).toHaveAttribute('data-selected', 'true');
    expect(screen.getByTestId('folder-12')).toBeInTheDocument();

    await userEvent.click(within(screen.getByTestId('folder-10')).getByRole('button', { name: 'Collapse' }));
    await waitFor(() => expect(screen.queryByTestId('folder-13')).not.toBeInTheDocument());
  });

  it('offers no management actions to non-admins', async () => {
    localStorage.setItem('dochub.actingUserId', String(alice.id));
    fakeApi(routes());
    renderApp();
    await screen.findByTestId('folder-10');
    expect(screen.queryByRole('button', { name: 'New folder' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Actions for/ })).not.toBeInTheDocument();
  });

  it('creates a top-level folder and a sub-folder, then selects the new folder', async () => {
    let next = 20;
    const { calls } = fakeApi(
      routes({ 'POST /api/folders': (r) => new Reply(201, folder(next++, (r.body as { name: string }).name)) }),
    );
    renderApp();
    await screen.findByTestId('folder-10');

    await userEvent.click(screen.getByRole('button', { name: 'New folder' }));
    let dialog = await screen.findByRole('dialog', { name: 'New folder' });
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Reports');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(window.location.search).toBe('?folder=20'));

    await openActions('Policies');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'New sub-folder' }));
    dialog = await screen.findByRole('dialog', { name: 'New folder' });
    await userEvent.type(within(dialog).getByLabelText('Name'), 'HR');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(calls.filter((c) => c.method === 'POST' && c.path === '/api/folders').map((c) => c.body)).toEqual([
        { parentFolderId: null, name: 'Reports' },
        { parentFolderId: 11, name: 'HR' },
      ]),
    );
  });

  it('shows a duplicate name next to the field', async () => {
    fakeApi(routes({ 'POST /api/folders': () => new Reply(409, { type: 'duplicate-name', title: 'Duplicate' }) }));
    renderApp();
    await screen.findByTestId('folder-10');
    await userEvent.click(screen.getByRole('button', { name: 'New folder' }));
    const dialog = await screen.findByRole('dialog', { name: 'New folder' });
    await userEvent.type(within(dialog).getByLabelText('Name'), 'Policies');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));
    expect(await within(dialog).findByText('A folder with this name already exists here.')).toBeInTheDocument();
  });

  it('renames with the current row version', async () => {
    const { calls } = fakeApi(
      routes({ 'PUT /api/folders/(\\d+)': (r, m) => folder(Number(m[1]), (r.body as { name: string }).name) }),
    );
    renderApp();
    await openActions('Policies');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Rename' }));
    const dialog = await screen.findByRole('dialog', { name: 'Rename folder' });
    const name = within(dialog).getByLabelText('Name');
    expect(name).toHaveValue('Policies');
    await userEvent.clear(name);
    await userEvent.type(name, 'Guidelines');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() =>
      expect(calls.find((c) => c.method === 'PUT')?.body).toEqual({ name: 'Guidelines', rowVersion: 'f11' }),
    );
    expect(calls.find((c) => c.method === 'PUT')?.path).toBe('/api/folders/11');
  });

  it('moves a folder to the top level but never into itself', async () => {
    const { calls } = fakeApi(routes({ 'POST /api/folders/(\\d+)/move': (_r, m) => folder(Number(m[1]), 'Archive') }));
    renderApp('/?folder=12');
    await openActions('Contracts');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Move to…' }));
    const picker = await screen.findByRole('dialog', { name: 'Move folder to…' });
    expect(within(picker).queryByRole('button', { name: /Contracts|Archive|2025/ })).not.toBeInTheDocument();
    await userEvent.click(within(picker).getByRole('button', { name: '(top level)' }));

    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/folders/10/move')?.body).toEqual({
        newParentFolderId: null,
        rowVersion: 'f10',
      }),
    );
  });

  it('explains why a folder in use cannot be deleted', async () => {
    const { calls } = fakeApi(
      routes({ 'DELETE /api/folders/(\\d+)': () => new Reply(409, { type: 'in-use', title: 'In use' }) }),
    );
    renderApp();
    await openActions('Policies');
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Delete' }));
    const dialog = await screen.findByRole('dialog', { name: 'Delete folder "Policies"?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));

    expect(await screen.findByText(/still has sub-folders or documents/)).toBeInTheDocument();
    expect(calls.find((c) => c.method === 'DELETE')?.query.get('rowVersion')).toBe('f11');
  });
});
