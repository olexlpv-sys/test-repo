import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { ModalsProvider } from '@mantine/modals';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it } from 'vitest';
import type { ReactNode } from 'react';
import { SessionContext, type Session } from '../app/sessionContext';
import { fakeApi } from '../test/fakeApi';
import { theme } from '../theme';
import type { DiffBlock, MyRoles, VersionHeader } from './api';
import { CommentsPanel } from './CommentsPanel';
import { diffSide } from './diffSide';
import { PermissionsDialog } from './PermissionsDialog';

const me = (id: number, name: string) => ({
  id,
  login: name.toLowerCase(),
  displayName: name,
  email: null,
  isAdmin: false,
});

function wrap(children: ReactNode, userId = 4) {
  const session: Session = {
    testMode: true,
    userId,
    me: me(userId, userId === 2 ? 'Alice' : 'Carol'),
    recentUserIds: [],
    actAs: async () => {},
  };
  return render(
    <MantineProvider theme={theme} env="test">
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ModalsProvider>
          <SessionContext.Provider value={session}>{children}</SessionContext.Provider>
        </ModalsProvider>
      </QueryClientProvider>
    </MantineProvider>,
  );
}

const roles = (extra: Partial<MyRoles>): MyRoles => ({
  isOwner: false,
  isAdmin: false,
  isEditor: false,
  isApprover: false,
  editorNodeScopes: [],
  canEditStructure: false,
  canEditAllContent: false,
  editableLogicalNodeIds: [],
  canComment: false,
  canResolve: false,
  canSign: false,
  canManage: false,
  canMove: false,
  canRestore: false,
  ...extra,
});

const draft = {
  id: 11,
  documentId: 7,
  rowVersion: 'v',
  status: 'Draft',
  versionNumber: null,
  label: 'Draft',
  createdAt: '2026-09-01T00:00:00Z',
  createdBy: { id: 2, displayName: 'Alice' },
  signedAt: null,
  signedBy: [],
  basedOnVersionId: 10,
  isCurrent: true,
  modifiedAfterSigning: false,
} as VersionHeader;

const comment = (id: number, authorId: number, author: string, body: string, extra: Record<string, unknown> = {}) => ({
  id,
  versionId: 11,
  versionLabel: 'Draft',
  logicalNodeId: null,
  nodeTitle: null,
  author: { id: authorId, displayName: author },
  body,
  isDeleted: false,
  createdAt: '2026-09-02T10:00:00Z',
  editedAt: null,
  resolvedAt: null,
  resolvedBy: null,
  rowVersion: `c${id}`,
  replies: [],
  ...extra,
});

describe('side-by-side diff', () => {
  it('keeps deletions on the base side and insertions on the target side', () => {
    const blocks: DiffBlock[] = [
      {
        type: 'paragraph',
        status: 'changed',
        ops: [
          { op: 'equal', text: 'The ' },
          { op: 'delete', text: 'old' },
          { op: 'insert', text: 'new' },
        ],
      },
      { type: 'paragraph', status: 'inserted', ops: [{ op: 'insert', text: 'Added.' }] },
      { type: 'paragraph', status: 'deleted', ops: [{ op: 'delete', text: 'Gone.' }] },
    ];
    expect(diffSide(blocks, 'base').map((b) => b.ops?.map((o) => o.text).join(''))).toEqual(['The old', 'Gone.']);
    expect(diffSide(blocks, 'target').map((b) => b.ops?.map((o) => o.text).join(''))).toEqual(['The new', 'Added.']);
  });
});

describe('comments panel', () => {
  it('offers edit to the author, delete to the author, resolve to approvers — and nothing on a comment by someone else', async () => {
    fakeApi({
      'GET /api/versions/11/comments': (r) =>
        r.query.get('scope') === 'document'
          ? [
              comment(1, 4, 'Carol', 'Mine.'),
              comment(2, 2, 'Alice', 'The owner’s.', { replies: [comment(3, 4, 'Carol', 'A reply.')] }),
            ]
          : [],
    });
    wrap(
      <CommentsPanel
        documentId={7}
        version={draft}
        roles={roles({ isApprover: true, canComment: true, canResolve: true })}
        node={null}
        onClose={() => {}}
      />,
    );

    const mine = await screen.findByTestId('comment-1');
    expect(within(mine).getByRole('button', { name: 'Edit' })).toBeInTheDocument();
    expect(within(mine).getByRole('button', { name: 'Delete' })).toBeInTheDocument();
    expect(within(mine).getByRole('button', { name: 'Resolve' })).toBeInTheDocument();
    const theirs = screen.getByTestId('comment-2');
    expect(within(theirs).queryByRole('button', { name: 'Edit' })).not.toBeInTheDocument();
    expect(within(theirs).queryByRole('button', { name: 'Delete' })).not.toBeInTheDocument();
    expect(within(theirs).getByRole('button', { name: 'Resolve' })).toBeInTheDocument();
    // Replies are never resolved on their own.
    expect(within(screen.getByTestId('comment-3')).queryByRole('button', { name: 'Resolve' })).not.toBeInTheDocument();
  });

  it('shows deleted comments as such and the reply box only on open threads of this version', async () => {
    fakeApi({
      'GET /api/versions/11/comments': (r) =>
        r.query.get('scope') === 'document'
          ? [
              comment(1, 4, 'Carol', '', { isDeleted: true, body: null }),
              comment(2, 4, 'Carol', 'Resolved one.', {
                resolvedAt: '2026-09-03T00:00:00Z',
                resolvedBy: { id: 2, displayName: 'Alice' },
              }),
              comment(3, 4, 'Carol', 'From v1.', { versionId: 10, versionLabel: 'v1' }),
            ]
          : [],
    });
    wrap(
      <CommentsPanel
        documentId={7}
        version={draft}
        roles={roles({ canComment: true })}
        node={null}
        onClose={() => {}}
      />,
    );
    expect(await screen.findByText('Comment deleted')).toBeInTheDocument();
    expect(screen.getByTestId('comment-2')).toHaveTextContent('resolved by Alice');
    expect(screen.getByTestId('comment-3')).toHaveTextContent('v1');
    expect(within(screen.getByTestId('thread-2')).queryByRole('group', { name: /Reply to/ })).not.toBeInTheDocument();
    expect(within(screen.getByTestId('thread-3')).queryByRole('group', { name: /Reply to/ })).not.toBeInTheDocument();
  });
});

describe('permissions dialog', () => {
  const permissions = {
    owner: { id: 2, displayName: 'Alice' },
    grants: [
      {
        id: 9,
        user: { id: 3, displayName: 'Bob' },
        role: 'Editor',
        logicalNodeId: 'l1',
        nodeTitle: 'Old chapter',
        nodeInCurrentVersion: false,
        grantedAt: '2026-09-01T00:00:00Z',
        grantedBy: { id: 2, displayName: 'Alice' },
      },
    ],
  };
  const users = [me(2, 'Alice'), me(3, 'Bob'), me(4, 'Carol')].map((u) => ({ ...u, isActive: true }));

  it('never offers the owner and warns about grants on nodes not in the current draft', async () => {
    fakeApi({
      'GET /api/documents/7/permissions': () => permissions,
      'GET /api/users': () => ({ items: users, page: 1, pageSize: 20, totalCount: 3 }),
    });
    wrap(<PermissionsDialog documentId={7} opened onClose={() => {}} canManage tree={[]} />, 2);
    expect(await screen.findByText(/not in current draft/)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('combobox', { name: 'User' }));
    await waitFor(() => expect(screen.getByRole('option', { name: /Bob/ })).toBeInTheDocument());
    expect(screen.queryByRole('option', { name: /Alice/ })).not.toBeInTheDocument();
  });

  it('is read-only for everyone but the owner', async () => {
    fakeApi({ 'GET /api/documents/7/permissions': () => permissions });
    wrap(<PermissionsDialog documentId={7} opened onClose={() => {}} canManage={false} tree={[]} />);
    expect(await screen.findByText('Only the owner changes roles.')).toBeInTheDocument();
    expect(await screen.findByTestId('grant-9')).toHaveTextContent('Bob');
    expect(screen.queryByRole('button', { name: /Remove/ })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Add' })).not.toBeInTheDocument();
  });
});
