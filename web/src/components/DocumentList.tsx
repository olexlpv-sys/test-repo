import { useState } from 'react';
import { ActionIcon, Button, Group, Menu, Modal, Stack, Switch, Text, TextInput } from '@mantine/core';
import { modals } from '@mantine/modals';
import { notifications } from '@mantine/notifications';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { api, ApiError, unwrap, type Schemas } from '../api/client';
import { useSession } from '../app/sessionContext';
import { FolderPickerModal } from './FolderTree';

type Item = Schemas['DocumentListItem'];
type SortBy = 'title' | 'status' | 'latestSignedVersion' | 'owner' | 'modifiedAt';

const statusTone: Record<string, string | undefined> = { Draft: 'amber', Signed: 'green' };

const columns: { key: SortBy; label: string }[] = [
  { key: 'title', label: 'Title' },
  { key: 'status', label: 'Status' },
  { key: 'latestSignedVersion', label: 'Version' },
  { key: 'owner', label: 'Owner' },
  { key: 'modifiedAt', label: 'Modified' },
];

/** The documents of the selected folder (FR-UI1): sort, show deleted, add, delete, restore, move. */
export function DocumentList({ folderId, folderName }: { folderId: number; folderName: string }) {
  const { me } = useSession();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const [sort, setSort] = useState<{ by: SortBy; dir: 'asc' | 'desc' }>({ by: 'title', dir: 'asc' });
  const [showDeleted, setShowDeleted] = useState(false);
  const [selected, setSelected] = useState<number | null>(null);
  const [adding, setAdding] = useState(false);
  const [title, setTitle] = useState('');
  const [moving, setMoving] = useState<Item | null>(null);
  const [restoring, setRestoring] = useState<Item | null>(null);

  const list = useQuery({
    queryKey: ['documents', folderId, sort, showDeleted],
    queryFn: () =>
      unwrap(
        api.GET('/api/folders/{folderId}/documents', {
          params: {
            path: { folderId },
            query: { SortBy: sort.by, SortDir: sort.dir, IncludeDeleted: showDeleted, PageSize: 200 },
          },
        }),
      ),
    placeholderData: keepPreviousData,
  });
  const items = list.data?.items ?? [];
  const selectedItem = items.find((i) => i.id === selected) ?? null;
  const isOwner = (item: Item | null) => item?.myRoles.includes('Owner') ?? false;

  const refresh = () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: ['documents'] }),
      queryClient.invalidateQueries({ queryKey: ['folders'] }),
    ]);

  const add = useMutation({
    mutationFn: (value: string) => unwrap(api.POST('/api/documents', { body: { folderId, title: value } })),
    onSuccess: async (created) => {
      setAdding(false);
      setTitle('');
      await refresh();
      navigate(`/documents/${created.id}`);
    },
  });

  const remove = useMutation({
    mutationFn: (item: Item) =>
      unwrap(
        api.DELETE('/api/documents/{id}', {
          params: { path: { id: item.id }, query: { rowVersion: item.rowVersion } },
        }),
      ),
    onSuccess: async () => {
      setSelected(null);
      await refresh();
    },
  });

  const restore = useMutation({
    mutationFn: ({ item, target }: { item: Item; target?: number }) =>
      unwrap(
        api.POST('/api/documents/{id}/restore', {
          params: { path: { id: item.id } },
          body: { folderId: target ?? null },
        }),
      ),
    meta: { silent: true },
    onSuccess: async () => {
      setRestoring(null);
      await refresh();
    },
    onError: (error, { item }) => {
      if (error instanceof ApiError && error.type === 'folder-missing') {
        setRestoring(item); // the original folder is gone: ask for a target
      } else {
        notifications.show({ color: 'red', title: 'Not restored', message: error.message });
      }
    },
  });

  const move = useMutation({
    mutationFn: ({ item, target }: { item: Item; target: number }) =>
      unwrap(
        api.POST('/api/documents/{id}/move', {
          params: { path: { id: item.id } },
          body: { folderId: target, rowVersion: item.rowVersion },
        }),
      ),
    onSuccess: async () => {
      setMoving(null);
      await refresh();
    },
  });

  const toggleSort = (by: SortBy) => setSort((s) => ({ by, dir: s.by === by && s.dir === 'asc' ? 'desc' : 'asc' }));
  const canMove = (item: Item) => me?.isAdmin || (isOwner(item) && item.status !== 'Deleted');
  const canRestore = (item: Item) => item.status === 'Deleted' && (me?.isAdmin || isOwner(item));

  return (
    <Stack gap={20}>
      <Group justify="space-between" wrap="wrap" gap="sm">
        <span className="dh-section-title">{folderName}</span>
        <Group gap="sm">
          <Switch
            label="Show deleted"
            checked={showDeleted}
            onChange={(e) => setShowDeleted(e.currentTarget.checked)}
          />
          <Button
            variant="soft"
            color="red"
            disabled={!selectedItem || !isOwner(selectedItem) || selectedItem.status === 'Deleted'}
            onClick={() =>
              selectedItem &&
              modals.openConfirmModal({
                title: `Delete "${selectedItem.title}"?`,
                children: <Text size="sm">The owner and admins can restore it later.</Text>,
                labels: { confirm: 'Delete', cancel: 'Cancel' },
                confirmProps: { color: 'red', variant: 'soft' },
                groupProps: { grow: true },
                onConfirm: () => remove.mutate(selectedItem),
              })
            }
          >
            Delete
          </Button>
          <Button onClick={() => setAdding(true)}>Create</Button>
        </Group>
      </Group>

      <Group gap={0} aria-label="Sort by">
        <Text span size="sm" className="dh-muted" mr={4}>
          Sort:
        </Text>
        {columns.map((c) => (
          <button
            key={c.key}
            type="button"
            className="dh-sort"
            data-active={sort.by === c.key}
            onClick={() => toggleSort(c.key)}
          >
            {c.label} {sort.by === c.key ? (sort.dir === 'asc' ? '▲' : '▼') : ''}
          </button>
        ))}
      </Group>

      {list.isSuccess && items.length === 0 ? (
        <Text className="dh-muted">This folder has no documents{showDeleted ? '' : ' (deleted ones are hidden)'}.</Text>
      ) : (
        <div>
          {items.map((item) => (
            <article
              key={item.id}
              data-testid={`document-${item.id}`}
              className={item.status === 'Deleted' ? 'dh-card dh-deleted' : 'dh-card'}
              data-selected={item.id === selected}
              aria-selected={item.id === selected}
              style={{ cursor: 'pointer' }}
              onClick={() => setSelected(item.id)}
              onDoubleClick={() => item.status !== 'Deleted' && navigate(`/documents/${item.id}`)}
            >
              <div className="dh-card-header">
                <Group gap={12} wrap="nowrap" style={{ minWidth: 0 }}>
                  <Text className="dh-card-title" truncate>
                    {item.title}
                  </Text>
                  <span className="dh-chip" data-tone={statusTone[item.status]}>
                    {item.status}
                  </span>
                  {item.latestSignedVersion && <span className="dh-chip">v{item.latestSignedVersion}</span>}
                </Group>
                <Group
                  gap={8}
                  wrap="nowrap"
                  onClick={(e) => e.stopPropagation()}
                  onDoubleClick={(e) => e.stopPropagation()}
                >
                  {canRestore(item) && (
                    <Button variant="soft" onClick={() => restore.mutate({ item })}>
                      Restore
                    </Button>
                  )}
                  {item.status !== 'Deleted' && (
                    <Button variant="soft" onClick={() => navigate(`/documents/${item.id}`)}>
                      Open
                    </Button>
                  )}
                  {canMove(item) && (
                    <Menu position="bottom-end" withinPortal>
                      <Menu.Target>
                        <ActionIcon size={36} variant="soft" color="gray" aria-label={`Actions for ${item.title}`}>
                          ⋯
                        </ActionIcon>
                      </Menu.Target>
                      <Menu.Dropdown>
                        <Menu.Item onClick={() => setMoving(item)}>Move to…</Menu.Item>
                      </Menu.Dropdown>
                    </Menu>
                  )}
                </Group>
              </div>
              <div className="dh-card-body">
                <Group justify="flex-end" gap={40} wrap="wrap">
                  <Field label="Owner" value={item.owner.displayName} />
                  <Field label="Modified" value={new Date(item.modifiedAt).toLocaleString()} />
                  <Field
                    label="Signatures"
                    value={
                      item.signatureProgress
                        ? `${item.signatureProgress.signed}/${item.signatureProgress.required}`
                        : '—'
                    }
                  />
                </Group>
              </div>
            </article>
          ))}
        </div>
      )}

      <Modal opened={adding} onClose={() => setAdding(false)} title="New document">
        <form
          onSubmit={(e) => {
            e.preventDefault();
            add.mutate(title);
          }}
        >
          <Stack>
            <TextInput
              label="Title"
              data-autofocus
              value={title}
              onChange={(e) => setTitle(e.currentTarget.value)}
              error={add.error instanceof ApiError ? add.error.errors?.title?.[0] : undefined}
            />
          </Stack>
          <div className="dh-modal-footer">
            <Button variant="soft" color="gray" onClick={() => setAdding(false)}>
              Cancel
            </Button>
            <Button type="submit" loading={add.isPending} disabled={!title.trim()}>
              Create
            </Button>
          </div>
        </form>
      </Modal>

      <FolderPickerModal
        opened={moving !== null}
        title={`Move "${moving?.title ?? ''}" to…`}
        onClose={() => setMoving(null)}
        onPick={(target) => moving && target !== null && move.mutate({ item: moving, target })}
      />
      <FolderPickerModal
        opened={restoring !== null}
        title="The original folder is gone — restore into…"
        onClose={() => setRestoring(null)}
        onPick={(target) => restoring && target !== null && restore.mutate({ item: restoring, target })}
      />
    </Stack>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div className="dh-field-label">{label}</div>
      <div className="dh-field-value">{value}</div>
    </div>
  );
}
