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
    documentCount: 2,
    children: [{ id: 12, name: 'Archive', sortOrder: 0, documentCount: 0, children: [] }],
  },
  { id: 11, name: 'Policies', sortOrder: 1, documentCount: 0, children: [] },
];

function doc(id: number, title: string, extra: Record<string, unknown> = {}) {
  return {
    id,
    rowVersion: `rv${id}`,
    folderId: 10,
    title,
    status: 'Draft',
    latestSignedVersion: null,
    hasDraft: true,
    signatureProgress: null,
    owner: { id: 2, displayName: 'Alice Author' },
    myRoles: ['Owner'],
    createdAt: '2026-09-01T10:00:00Z',
    modifiedAt: '2026-09-02T10:00:00Z',
    ...extra,
  };
}

const documents = [
  doc(100, 'Supply agreement'),
  doc(101, 'Signed NDA', {
    status: 'Signed',
    latestSignedVersion: 3,
    signatureProgress: { signed: 2, required: 2 },
    myRoles: ['Reader'],
  }),
  doc(102, 'Old lease', { status: 'Deleted' }),
];

function routes(overrides: Record<string, Parameters<typeof fakeApi>[0][string]> = {}) {
  return {
    ...baseRoutes(),
    'GET /api/folders/tree': () => tree,
    'GET /api/folders/(\\d+)/documents': (r: { query: URLSearchParams }) => {
      const items =
        r.query.get('IncludeDeleted') === 'true' ? documents : documents.filter((d) => d.status !== 'Deleted');
      return { items, page: 1, pageSize: 200, totalCount: items.length };
    },
    ...overrides,
  };
}

const card = (title: string) => screen.getByText(title).closest('article') as HTMLElement;

describe('document list (FR-UI1)', () => {
  it("shows the first folder's documents with status, version, owner and signatures", async () => {
    localStorage.setItem('dochub.actingUserId', String(alice.id));
    fakeApi(routes());
    renderApp();

    expect(await screen.findByText('Supply agreement')).toBeInTheDocument();
    expect(screen.getByText('Contracts', { selector: '.dh-section-title' })).toBeInTheDocument();
    const signed = card('Signed NDA');
    expect(within(signed).getByText('Signed')).toHaveAttribute('data-tone', 'green');
    expect(within(signed).getByText('v3')).toBeInTheDocument();
    expect(within(signed).getByText('2/2')).toBeInTheDocument();
    expect(within(card('Supply agreement')).getByText('Draft')).toHaveAttribute('data-tone', 'amber');
    expect(screen.queryByText('Old lease')).not.toBeInTheDocument();
  });

  it('sorts on the server and shows deleted documents on request', async () => {
    const { calls } = fakeApi(routes());
    renderApp();
    await screen.findByText('Supply agreement');

    await userEvent.click(screen.getByRole('button', { name: /^Status/ }));
    await waitFor(() =>
      expect(calls.some((c) => c.query.get('SortBy') === 'status' && c.query.get('SortDir') === 'asc')).toBe(true),
    );
    await userEvent.click(screen.getByRole('button', { name: /^Status/ }));
    await waitFor(() =>
      expect(calls.some((c) => c.query.get('SortBy') === 'status' && c.query.get('SortDir') === 'desc')).toBe(true),
    );

    await userEvent.click(screen.getByLabelText('Show deleted'));
    expect(await screen.findByText('Old lease')).toBeInTheDocument();
    expect(within(card('Old lease')).getByText('Deleted')).not.toHaveAttribute('data-tone');
  });

  it('opens the folder from the URL (?folder=12) and expands the tree down to it', async () => {
    const { calls } = fakeApi(routes());
    renderApp('/?folder=12');
    expect(await screen.findByText('Archive', { selector: '.dh-section-title' })).toBeInTheDocument();
    expect(screen.getByTestId('folder-12')).toBeVisible();
    await waitFor(() => expect(calls.some((c) => c.path === '/api/folders/12/documents')).toBe(true));
  });

  it('pages large folders with "Show more"', async () => {
    const many = Array.from({ length: 201 }, (_, i) => doc(1000 + i, `Doc ${String(i).padStart(3, '0')}`));
    const { calls } = fakeApi(
      routes({
        'GET /api/folders/(\\d+)/documents': (r: { query: URLSearchParams }) => {
          const page = Number(r.query.get('Page'));
          return { items: many.slice((page - 1) * 200, page * 200), page, pageSize: 200, totalCount: many.length };
        },
      }),
    );
    renderApp();
    await screen.findByText('Doc 000');
    expect(screen.queryByText('Doc 200')).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: 'Show more (200 of 201)' }));
    expect(await screen.findByText('Doc 200')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Show more/ })).not.toBeInTheDocument();
    expect(calls.filter((c) => c.path === '/api/folders/10/documents').map((c) => c.query.get('Page'))).toEqual([
      '1',
      '2',
    ]);
  });

  it('lets the owner delete the selected document (with its row version) but not a reader', async () => {
    localStorage.setItem('dochub.actingUserId', String(alice.id));
    const { calls } = fakeApi(routes({ 'DELETE /api/documents/(\\d+)': () => new Reply(204) }));
    renderApp();
    await screen.findByText('Supply agreement');
    const del = screen.getByRole('button', { name: 'Delete' });
    expect(del).toBeDisabled();

    await userEvent.click(card('Signed NDA'));
    expect(del).toBeDisabled(); // reader only

    await userEvent.click(card('Supply agreement'));
    expect(card('Supply agreement')).toHaveAttribute('data-selected', 'true');
    await userEvent.click(del);
    const dialog = await screen.findByRole('dialog', { name: 'Delete "Supply agreement"?' });
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete' }));

    await waitFor(() =>
      expect(
        calls.some(
          (c) => c.method === 'DELETE' && c.path === '/api/documents/100' && c.query.get('rowVersion') === 'rv100',
        ),
      ).toBe(true),
    );
  });

  it('creates a document in the folder and opens it', async () => {
    const { calls } = fakeApi(routes({ 'POST /api/documents': () => new Reply(201, { id: 555 }) }));
    renderApp();
    await screen.findByText('Supply agreement');

    await userEvent.click(screen.getByRole('button', { name: 'Create' }));
    const dialog = await screen.findByRole('dialog', { name: 'New document' });
    const create = within(dialog).getByRole('button', { name: 'Create' });
    expect(create).toBeDisabled();
    await userEvent.type(within(dialog).getByLabelText('Title'), 'Service contract');
    await userEvent.click(create);

    await waitFor(() => expect(window.location.pathname).toBe('/documents/555'));
    expect(calls.find((c) => c.method === 'POST' && c.path === '/api/documents')?.body).toEqual({
      folderId: 10,
      title: 'Service contract',
    });
  });

  it("shows the API's validation message for the title", async () => {
    fakeApi(
      routes({
        'POST /api/documents': () =>
          new Reply(400, { type: 'validation', title: 'Invalid', errors: { title: ['The title is too long.'] } }),
      }),
    );
    renderApp();
    await screen.findByText('Supply agreement');

    await userEvent.click(screen.getByRole('button', { name: 'Create' }));
    const dialog = await screen.findByRole('dialog', { name: 'New document' });
    await userEvent.type(within(dialog).getByLabelText('Title'), 'x');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Create' }));
    expect(await within(dialog).findByText('The title is too long.')).toBeInTheDocument();
  });

  it('restores a deleted document, asking for a folder when the original one is gone', async () => {
    let first = true;
    const { calls } = fakeApi(
      routes({
        'POST /api/documents/(\\d+)/restore': () => {
          if (first) {
            first = false;
            return new Reply(409, { type: 'folder-missing', title: 'Folder missing' });
          }
          return { id: 102 };
        },
      }),
    );
    renderApp();
    await screen.findByText('Supply agreement');
    await userEvent.click(screen.getByLabelText('Show deleted'));
    await screen.findByText('Old lease');

    await userEvent.click(within(card('Old lease')).getByRole('button', { name: 'Restore' }));
    const picker = await screen.findByRole('dialog', { name: /original folder is gone/ });
    await userEvent.click(within(picker).getByRole('button', { name: 'Policies' }));

    await waitFor(() =>
      expect(calls.filter((c) => c.path === '/api/documents/102/restore').map((c) => c.body)).toEqual([
        { folderId: null },
        { folderId: 11 },
      ]),
    );
  });

  it('lets an admin move a deleted document (to empty a folder) but not the owner', async () => {
    const { calls } = fakeApi(routes({ 'POST /api/documents/(\\d+)/move': () => ({ id: 102 }) }));
    renderApp();
    await screen.findByText('Supply agreement');
    await userEvent.click(screen.getByLabelText('Show deleted'));
    await screen.findByText('Old lease');

    await userEvent.click(within(card('Old lease')).getByRole('button', { name: 'Actions for Old lease' }));
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Move to…' }));
    await userEvent.click(
      within(await screen.findByRole('dialog', { name: /Move "Old lease"/ })).getByRole('button', { name: 'Policies' }),
    );
    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/documents/102/move')?.body).toEqual({
        folderId: 11,
        rowVersion: 'rv102',
      }),
    );
  });

  it('moves a document to another folder with its row version', async () => {
    localStorage.setItem('dochub.actingUserId', String(alice.id));
    const { calls } = fakeApi(routes({ 'POST /api/documents/(\\d+)/move': () => ({ id: 100 }) }));
    renderApp();
    await screen.findByText('Supply agreement');
    expect(within(card('Signed NDA')).queryByRole('button', { name: /Actions for/ })).not.toBeInTheDocument();
    await userEvent.click(screen.getByLabelText('Show deleted'));
    await screen.findByText('Old lease');
    expect(within(card('Old lease')).queryByRole('button', { name: /Actions for/ })).not.toBeInTheDocument();
    expect(within(card('Old lease')).getByRole('button', { name: 'Restore' })).toBeInTheDocument();

    await userEvent.click(
      within(card('Supply agreement')).getByRole('button', { name: 'Actions for Supply agreement' }),
    );
    await userEvent.click(await screen.findByRole('menuitem', { name: 'Move to…' }));
    const picker = await screen.findByRole('dialog', { name: /Move "Supply agreement"/ });
    await userEvent.click(within(picker).getByRole('button', { name: 'Policies' }));

    await waitFor(() =>
      expect(calls.find((c) => c.path === '/api/documents/100/move')?.body).toEqual({
        folderId: 11,
        rowVersion: 'rv100',
      }),
    );
  });
});
