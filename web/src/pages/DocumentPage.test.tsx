import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it } from 'vitest';
import { alice, baseRoutes, fakeApi, Reply } from '../test/fakeApi';
import { renderApp } from '../test/render';

const carol = { id: 4, displayName: 'Carol' };
const paragraph = (text: string) => ({
  type: 'doc',
  content: [{ type: 'paragraph', content: [{ type: 'text', text }] }],
});
const rule = (kind: string) => ({ kind, min: null, max: null, values: null, default: null });

function versionHeader(id: number, status: string, versionNumber: number | null, extra: Record<string, unknown> = {}) {
  return {
    id,
    documentId: 7,
    rowVersion: `v${id}`,
    status,
    versionNumber,
    label: versionNumber ? `v${versionNumber}` : 'Draft',
    createdAt: '2026-09-01T08:00:00Z',
    createdBy: { id: 2, displayName: 'Alice Author' },
    signedAt: versionNumber ? '2026-09-01T09:00:00Z' : null,
    signedBy: versionNumber ? [carol] : [],
    basedOnVersionId: versionNumber ? null : 10,
    isCurrent: true,
    modifiedAfterSigning: false,
    ...extra,
  };
}

function roles(extra: Record<string, unknown> = {}) {
  return {
    isOwner: false,
    isAdmin: false,
    isEditor: false,
    isApprover: false,
    editorNodeScopes: [],
    canEditStructure: false,
    canEditAllContent: false,
    editableLogicalNodeIds: [],
    canComment: true,
    canResolve: false,
    canSign: false,
    canManage: false,
    canMove: false,
    canRestore: false,
    ...extra,
  };
}

const node = {
  id: 101,
  logicalNodeId: 'aaaaaaaa-0000-0000-0000-000000000001',
  nodeTypeId: 1,
  title: 'Scope',
  number: '1',
  hasContent: true,
  sortOrder: 1,
  rowVersion: 'n1',
  children: [],
};

function routes(
  options: {
    versions?: unknown[];
    myRoles?: Record<string, unknown>;
    overrides?: Record<string, Parameters<typeof fakeApi>[0][string]>;
  } = {},
) {
  const versions = options.versions ?? [versionHeader(10, 'Signed', 1), versionHeader(11, 'Draft', null)];
  return {
    ...baseRoutes(),
    'GET /api/documents/7': () => ({
      id: 7,
      rowVersion: 'd',
      folderId: 1,
      title: 'NDA with Contoso',
      status: 'Active',
      owner: { id: 2, displayName: 'Alice Author' },
      createdAt: '2026-09-01T08:00:00Z',
      deletedAt: null,
      versions,
      myRoles: roles(options.myRoles),
    }),
    'GET /api/documents/7/my-permissions': () => roles(options.myRoles),
    'GET /api/versions/(\\d+)/tree': () => [node],
    'GET /api/content-schema': () => ({
      version: 1,
      fontFamilies: ['Arial'],
      nodes: {
        doc: { attributes: {}, children: ['paragraph'] },
        paragraph: { attributes: { align: rule('Enum') }, children: ['text'] },
        text: { attributes: {}, children: null },
      },
      marks: { bold: { attributes: {} } },
    }),
    'GET /api/content-styles': () => [],
    'GET /api/content-styles/stylesheet.css': () => '',
    'GET /api/node-types': () => [
      {
        id: 1,
        code: 'SECTION',
        name: 'Section',
        description: null,
        sortOrder: 1,
        isActive: true,
        rowVersion: 't',
        usageCount: 1,
      },
    ],
    'GET /api/versions/(\\d+)/signatures': () => ({
      requiredApprovers: [carol],
      signatures: [],
      pendingApprovers: [carol],
      isComplete: false,
    }),
    'GET /api/versions/(\\d+)/change-summary': () => ({
      since: 'latestSigned',
      nodes: [
        {
          logicalNodeId: node.logicalNodeId,
          changeCount: 2,
          lastChangedAt: '2026-09-02T10:15:00Z',
          lastChangedBy: { id: 2, displayName: 'Alice Author' },
          hasScriptChange: true,
          hasChangeAfterSigning: false,
          structural: [],
        },
      ],
      removed: [],
    }),
    'GET /api/documents/7/comments/counts': () => ({ versionId: 11, document: 0, nodes: {} }),
    'GET /api/nodes/101/content': () => ({
      nodeId: 101,
      logicalNodeId: node.logicalNodeId,
      schemaVersion: 1,
      contentJson: paragraph('The new scope.'),
      contentHtml: '<p>The new scope.</p>',
      modifiedAt: '2026-09-02T10:15:00Z',
      modifiedBy: { id: 2, displayName: 'Alice Author' },
      rowVersion: 'c1',
    }),
    'GET /api/documents/7/nodes/[^/]+/history': () => ({
      items: [
        {
          id: 501,
          changedAt: '2026-09-02T10:15:00Z',
          versionId: 11,
          versionLabel: 'Draft',
          kind: 'ContentChanged',
          logicalNodeId: node.logicalNodeId,
          user: { id: 2, displayName: 'Alice Author' },
          source: 'App',
          dbLogin: null,
          ticket: null,
          reason: null,
          summary: 'Content changed (+1 / −1 words)',
          changes: [],
          hasContentDiff: true,
          afterSigning: false,
        },
        {
          id: 500,
          changedAt: '2026-09-02T09:02:00Z',
          versionId: 11,
          versionLabel: 'Draft',
          kind: 'ContentChanged',
          logicalNodeId: node.logicalNodeId,
          user: null,
          source: 'Script',
          dbLogin: 'jdoe',
          ticket: 'INC-1234',
          reason: 'fix typo',
          summary: 'Content changed (+1 / −0 words)',
          changes: [],
          hasContentDiff: true,
          afterSigning: false,
        },
      ],
      page: 1,
      pageSize: 20,
      totalCount: 2,
    }),
    'GET /api/history/entries/500/content': () => ({
      entryId: 500,
      logicalNodeId: node.logicalNodeId,
      versionId: 11,
      changedAt: '2026-09-02T09:02:00Z',
      title: 'Scope',
      nodeTypeId: 1,
      contentJson: paragraph('The old scope.'),
      contentHtml: '<p>The old scope.</p>',
    }),
    'GET /api/documents/7/nodes/[^/]+/changes': () => ({
      html: '',
      stats: { inserted: 1, deleted: 1 },
      blocks: [
        {
          type: 'paragraph',
          status: 'changed',
          ops: [
            { op: 'equal', text: 'The ' },
            {
              op: 'delete',
              text: 'old',
              by: {
                entryId: 500,
                userId: null,
                displayName: 'jdoe',
                source: 'Script',
                ticket: 'INC-1234',
                changedAt: '2026-09-02T09:02:00Z',
              },
            },
            {
              op: 'insert',
              text: 'new',
              by: {
                entryId: 501,
                userId: 2,
                displayName: 'Alice Author',
                source: 'App',
                ticket: null,
                changedAt: '2026-09-02T10:15:00Z',
              },
            },
            { op: 'equal', text: ' scope.' },
          ],
        },
      ],
    }),
    ...options.overrides,
  };
}

describe('document form (T15)', () => {
  it('opens a signed version read-only, without restore, and shows the section history with script changes', async () => {
    localStorage.setItem('dochub.actingUserId', String(alice.id));
    fakeApi(
      routes({ versions: [versionHeader(10, 'Signed', 1)], myRoles: { isEditor: true, canEditAllContent: true } }),
    );
    renderApp('/documents/7');

    expect(await screen.findByText('v1 is signed and read-only.')).toBeInTheDocument();
    const section = await screen.findByTestId('section-101');
    await waitFor(() =>
      expect(within(section).getByLabelText('Section text')).toHaveAttribute('contenteditable', 'false'),
    );
    expect(within(section).getByLabelText('Script or after-signing change')).toBeInTheDocument();

    await userEvent.click(within(section).getByRole('button', { name: 'History of Scope' }));
    const history = await within(section).findByTestId(`history-${node.logicalNodeId}`);
    expect(await within(history).findByText('Script · jdoe · INC-1234')).toBeInTheDocument();
    expect(within(history).getByText(/v1 signed/)).toHaveTextContent('Carol');
    expect(within(history).queryByRole('button', { name: 'Restore this text' })).not.toBeInTheDocument();

    // View: the text as it was; Compare: attributed track changes.
    await userEvent.click(within(history).getAllByRole('button', { name: 'View' })[1] as HTMLElement);
    expect(await within(section).findByText('The old scope.')).toBeInTheDocument();
    await userEvent.click(within(history).getAllByRole('button', { name: 'Compare' })[1] as HTMLElement);
    const changes = await within(section).findByTestId('track-changes');
    expect(within(changes).getByText('new')).toHaveAttribute(
      'title',
      expect.stringMatching(/^Inserted by Alice Author/),
    );
    expect(within(changes).getByText('old')).toHaveAttribute(
      'title',
      expect.stringMatching(/^Deleted by Script · jdoe · INC-1234/),
    );
    await userEvent.click(within(section).getByRole('button', { name: 'Back to current text' }));
    expect(await within(section).findByLabelText('Section text')).toBeInTheDocument();
  });

  it('a "changes since" date means the start of that local day', async () => {
    const originalTz = process.env.TZ;
    process.env.TZ = 'Europe/Berlin';
    try {
      localStorage.setItem('dochub.actingUserId', String(alice.id));
      const { calls } = fakeApi(
        routes({ versions: [versionHeader(10, 'Signed', 1)], myRoles: { isEditor: true, canEditAllContent: true } }),
      );
      renderApp('/documents/7');
      await screen.findByTestId('section-101');

      await userEvent.click(screen.getAllByLabelText('Changes since')[0] as HTMLElement);
      await userEvent.click(await screen.findByRole('option', { name: 'A date…' }));
      // A mistyped 5-digit year is ignored (it used to blank the page), the corrected date is used.
      fireEvent.change(screen.getByLabelText('Since date'), { target: { value: '20266-09-26' } });
      expect(screen.getByTestId('section-101')).toBeInTheDocument();
      fireEvent.change(screen.getByLabelText('Since date'), { target: { value: '2026-09-26' } });

      await waitFor(() =>
        expect(
          calls.some(
            (c) => c.path.endsWith('/change-summary') && c.query.get('since') === 'd:2026-09-25T22:00:00.000Z',
          ),
        ).toBe(true),
      );
      expect(calls.some((c) => c.query.get('since') === 'd:2026-09-26')).toBe(false);
    } finally {
      process.env.TZ = originalTz;
    }
  });

  it('offers Sign to an approver only; the last signature switches the view to the new version', async () => {
    localStorage.setItem('dochub.actingUserId', '4');
    let signed = false;
    const { calls } = fakeApi({
      ...routes({ myRoles: { isApprover: true, canSign: true } }),
      'GET /api/me': () => ({ id: 4, login: 'carol', displayName: 'Carol', email: null, isAdmin: false }),
      'GET /api/users/(\\d+)': () => ({
        id: 4,
        login: 'carol',
        displayName: 'Carol',
        email: null,
        isAdmin: false,
        isActive: true,
      }),
      'GET /api/documents/7': () => ({
        id: 7,
        rowVersion: 'd',
        folderId: 1,
        title: 'NDA with Contoso',
        status: 'Active',
        owner: { id: 2, displayName: 'Alice Author' },
        createdAt: '2026-09-01T08:00:00Z',
        deletedAt: null,
        versions: signed
          ? [versionHeader(10, 'Signed', 1, { isCurrent: false }), versionHeader(11, 'Signed', 2)]
          : [versionHeader(10, 'Signed', 1), versionHeader(11, 'Draft', null)],
        myRoles: roles({ isApprover: true, canSign: true }),
      }),
      'POST /api/versions/11/signatures': () => {
        signed = true;
        return {
          signatures: { requiredApprovers: [carol], signatures: [], pendingApprovers: [], isComplete: true },
          version: versionHeader(11, 'Signed', 2),
        };
      },
    });
    renderApp('/documents/7');

    await userEvent.click(await screen.findByRole('button', { name: 'Sign' }));
    const dialog = await screen.findByRole('dialog', { name: /Sign Draft/ });
    expect(dialog).toHaveTextContent('Your signature confirms the current content');
    await userEvent.type(within(dialog).getByLabelText('Note (optional)'), 'Looks good');
    await userEvent.click(within(dialog).getByRole('button', { name: 'Sign' }));

    expect(await screen.findByText('Signed as v2')).toBeInTheDocument();
    await waitFor(() => expect(window.location.search).toContain('version=11'));
    expect(calls.find((c) => c.method === 'POST')?.body).toEqual({ comment: 'Looks good' });
    expect(await screen.findByText('v2 is signed and read-only.')).toBeInTheDocument();
  });

  it('never shows Sign to the owner and warns that there are no approvers', async () => {
    localStorage.setItem('dochub.actingUserId', String(alice.id));
    fakeApi(
      routes({
        myRoles: { isOwner: true, canEditStructure: true, canEditAllContent: true, canManage: true },
        overrides: {
          'GET /api/versions/(\\d+)/signatures': () => ({
            requiredApprovers: [],
            signatures: [],
            pendingApprovers: [],
            isComplete: false,
          }),
        },
      }),
    );
    renderApp('/documents/7');
    expect(await screen.findByTestId('signature-progress')).toHaveTextContent('Signatures 0 / 0');
    expect(screen.getAllByText('Add an approver to sign.').length).toBeGreaterThan(0);
    expect(screen.queryByRole('button', { name: 'Sign' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Discard draft' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: '+ Node' })).toBeInTheDocument();
    const section = await screen.findByTestId('section-101');
    await waitFor(() =>
      expect(within(section).getByLabelText('Section text')).toHaveAttribute('contenteditable', 'true'),
    );
  });

  it('says so when the document cannot be opened', async () => {
    fakeApi({
      ...baseRoutes(),
      'GET /api/documents/7': () => new Reply(404, { title: 'Not found' }),
      'GET /api/documents/7/my-permissions': () => new Reply(404, { title: 'Not found' }),
      'GET /api/content-schema': () => ({}),
    });
    renderApp('/documents/7');
    expect(await screen.findByText('The document could not be opened.')).toBeInTheDocument();
  });
});
