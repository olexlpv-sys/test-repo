import { useState } from 'react';
import { Button, Drawer, Group, Modal, Popover, Select, Stack, Text, Textarea, Tooltip } from '@mantine/core';
import { modals } from '@mantine/modals';
import { notifications } from '@mantine/notifications';
import { useInfiniteQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { api, unwrap } from '../api/client';
import { useSession } from '../app/sessionContext';
import { type DocumentDetails, type MyRoles, type SignatureStatus, type VersionHeader } from './api';
import { describeEntry, sourceLabel } from './historyText';
import { approverStates, versionLabel } from './versions';

interface Props {
  document: DocumentDetails;
  version: VersionHeader;
  roles: MyRoles | undefined;
  signatures: SignatureStatus | undefined;
  onSelectVersion: (versionId: number) => void;
  onPermissions: () => void;
  commentsOpen: boolean;
  onToggleComments: () => void;
  /** Open comments on the whole document (badge on the Comments button). */
  documentComments: number;
}

/** Version selector, read-only and ⚠ banners, signing panel, New draft / Discard draft, activity feed (T15 §1). */
export function VersionBar({
  document,
  version,
  roles,
  signatures,
  onSelectVersion,
  onPermissions,
  commentsOpen,
  onToggleComments,
  documentComments,
}: Props) {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { me } = useSession();
  const [signing, setSigning] = useState(false);
  const [note, setNote] = useState('');
  const [activity, setActivity] = useState(false);
  const isDraft = version.status === 'Draft';
  const draft = document.versions.find((v) => v.status === 'Draft');
  const hasSigned = document.versions.some((v) => v.status === 'Signed');
  const states = signatures ? approverStates(signatures) : [];
  const valid = states.filter((s) => s.state === 'signed').length;
  const mine = states.find((s) => s.id === me?.id);

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['doc', document.id] });
    await queryClient.invalidateQueries({ queryKey: ['documents'] });
  };

  const sign = useMutation({
    mutationFn: () =>
      unwrap(
        api.POST('/api/versions/{versionId}/signatures', {
          params: { path: { versionId: version.id } },
          body: { comment: note.trim() || null },
        }),
      ),
    onSuccess: async (result) => {
      setSigning(false);
      setNote('');
      await refresh();
      if (result.version.status === 'Signed') {
        notifications.show({
          color: 'green',
          title: `Signed as ${result.version.label}`,
          message: 'Every approver has signed.',
        });
        onSelectVersion(result.version.id);
      }
    },
  });

  const withdraw = useMutation({
    mutationFn: () =>
      unwrap(api.DELETE('/api/versions/{versionId}/signatures/mine', { params: { path: { versionId: version.id } } })),
    onSuccess: refresh,
  });

  const newDraft = useMutation({
    mutationFn: () => unwrap(api.POST('/api/documents/{id}/drafts', { params: { path: { id: document.id } } })),
    onSuccess: async (created) => {
      await refresh();
      onSelectVersion(created.id);
    },
  });

  const discard = useMutation({
    mutationFn: () =>
      unwrap(
        api.DELETE('/api/versions/{versionId}', {
          params: { path: { versionId: version.id }, query: { rowVersion: version.rowVersion } },
        }),
      ),
    onSuccess: async () => {
      await refresh();
      const latest = document.versions
        .filter((v) => v.status === 'Signed')
        .sort((a, b) => (b.versionNumber ?? 0) - (a.versionNumber ?? 0))[0];
      if (latest) {
        onSelectVersion(latest.id);
      }
    },
  });

  return (
    <div className="dh-versionbar">
      <Group justify="space-between" wrap="wrap" gap={12}>
        <Group gap={12} wrap="wrap">
          <Button variant="soft" color="gray" onClick={() => navigate(`/?folder=${document.folderId}`)}>
            ← Back
          </Button>
          <span className="dh-section-title" data-testid="document-title">
            {document.title}
          </span>
          <Select
            aria-label="Version"
            w={230}
            allowDeselect={false}
            data={document.versions
              .filter((v) => v.status !== 'Deleted' || v.id === version.id)
              .map((v) => ({
                value: String(v.id),
                label: `${versionLabel(v, document.versions)}${v.status === 'Deleted' ? ' (discarded)' : ''}`,
              }))}
            value={String(version.id)}
            onChange={(value) => value && onSelectVersion(Number(value))}
            comboboxProps={{ withinPortal: true }}
          />
          <span
            className="dh-chip"
            data-tone={version.status === 'Signed' ? 'green' : version.status === 'Draft' ? 'amber' : undefined}
          >
            {version.status}
          </span>
          {version.modifiedAfterSigning && (
            <Tooltip label="Changed by a support script after it was signed">
              <span className="dh-chip" data-tone="red">
                ⚠ Modified after signing
              </span>
            </Tooltip>
          )}
          {isDraft && signatures && (
            <Popover withinPortal position="bottom-start">
              <Popover.Target>
                <Button variant="soft" data-testid="signature-progress">
                  Signatures {valid} / {signatures.requiredApprovers.length}
                </Button>
              </Popover.Target>
              <Popover.Dropdown>
                <Stack gap={6} miw={260}>
                  {states.length === 0 && <Text size="sm">No approvers yet. Add an approver to sign.</Text>}
                  {states.map((s) => (
                    <Group key={s.id} justify="space-between" gap={12} data-testid={`approver-${s.id}`}>
                      <Text size="sm">{s.name}</Text>
                      <Text size="sm" c={s.state === 'signed' ? 'green' : s.state === 'outdated' ? 'orange' : 'dimmed'}>
                        {{ signed: '✔ signed', outdated: '⚠ outdated', pending: '⏳ pending' }[s.state]}
                      </Text>
                    </Group>
                  ))}
                </Stack>
              </Popover.Dropdown>
            </Popover>
          )}
          {isDraft &&
            signatures &&
            signatures.requiredApprovers.length === 0 &&
            (roles?.isOwner || roles?.isEditor) && (
              <Text size="sm" className="dh-muted">
                Add an approver to sign.
              </Text>
            )}
        </Group>
        <Group gap={8} wrap="wrap">
          {isDraft && roles?.canSign && mine && mine.state !== 'signed' && (
            <Button
              onClick={() => {
                // A signature confirms the current content: reload every section's text first.
                void queryClient.invalidateQueries({ queryKey: ['doc', document.id, 'content'] });
                setSigning(true);
              }}
            >
              Sign
            </Button>
          )}
          {isDraft && mine?.state === 'signed' && (
            <Button variant="soft" color="red" loading={withdraw.isPending} onClick={() => withdraw.mutate()}>
              Withdraw signature
            </Button>
          )}
          {roles?.isOwner && !draft && hasSigned && document.status !== 'Deleted' && (
            <Button variant="soft" loading={newDraft.isPending} onClick={() => newDraft.mutate()}>
              New draft
            </Button>
          )}
          {roles?.isOwner && isDraft && (
            <Button
              variant="soft"
              color="red"
              onClick={() =>
                modals.openConfirmModal({
                  title: 'Discard this draft?',
                  children: <Text size="sm">All changes made in the draft are dropped; the signed versions stay.</Text>,
                  labels: { confirm: 'Discard', cancel: 'Cancel' },
                  confirmProps: { color: 'red', variant: 'soft' },
                  groupProps: { grow: true },
                  onConfirm: () => discard.mutate(),
                })
              }
            >
              Discard draft
            </Button>
          )}
          {hasSigned ? (
            <Button variant="soft" color="gray" onClick={() => navigate(`/documents/${document.id}/compare`)}>
              Compare…
            </Button>
          ) : (
            <Tooltip label="No signed version to compare with yet">
              <Button variant="soft" color="gray" disabled>
                Compare…
              </Button>
            </Tooltip>
          )}
          <Button variant="soft" color="gray" onClick={onPermissions}>
            {roles?.canManage ? 'Share / Permissions' : 'Permissions'}
          </Button>
          <Button
            variant={commentsOpen ? 'soft' : 'subtle'}
            color={commentsOpen ? 'brand' : 'gray'}
            onClick={onToggleComments}
            aria-pressed={commentsOpen}
          >
            💬 Comments{documentComments > 0 ? ` (${documentComments})` : ''}
          </Button>
          <Button variant="soft" color="gray" onClick={() => setActivity(true)}>
            Activity
          </Button>
        </Group>
      </Group>
      {!isDraft && (
        <div className="dh-banner" role="status">
          {version.status === 'Signed' ? `${version.label} is signed and read-only.` : 'This version is read-only.'}
          {roles?.isOwner && !draft && hasSigned && ' Create a new draft to make changes.'}
        </div>
      )}

      <Modal
        opened={signing}
        onClose={() => setSigning(false)}
        title={`Sign ${versionLabel(version, document.versions)}`}
      >
        <Stack>
          <Text size="sm">Your signature confirms the current content of this draft.</Text>
          <Textarea
            label="Note (optional)"
            value={note}
            onChange={(e) => setNote(e.currentTarget.value)}
            maxLength={1000}
            autosize
            minRows={2}
          />
        </Stack>
        <Group className="dh-modal-footer" gap={12}>
          <Button variant="soft" color="gray" onClick={() => setSigning(false)}>
            Cancel
          </Button>
          <Button loading={sign.isPending} onClick={() => sign.mutate()}>
            Sign
          </Button>
        </Group>
      </Modal>

      <Drawer
        opened={activity}
        onClose={() => setActivity(false)}
        title="Document activity"
        position="right"
        size={460}
      >
        {activity && <ActivityFeed documentId={document.id} />}
      </Drawer>
    </div>
  );
}

/** The document-wide activity feed (secondary view; per-section history lives in the sections). */
function ActivityFeed({ documentId }: { documentId: number }) {
  const feed = useInfiniteQuery({
    queryKey: ['doc', documentId, 'activity'],
    queryFn: ({ pageParam }) =>
      unwrap(
        api.GET('/api/documents/{id}/history', {
          params: { path: { id: documentId }, query: { Page: pageParam, PageSize: 50 } },
        }),
      ),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page * last.pageSize < last.totalCount ? last.page + 1 : undefined),
  });
  const items = feed.data?.pages.flatMap((p) => p.items) ?? [];
  return (
    <Stack gap={10}>
      {feed.isSuccess && items.length === 0 && <Text className="dh-muted">No changes yet.</Text>}
      {items.map((entry) => (
        <div key={entry.id} className="dh-timeline-item">
          <Text size="sm" fw={500}>
            {new Date(entry.changedAt).toLocaleString()} · {sourceLabel(entry)}
            {entry.afterSigning && ' ⚠'}
          </Text>
          <Text size="sm" className="dh-muted">
            {entry.versionLabel ? `${entry.versionLabel} · ` : ''}
            {describeEntry(entry)}
          </Text>
        </div>
      ))}
      {feed.hasNextPage && (
        <Button variant="soft" loading={feed.isFetchingNextPage} onClick={() => void feed.fetchNextPage()}>
          Show more
        </Button>
      )}
    </Stack>
  );
}
