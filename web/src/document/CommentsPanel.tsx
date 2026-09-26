import { useState } from 'react';
import { ActionIcon, Button, Group, Stack, Switch, Text, Textarea, Tooltip } from '@mantine/core';
import { modals } from '@mantine/modals';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../api/client';
import { useSession } from '../app/sessionContext';
import type { MyRoles, TreeNode, VersionHeader } from './api';

type Comment = Schemas['CommentView'];

interface Props {
  documentId: number;
  version: VersionHeader;
  roles: MyRoles | undefined;
  /** The node whose threads are shown under the document threads (the selected section). */
  node: TreeNode | null;
  onClose: () => void;
}

const commentsKey = (documentId: number) => ['doc', documentId, 'comments'] as const;

/** Threads of the document (`logicalNodeId` null) or of one node ('' = none selected: not loaded). */
function useThreads(
  documentId: number,
  versionId: number,
  logicalNodeId: string | null,
  include: { showResolved: boolean; previous: boolean },
) {
  const scope = logicalNodeId === null ? 'document' : 'node';
  return useQuery({
    queryKey: [
      ...commentsKey(documentId),
      versionId,
      scope,
      logicalNodeId ?? '',
      include.showResolved,
      include.previous,
    ],
    queryFn: () =>
      unwrap(
        api.GET('/api/versions/{versionId}/comments', {
          params: {
            path: { versionId },
            query: {
              scope,
              logicalNodeId: logicalNodeId ?? undefined,
              includeResolved: include.showResolved,
              includePreviousVersions: include.previous,
            },
          },
        }),
      ),
    enabled: logicalNodeId !== '',
  });
}

/** Comments (T16 §2, FR-CM1/CM2): document-level threads and the selected node's threads; reply, edit, delete, resolve. */
export function CommentsPanel({ documentId, version, roles, node, onClose }: Props) {
  const queryClient = useQueryClient();
  const [showResolved, setShowResolved] = useState(true);
  const [previous, setPrevious] = useState(false);
  const isDraft = version.status === 'Draft';
  const canComment = (roles?.canComment ?? false) && version.status !== 'Deleted';

  const include = { showResolved, previous: previous && isDraft };
  const documentThreads = useThreads(documentId, version.id, null, include);
  const nodeThreads = useThreads(documentId, version.id, node?.logicalNodeId ?? '', include);

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: commentsKey(documentId) });
    await queryClient.invalidateQueries({ queryKey: ['doc', documentId, 'comment-counts'] });
  };

  return (
    <section className="dh-card dh-comments" aria-label="Comments">
      <div className="dh-card-header">
        <span className="dh-card-title">💬 Comments</span>
        <ActionIcon variant="soft" color="gray" aria-label="Close comments" onClick={onClose}>
          ✕
        </ActionIcon>
      </div>
      <div className="dh-comments-body">
        <Group gap={16} mb={12}>
          <Switch
            size="xs"
            label="Show resolved"
            checked={showResolved}
            onChange={(e) => setShowResolved(e.currentTarget.checked)}
          />
          {isDraft && (
            <Switch
              size="xs"
              label="From previous versions"
              checked={previous}
              onChange={(e) => setPrevious(e.currentTarget.checked)}
            />
          )}
        </Group>

        <Text fw={500} size="sm" mb={6}>
          Document
        </Text>
        <Threads
          list={documentThreads.data}
          loading={documentThreads.isPending}
          roles={roles}
          version={version}
          onChanged={refresh}
        />
        <NewComment
          version={version}
          canComment={canComment}
          logicalNodeId={null}
          label="Comment on the document"
          onAdded={refresh}
        />

        {node && (
          <>
            <Text fw={500} size="sm" mt={18} mb={6}>
              {node.number} {node.title}
            </Text>
            <Threads
              list={nodeThreads.data}
              loading={nodeThreads.isPending}
              roles={roles}
              version={version}
              onChanged={refresh}
            />
            <NewComment
              version={version}
              canComment={canComment}
              logicalNodeId={node.logicalNodeId}
              label={`Comment on ${node.number} ${node.title}`}
              onAdded={refresh}
            />
          </>
        )}
        {!node && (
          <Text size="sm" className="dh-muted" mt={16}>
            Select a section to see and add its comments.
          </Text>
        )}
      </div>
    </section>
  );
}

function NewComment({
  version,
  canComment,
  logicalNodeId,
  label,
  onAdded,
  parentCommentId,
}: {
  version: VersionHeader;
  canComment: boolean;
  logicalNodeId: string | null;
  label: string;
  onAdded: () => Promise<void>;
  parentCommentId?: number;
}) {
  const [body, setBody] = useState('');
  const add = useMutation({
    mutationFn: () =>
      unwrap(
        api.POST('/api/versions/{versionId}/comments', {
          params: { path: { versionId: version.id } },
          body: { body: body.trim(), logicalNodeId, parentCommentId: parentCommentId ?? null },
        }),
      ),
    onSuccess: async () => {
      setBody('');
      await onAdded();
    },
  });
  const input = (
    <Textarea
      aria-label={label}
      placeholder={parentCommentId ? 'Reply…' : `${label}…`}
      autosize
      minRows={parentCommentId ? 1 : 2}
      maxLength={4000}
      value={body}
      disabled={!canComment}
      onChange={(e) => setBody(e.currentTarget.value)}
    />
  );
  return (
    <Stack gap={6} mt={8} role="group" aria-label={label}>
      {canComment ? (
        input
      ) : (
        <Tooltip
          label={
            version.status === 'Deleted'
              ? 'Discarded versions take no comments'
              : 'Only the owner, editors and approvers can comment'
          }
        >
          <div>{input}</div>
        </Tooltip>
      )}
      {canComment && body.trim() && (
        <Group justify="flex-end">
          <Button size="compact-sm" loading={add.isPending} onClick={() => add.mutate()}>
            {parentCommentId ? 'Reply' : 'Comment'}
          </Button>
        </Group>
      )}
    </Stack>
  );
}

function Threads({
  list,
  loading,
  roles,
  version,
  onChanged,
}: {
  list: Comment[] | undefined;
  loading: boolean;
  roles: MyRoles | undefined;
  version: VersionHeader;
  onChanged: () => Promise<void>;
}) {
  if (loading) {
    return (
      <Text size="sm" className="dh-muted">
        Loading…
      </Text>
    );
  }

  if (!list || list.length === 0) {
    return (
      <Text size="sm" className="dh-muted">
        No comments yet.
      </Text>
    );
  }

  return (
    <>
      {list.map((thread) => (
        <div
          key={thread.id}
          className="dh-thread"
          data-resolved={thread.resolvedAt ? true : undefined}
          data-testid={`thread-${thread.id}`}
        >
          <CommentItem comment={thread} top roles={roles} version={version} onChanged={onChanged} />
          {thread.replies.map((reply) => (
            <div key={reply.id} className="dh-reply">
              <CommentItem comment={reply} top={false} roles={roles} version={version} onChanged={onChanged} />
            </div>
          ))}
          {!thread.resolvedAt && !thread.isDeleted && thread.versionId === version.id && (
            <NewComment
              version={version}
              canComment={(roles?.canComment ?? false) && version.status !== 'Deleted'}
              logicalNodeId={thread.logicalNodeId}
              parentCommentId={thread.id}
              label={`Reply to ${thread.author.displayName}`}
              onAdded={onChanged}
            />
          )}
        </div>
      ))}
    </>
  );
}

function CommentItem({
  comment,
  top,
  roles,
  version,
  onChanged,
}: {
  comment: Comment;
  top: boolean;
  roles: MyRoles | undefined;
  version: VersionHeader;
  onChanged: () => Promise<void>;
}) {
  const { me } = useSession();
  const [editing, setEditing] = useState<string | null>(null);
  const mine = comment.author.id === me?.id;
  const writable = version.status !== 'Deleted' && comment.versionId === version.id && !comment.isDeleted;

  const update = useMutation({
    mutationFn: (body: string) =>
      unwrap(
        api.PUT('/api/comments/{id}', {
          params: { path: { id: comment.id } },
          body: { body, rowVersion: comment.rowVersion },
        }),
      ),
    onSuccess: async () => {
      setEditing(null);
      await onChanged();
    },
  });
  const remove = useMutation({
    mutationFn: () =>
      unwrap(
        api.DELETE('/api/comments/{id}', {
          params: { path: { id: comment.id }, query: { rowVersion: comment.rowVersion } },
        }),
      ),
    onSuccess: onChanged,
  });
  const resolve = useMutation({
    mutationFn: (done: boolean) =>
      done
        ? unwrap(api.POST('/api/comments/{id}/resolve', { params: { path: { id: comment.id } } }))
        : unwrap(api.POST('/api/comments/{id}/reopen', { params: { path: { id: comment.id } } })),
    onSuccess: onChanged,
  });

  return (
    <div data-testid={`comment-${comment.id}`}>
      <div className="dh-comment-meta">
        <b>{comment.author.displayName}</b> · {new Date(comment.createdAt).toLocaleString()}
        {comment.editedAt && ' · edited'}
        {comment.versionId !== version.id && comment.versionLabel && ` · ${comment.versionLabel}`}
        {top && comment.resolvedAt && ` · resolved by ${comment.resolvedBy?.displayName ?? '?'}`}
      </div>
      {editing !== null ? (
        <Stack gap={6}>
          <Textarea
            aria-label="Edit comment"
            autosize
            minRows={2}
            maxLength={4000}
            value={editing}
            onChange={(e) => setEditing(e.currentTarget.value)}
          />
          <Group gap={6} justify="flex-end">
            <Button size="compact-xs" variant="soft" color="gray" onClick={() => setEditing(null)}>
              Cancel
            </Button>
            <Button
              size="compact-xs"
              loading={update.isPending}
              disabled={!editing.trim()}
              onClick={() => update.mutate(editing.trim())}
            >
              Save
            </Button>
          </Group>
        </Stack>
      ) : (
        <div className="dh-comment-body">
          {comment.isDeleted ? <i className="dh-muted">Comment deleted</i> : comment.body}
        </div>
      )}
      {writable && editing === null && (
        <Group gap={4}>
          {mine && (
            <Button size="compact-xs" variant="subtle" color="gray" onClick={() => setEditing(comment.body ?? '')}>
              Edit
            </Button>
          )}
          {(mine || roles?.canManage) && (
            <Button
              size="compact-xs"
              variant="subtle"
              color="red"
              onClick={() =>
                modals.openConfirmModal({
                  title: 'Delete this comment?',
                  labels: { confirm: 'Delete', cancel: 'Cancel' },
                  confirmProps: { color: 'red', variant: 'soft' },
                  groupProps: { grow: true },
                  onConfirm: () => remove.mutate(),
                })
              }
            >
              Delete
            </Button>
          )}
          {top && roles?.canResolve && (
            <Button size="compact-xs" variant="subtle" onClick={() => resolve.mutate(!comment.resolvedAt)}>
              {comment.resolvedAt ? 'Reopen' : 'Resolve'}
            </Button>
          )}
        </Group>
      )}
    </div>
  );
}
