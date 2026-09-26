import { useEffect, useEffectEvent, useMemo, useState } from 'react';
import {
  ActionIcon,
  Button,
  Group,
  Menu,
  Modal,
  Stack,
  Text,
  TextInput,
  Tree,
  useTree,
  type TreeNodeData,
} from '@mantine/core';
import { modals } from '@mantine/modals';
import { notifications } from '@mantine/notifications';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useFolderTree } from '../api/queries';
import { api, ApiError, describeError, unwrap, type Schemas } from '../api/client';

type FolderNode = Schemas['FolderNode'];

function toTreeData(nodes: FolderNode[]): TreeNodeData[] {
  return nodes.map((n) => ({
    value: String(n.id),
    label: n.name,
    nodeProps: { count: n.documentCount },
    children: toTreeData(n.children),
  }));
}

/** Folder ids of the path to a folder (to expand the tree down to the selection). */
function pathTo(nodes: FolderNode[], id: number, path: number[] = []): number[] | null {
  for (const node of nodes) {
    if (node.id === id) {
      return [...path, node.id];
    }

    const found = pathTo(node.children, id, [...path, node.id]);
    if (found) {
      return found;
    }
  }

  return null;
}

/** A new folder (id = parent) or a rename, with the row version read when the dialog opened (a concurrent change → 409). */
interface Edit {
  mode: 'create' | 'rename';
  id: number | null;
  name: string;
  rowVersion?: string;
}

async function folderRowVersion(id: number): Promise<string> {
  return (await unwrap(api.GET('/api/folders/{id}', { params: { path: { id } } }))).rowVersion;
}

interface FolderTreeProps {
  selectedId: number | null;
  onSelect: (id: number) => void;
  /** Management actions (new, rename, move, delete) — admins only (T14; reused by the Admin tab). */
  manage: boolean;
}

/** The virtual-folder tree (FR-UI1, FR-F5): expand/collapse, select; admins also create, rename, move and delete folders. */
export function FolderTree({ selectedId, onSelect, manage }: FolderTreeProps) {
  const queryClient = useQueryClient();
  const folders = useFolderTree();
  const data = useMemo(() => toTreeData(folders.data ?? []), [folders.data]);
  const tree = useTree();
  const [editing, setEditing] = useState<Edit | null>(null);
  const [moving, setMoving] = useState<{ id: number; rowVersion: string } | null>(null);

  // Expand down to the selected folder (deep links: ?folder=12) — once per selection, in one state update (tree.expand
  // changes identity on every expansion, so it can't be an effect dependency).
  const expandTo = useEffectEvent((ids: number[]) => {
    if (ids.some((id) => !tree.expandedState[String(id)])) {
      tree.setExpandedState({ ...tree.expandedState, ...Object.fromEntries(ids.map((id) => [String(id), true])) });
    }
  });
  useEffect(() => {
    if (selectedId !== null && folders.data) {
      expandTo(pathTo(folders.data, selectedId)?.slice(0, -1) ?? []);
    }
  }, [selectedId, folders.data]);

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['folders'] });

  const save = useMutation({
    mutationFn: (edit: Edit) =>
      edit.mode === 'rename' && edit.id !== null && edit.rowVersion
        ? unwrap(
            api.PUT('/api/folders/{id}', {
              params: { path: { id: edit.id } },
              body: { name: edit.name, rowVersion: edit.rowVersion },
            }),
          )
        : unwrap(api.POST('/api/folders', { body: { parentFolderId: edit.id, name: edit.name } })),
    onSuccess: (folder) => {
      setEditing(null);
      void refresh();
      if (folder) {
        onSelect(folder.id);
      }
    },
  });

  const remove = useMutation({
    mutationFn: ({ id, rowVersion }: { id: number; rowVersion: string }) =>
      unwrap(api.DELETE('/api/folders/{id}', { params: { path: { id }, query: { rowVersion } } })),
    meta: { silent: true },
    onSuccess: () => void refresh(),
    onError: (error) =>
      notifications.show({
        color: 'red',
        title: 'Folder not deleted',
        message:
          error instanceof ApiError && error.type === 'in-use'
            ? 'The folder still has sub-folders or documents (deleted documents too — move them out first).'
            : describeError(error).message,
      }),
  });

  const move = useMutation({
    mutationFn: ({ id, rowVersion, parent }: { id: number; rowVersion: string; parent: number | null }) =>
      unwrap(
        api.POST('/api/folders/{id}/move', {
          params: { path: { id } },
          body: { newParentFolderId: parent, rowVersion },
        }),
      ),
    onSuccess: () => {
      setMoving(null);
      void refresh();
    },
  });

  // Rename, move and delete send the row version the admin saw when starting the action.
  const withRowVersion = (id: number, start: (rowVersion: string) => void) =>
    folderRowVersion(id).then(start, (error: unknown) => notifications.show({ color: 'red', ...describeError(error) }));

  const confirmDelete = (node: TreeNodeData, rowVersion: string) =>
    modals.openConfirmModal({
      title: `Delete folder "${String(node.label)}"?`,
      children: <Text size="sm">Only empty folders can be deleted.</Text>,
      labels: { confirm: 'Delete', cancel: 'Cancel' },
      confirmProps: { color: 'red', variant: 'soft' },
      groupProps: { grow: true },
      onConfirm: () => remove.mutate({ id: Number(node.value), rowVersion }),
    });

  return (
    <section className="dh-card">
      <div className="dh-card-header">
        <span className="dh-card-title">Folders</span>
        {manage && (
          <Button variant="soft" onClick={() => setEditing({ mode: 'create', id: null, name: '' })}>
            New folder
          </Button>
        )}
      </div>
      <div className="dh-card-body" style={{ padding: '12px 10px', background: 'var(--dh-surface)' }}>
        {folders.isSuccess && data.length === 0 && (
          <Text className="dh-muted" size="sm" px={8}>
            No folders yet.
          </Text>
        )}
        <Tree
          data={data}
          tree={tree}
          levelOffset="md"
          expandOnClick={false}
          renderNode={({ node, expanded, hasChildren, elementProps }) => (
            <Group
              {...elementProps}
              gap={4}
              wrap="nowrap"
              data-testid={`folder-${node.value}`}
              className={`${elementProps.className} dh-folder-row`}
              data-selected={Number(node.value) === selectedId || undefined}
            >
              <ActionIcon
                size="xs"
                variant="transparent"
                aria-label={expanded ? 'Collapse' : 'Expand'}
                style={{ visibility: hasChildren ? 'visible' : 'hidden' }}
                onClick={(e) => {
                  e.stopPropagation();
                  tree.toggleExpanded(node.value);
                }}
              >
                {expanded ? '▾' : '▸'}
              </ActionIcon>
              <Text
                fz={15}
                style={{ flex: 1, cursor: 'pointer' }}
                onClick={() => onSelect(Number(node.value))}
                truncate
              >
                {node.label}
              </Text>
              <span className="dh-chip" style={{ fontSize: 13 }}>
                {(node.nodeProps as { count: number }).count}
              </span>
              {manage && (
                <Menu position="bottom-end" withinPortal>
                  <Menu.Target>
                    <ActionIcon
                      size="xs"
                      variant="subtle"
                      aria-label={`Actions for ${String(node.label)}`}
                      onClick={(e) => e.stopPropagation()}
                    >
                      ⋯
                    </ActionIcon>
                  </Menu.Target>
                  <Menu.Dropdown>
                    <Menu.Item onClick={() => setEditing({ mode: 'create', id: Number(node.value), name: '' })}>
                      New sub-folder
                    </Menu.Item>
                    <Menu.Item
                      onClick={() =>
                        void withRowVersion(Number(node.value), (rowVersion) =>
                          setEditing({ mode: 'rename', id: Number(node.value), name: String(node.label), rowVersion }),
                        )
                      }
                    >
                      Rename
                    </Menu.Item>
                    <Menu.Item
                      onClick={() =>
                        void withRowVersion(Number(node.value), (rowVersion) =>
                          setMoving({ id: Number(node.value), rowVersion }),
                        )
                      }
                    >
                      Move to…
                    </Menu.Item>
                    <Menu.Item
                      color="red"
                      onClick={() =>
                        void withRowVersion(Number(node.value), (rowVersion) => confirmDelete(node, rowVersion))
                      }
                    >
                      Delete
                    </Menu.Item>
                  </Menu.Dropdown>
                </Menu>
              )}
            </Group>
          )}
        />
      </div>

      <Modal
        opened={editing !== null}
        onClose={() => setEditing(null)}
        title={editing?.mode === 'rename' ? 'Rename folder' : 'New folder'}
      >
        <form
          onSubmit={(e) => {
            e.preventDefault();
            if (editing) {
              save.mutate(editing);
            }
          }}
        >
          <Stack>
            <TextInput
              label="Name"
              data-autofocus
              value={editing?.name ?? ''}
              onChange={(e) => editing && setEditing({ ...editing, name: e.currentTarget.value })}
              error={
                save.error instanceof ApiError
                  ? (save.error.errors?.name?.[0] ??
                    (save.error.type === 'duplicate-name' ? 'A folder with this name already exists here.' : undefined))
                  : undefined
              }
            />
          </Stack>
          <div className="dh-modal-footer">
            <Button variant="soft" color="gray" onClick={() => setEditing(null)}>
              Cancel
            </Button>
            <Button type="submit" loading={save.isPending} disabled={!editing?.name.trim()}>
              Save
            </Button>
          </div>
        </form>
      </Modal>

      <FolderPickerModal
        opened={moving !== null}
        title="Move folder to…"
        allowRoot
        excludeId={moving?.id ?? null}
        onClose={() => setMoving(null)}
        onPick={(parent) => moving && move.mutate({ ...moving, parent })}
      />
    </section>
  );
}

interface FolderPickerModalProps {
  opened: boolean;
  title: string;
  onClose: () => void;
  onPick: (folderId: number | null) => void;
  /** Offer "top level" (folders only; documents always live in a folder). */
  allowRoot?: boolean;
  /** A folder that can't be picked, with its sub-folders (moving a folder into itself). */
  excludeId?: number | null;
}

/** Folder picker for "Move to…" (folders and documents). */
export function FolderPickerModal({
  opened,
  title,
  onClose,
  onPick,
  allowRoot = false,
  excludeId = null,
}: FolderPickerModalProps) {
  const folders = useFolderTree();
  const flat = useMemo(() => {
    const items: { id: number; label: string }[] = [];
    const walk = (nodes: FolderNode[], depth: number) => {
      for (const n of nodes) {
        if (n.id === excludeId) {
          continue;
        }

        items.push({ id: n.id, label: `${'  '.repeat(depth)}${n.name}` });
        walk(n.children, depth + 1);
      }
    };
    walk(folders.data ?? [], 0);
    return items;
  }, [folders.data, excludeId]);

  return (
    <Modal opened={opened} onClose={onClose} title={title}>
      <Stack gap={4}>
        {allowRoot && (
          <Button variant="subtle" justify="flex-start" onClick={() => onPick(null)}>
            (top level)
          </Button>
        )}
        {flat.map((f) => (
          <Button key={f.id} variant="subtle" justify="flex-start" onClick={() => onPick(f.id)}>
            {f.label}
          </Button>
        ))}
      </Stack>
    </Modal>
  );
}
