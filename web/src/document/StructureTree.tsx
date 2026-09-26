import { useMemo, useRef, useState, type DragEvent, type KeyboardEvent } from 'react';
import { ActionIcon, Button, Group, Menu, Modal, Select, Stack, Text, TextInput } from '@mantine/core';
import { modals } from '@mantine/modals';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../api/client';
import { descendantCount, flatten, isInSubtree, type FlatNode, type NodeChangeSummary, type TreeNode } from './api';

type NodeType = Schemas['NodeTypeResponse'];
type Drop = { targetId: number; place: 'before' | 'after' | 'inside' };

interface Props {
  documentId: number;
  versionId: number;
  tree: TreeNode[];
  nodeTypes: NodeType[];
  /** Owner on a draft: add, rename, change type, delete, move. */
  canEditStructure: boolean;
  /** Nodes whose text the user may edit (others are muted). */
  isEditable: (logicalNodeId: string) => boolean;
  selectedId: number | null;
  onSelect: (node: TreeNode) => void;
  summary: Map<string, NodeChangeSummary>;
  comments: Record<string, number>;
}

/** Where a new or moved node goes: parent id and 0-based position among the parent's other children. */
function placement(
  flat: FlatNode[],
  drop: Drop,
  movingId: number | null,
): { parentId: number | null; position: number } {
  const target = flat.find((f) => f.node.id === drop.targetId);
  if (!target) {
    return { parentId: null, position: 0 };
  }

  if (drop.place === 'inside') {
    return { parentId: target.node.id, position: target.node.children.filter((c) => c.id !== movingId).length };
  }

  const siblings = target.siblings.filter((s) => s.id !== movingId);
  const index = siblings.findIndex((s) => s.id === target.node.id);
  return { parentId: target.parent?.id ?? null, position: drop.place === 'before' ? index : index + 1 };
}

/** The document's structure (T15 §2): numbering, title, type; expand/collapse; keyboard navigation; owner structure editing. */
export function StructureTree({
  documentId,
  versionId,
  tree,
  nodeTypes,
  canEditStructure,
  isEditable,
  selectedId,
  onSelect,
  summary,
  comments,
}: Props) {
  const queryClient = useQueryClient();
  const [collapsed, setCollapsed] = useState<Set<number>>(new Set());
  const [adding, setAdding] = useState<{
    parentId: number | null;
    position: number | null;
    title: string;
    typeId: string | null;
  } | null>(null);
  const [renaming, setRenaming] = useState<{ id: number; title: string } | null>(null);
  const [dragging, setDragging] = useState<number | null>(null);
  const [drop, setDrop] = useState<Drop | null>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const flat = useMemo(() => flatten(tree), [tree]);
  const typeById = useMemo(() => new Map(nodeTypes.map((t) => [t.id, t])), [nodeTypes]);
  const activeTypes = nodeTypes.filter((t) => t.isActive);

  // Rows under a collapsed node are hidden.
  const visible = useMemo(() => {
    const rows: FlatNode[] = [];
    let hideBelow: number | null = null;
    for (const f of flat) {
      if (hideBelow !== null && f.depth > hideBelow) {
        continue;
      }

      hideBelow = collapsed.has(f.node.id) ? f.depth : null;
      rows.push(f);
    }

    return rows;
  }, [flat, collapsed]);

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['doc', documentId] });
  const toggle = (id: number) =>
    setCollapsed((current) => {
      const next = new Set(current);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }

      return next;
    });

  const create = useMutation({
    mutationFn: (input: { parentId: number | null; position: number | null; title: string; typeId: number }) =>
      unwrap(
        api.POST('/api/versions/{versionId}/nodes', {
          params: { path: { versionId } },
          body: {
            parentNodeId: input.parentId,
            position: input.position,
            title: input.title,
            nodeTypeId: input.typeId,
          },
        }),
      ),
    onSuccess: async (node) => {
      setAdding(null);
      await refresh();
      onSelect({ ...node, children: [] });
    },
  });

  const update = useMutation({
    mutationFn: (input: { node: TreeNode; title?: string; typeId?: number }) =>
      unwrap(
        api.PATCH('/api/nodes/{nodeId}', {
          params: { path: { nodeId: input.node.id } },
          body: { title: input.title ?? null, nodeTypeId: input.typeId ?? null, rowVersion: input.node.rowVersion },
        }),
      ),
    onSuccess: async () => {
      setRenaming(null);
      await refresh();
    },
  });

  const move = useMutation({
    mutationFn: (input: { node: TreeNode; parentId: number | null; position: number }) =>
      unwrap(
        api.POST('/api/nodes/{nodeId}/move', {
          params: { path: { nodeId: input.node.id } },
          body: { newParentNodeId: input.parentId, position: input.position, rowVersion: input.node.rowVersion },
        }),
      ),
    onSuccess: refresh,
  });

  const remove = useMutation({
    mutationFn: (node: TreeNode) =>
      unwrap(
        api.DELETE('/api/nodes/{nodeId}', {
          params: { path: { nodeId: node.id }, query: { rowVersion: node.rowVersion } },
        }),
      ),
    onSuccess: refresh,
  });

  const moveBy = (f: FlatNode, action: 'up' | 'down' | 'indent' | 'outdent') => {
    const { node, parent, index, siblings } = f;
    if (action === 'up' && index > 0) {
      move.mutate({ node, parentId: parent?.id ?? null, position: index - 1 });
    } else if (action === 'down' && index < siblings.length - 1) {
      move.mutate({ node, parentId: parent?.id ?? null, position: index + 1 });
    } else if (action === 'indent' && index > 0) {
      const newParent = siblings[index - 1];
      if (newParent) {
        move.mutate({ node, parentId: newParent.id, position: newParent.children.length });
      }
    } else if (action === 'outdent' && parent) {
      const grand = flat.find((x) => x.node.id === parent.id);
      if (grand) {
        move.mutate({ node, parentId: grand.parent?.id ?? null, position: grand.index + 1 });
      }
    }
  };

  const confirmDelete = (node: TreeNode) => {
    const below = descendantCount(node);
    modals.openConfirmModal({
      title: `Delete "${node.number} ${node.title}"?`,
      children: (
        <Text size="sm">
          {below > 0
            ? `Its ${below} sub-node${below === 1 ? '' : 's'} and all text are deleted too.`
            : 'Its text is deleted too.'}
        </Text>
      ),
      labels: { confirm: 'Delete', cancel: 'Cancel' },
      confirmProps: { color: 'red', variant: 'soft' },
      groupProps: { grow: true },
      onConfirm: () => remove.mutate(node),
    });
  };

  const onDragOver = (e: DragEvent, target: FlatNode) => {
    const moving = dragging === null ? null : flat.find((f) => f.node.id === dragging);
    if (!moving || isInSubtree(moving.node, target.node.id)) {
      setDrop(null);
      return; // no drop into the own subtree
    }

    e.preventDefault();
    const box = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const y = (e.clientY - box.top) / Math.max(1, box.height);
    setDrop({ targetId: target.node.id, place: y < 0.3 ? 'before' : y > 0.7 ? 'after' : 'inside' });
  };

  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    const moving = flat.find((f) => f.node.id === dragging);
    if (moving && drop) {
      const { parentId, position } = placement(flat, drop, moving.node.id);
      move.mutate({ node: moving.node, parentId, position });
    }

    setDragging(null);
    setDrop(null);
  };

  // Keyboard: ↑/↓ move between rows, ←/→ collapse/expand, Enter opens the section.
  const onKeyDown = (e: KeyboardEvent) => {
    const index = visible.findIndex((f) => f.node.id === selectedId);
    const current = visible[index];
    const focusRow = (row: FlatNode | undefined) => {
      if (row) {
        onSelect(row.node);
        listRef.current?.querySelector<HTMLElement>(`[data-node-id="${row.node.id}"]`)?.focus();
      }
    };
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      focusRow(visible[Math.min(visible.length - 1, index + 1)]);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      focusRow(visible[Math.max(0, index - 1)]);
    } else if (e.key === 'ArrowLeft' && current) {
      if (current.node.children.length > 0 && !collapsed.has(current.node.id)) {
        toggle(current.node.id);
      } else {
        focusRow(visible.find((f) => f.node.id === current.parent?.id));
      }
    } else if (e.key === 'ArrowRight' && current && collapsed.has(current.node.id)) {
      toggle(current.node.id);
    } else if (e.key === 'F2' && current && canEditStructure) {
      setRenaming({ id: current.node.id, title: current.node.title });
    }
  };

  const firstType = activeTypes[0];

  return (
    <section className="dh-card dh-structure" aria-label="Structure">
      <div className="dh-card-header">
        <span className="dh-card-title">Structure</span>
        {canEditStructure && (
          <Button
            variant="soft"
            onClick={() =>
              setAdding({ parentId: null, position: null, title: '', typeId: firstType ? String(firstType.id) : null })
            }
          >
            + Node
          </Button>
        )}
      </div>
      <div
        className="dh-card-body dh-tree-body"
        ref={listRef}
        role="tree"
        aria-label="Document structure"
        onKeyDown={onKeyDown}
      >
        {flat.length === 0 && (
          <Text size="sm" className="dh-muted" px={8}>
            {canEditStructure ? 'No sections yet — add the first node.' : 'This version has no sections.'}
          </Text>
        )}
        {visible.map((f) => {
          const { node, depth } = f;
          const changes = summary.get(node.logicalNodeId);
          const type = typeById.get(node.nodeTypeId);
          const dropHere = drop?.targetId === node.id ? drop.place : undefined;
          return (
            <div
              key={node.id}
              role="treeitem"
              aria-selected={node.id === selectedId}
              aria-expanded={node.children.length > 0 ? !collapsed.has(node.id) : undefined}
              aria-level={depth + 1}
              tabIndex={node.id === selectedId || (selectedId === null && f === visible[0]) ? 0 : -1}
              data-node-id={node.id}
              data-testid={`tree-node-${node.id}`}
              className="dh-tree-row"
              data-selected={node.id === selectedId || undefined}
              data-muted={!isEditable(node.logicalNodeId) || undefined}
              data-drop={dropHere}
              style={{ paddingLeft: 6 + depth * 16 }}
              draggable={canEditStructure && renaming?.id !== node.id}
              onDragStart={(e) => {
                e.dataTransfer.effectAllowed = 'move';
                e.dataTransfer.setData('text/plain', String(node.id));
                setDragging(node.id);
              }}
              onDragEnd={() => {
                setDragging(null);
                setDrop(null);
              }}
              onDragOver={(e) => onDragOver(e, f)}
              onDragLeave={() => setDrop((d) => (d?.targetId === node.id ? null : d))}
              onDrop={onDrop}
              onClick={() => onSelect(node)}
              onDoubleClick={() => canEditStructure && setRenaming({ id: node.id, title: node.title })}
            >
              <ActionIcon
                size="xs"
                variant="transparent"
                color="gray"
                aria-label={collapsed.has(node.id) ? 'Expand' : 'Collapse'}
                style={{ visibility: node.children.length > 0 ? 'visible' : 'hidden' }}
                onClick={(e) => {
                  e.stopPropagation();
                  toggle(node.id);
                }}
                tabIndex={-1}
              >
                {collapsed.has(node.id) ? '▸' : '▾'}
              </ActionIcon>
              <span className="dh-tree-number">{node.number}</span>
              {renaming?.id === node.id ? (
                <form
                  style={{ flex: 1 }}
                  onSubmit={(e) => {
                    e.preventDefault();
                    if (renaming.title.trim() && renaming.title !== node.title) {
                      update.mutate({ node, title: renaming.title.trim() });
                    } else {
                      setRenaming(null);
                    }
                  }}
                >
                  <TextInput
                    size="xs"
                    aria-label="Node title"
                    autoFocus
                    value={renaming.title}
                    onChange={(e) => setRenaming({ id: node.id, title: e.currentTarget.value })}
                    onBlur={() => setRenaming(null)}
                    onKeyDown={(e) => e.key === 'Escape' && setRenaming(null)}
                    onClick={(e) => e.stopPropagation()}
                  />
                </form>
              ) : (
                <span className="dh-tree-title">{node.title}</span>
              )}
              {type && <span className="dh-chip dh-tree-type">{type.name}</span>}
              {changes && changes.changeCount > 0 && (
                <span className="dh-tree-changes" title={`${changes.changeCount} change(s) since the baseline`}>
                  ●{changes.changeCount}
                </span>
              )}
              {(comments[node.logicalNodeId] ?? 0) > 0 && (
                <span className="dh-tree-comments">💬{comments[node.logicalNodeId]}</span>
              )}
              {canEditStructure && (
                <Menu position="bottom-end" withinPortal>
                  <Menu.Target>
                    <ActionIcon
                      size="sm"
                      variant="subtle"
                      color="gray"
                      aria-label={`Actions for ${node.title}`}
                      onClick={(e) => e.stopPropagation()}
                      tabIndex={-1}
                    >
                      ⋯
                    </ActionIcon>
                  </Menu.Target>
                  <Menu.Dropdown>
                    <Menu.Item
                      onClick={() =>
                        setAdding({
                          parentId: node.id,
                          position: null,
                          title: '',
                          typeId: firstType ? String(firstType.id) : null,
                        })
                      }
                    >
                      Add child
                    </Menu.Item>
                    <Menu.Item
                      onClick={() =>
                        setAdding({
                          parentId: f.parent?.id ?? null,
                          position: f.index + 1,
                          title: '',
                          typeId: String(node.nodeTypeId),
                        })
                      }
                    >
                      Add sibling below
                    </Menu.Item>
                    <Menu.Item onClick={() => setRenaming({ id: node.id, title: node.title })}>Rename</Menu.Item>
                    <Menu.Sub>
                      <Menu.Sub.Target>
                        <Menu.Sub.Item>Change type</Menu.Sub.Item>
                      </Menu.Sub.Target>
                      <Menu.Sub.Dropdown>
                        {activeTypes.map((t) => (
                          <Menu.Item
                            key={t.id}
                            disabled={t.id === node.nodeTypeId}
                            onClick={() => update.mutate({ node, typeId: t.id })}
                          >
                            {t.name}
                          </Menu.Item>
                        ))}
                      </Menu.Sub.Dropdown>
                    </Menu.Sub>
                    <Menu.Divider />
                    <Menu.Item disabled={f.index === 0} onClick={() => moveBy(f, 'up')}>
                      Move up
                    </Menu.Item>
                    <Menu.Item disabled={f.index === f.siblings.length - 1} onClick={() => moveBy(f, 'down')}>
                      Move down
                    </Menu.Item>
                    <Menu.Item disabled={f.index === 0} onClick={() => moveBy(f, 'indent')}>
                      Indent
                    </Menu.Item>
                    <Menu.Item disabled={!f.parent} onClick={() => moveBy(f, 'outdent')}>
                      Outdent
                    </Menu.Item>
                    <Menu.Divider />
                    <Menu.Item color="red" onClick={() => confirmDelete(node)}>
                      Delete
                    </Menu.Item>
                  </Menu.Dropdown>
                </Menu>
              )}
            </div>
          );
        })}
      </div>

      <Modal opened={adding !== null} onClose={() => setAdding(null)} title="New node">
        <form
          onSubmit={(e) => {
            e.preventDefault();
            if (adding?.typeId && adding.title.trim()) {
              create.mutate({
                parentId: adding.parentId,
                position: adding.position,
                title: adding.title.trim(),
                typeId: Number(adding.typeId),
              });
            }
          }}
        >
          <Stack>
            <Select
              label="Type"
              data={activeTypes.map((t) => ({ value: String(t.id), label: t.name }))}
              value={adding?.typeId ?? null}
              onChange={(value) => adding && setAdding({ ...adding, typeId: value })}
              allowDeselect={false}
              comboboxProps={{ withinPortal: true }}
            />
            <TextInput
              label="Title"
              data-autofocus
              value={adding?.title ?? ''}
              onChange={(e) => adding && setAdding({ ...adding, title: e.currentTarget.value })}
            />
          </Stack>
          <Group className="dh-modal-footer" gap={12}>
            <Button variant="soft" color="gray" onClick={() => setAdding(null)}>
              Cancel
            </Button>
            <Button type="submit" loading={create.isPending} disabled={!adding?.title.trim() || !adding.typeId}>
              Add
            </Button>
          </Group>
        </form>
      </Modal>
    </section>
  );
}
