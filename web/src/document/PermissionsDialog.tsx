import { useMemo, useState } from 'react';
import { ActionIcon, Button, Group, Modal, SegmentedControl, Select, Stack, Table, Text, Tooltip } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { api, unwrap } from '../api/client';
import { flatten, type TreeNode } from './api';

interface Props {
  documentId: number;
  opened: boolean;
  onClose: () => void;
  /** Only the owner manages roles (FR-P4); everyone else sees the list read-only. */
  canManage: boolean;
  /** The tree the node scope is picked from. */
  tree: TreeNode[];
}

type Role = 'Editor' | 'Approver';

/** "Share / Permissions" (T16 §3): owner and grants; the owner adds and removes Editor/Approver grants. */
export function PermissionsDialog({ documentId, opened, onClose, canManage, tree }: Props) {
  const queryClient = useQueryClient();
  const [search, setSearch] = useState('');
  const [debounced] = useDebouncedValue(search, 250);
  const [userId, setUserId] = useState<string | null>(null);
  const [role, setRole] = useState<Role>('Editor');
  const [scope, setScope] = useState<string>('document');

  const permissions = useQuery({
    queryKey: ['doc', documentId, 'permissions'],
    queryFn: () => unwrap(api.GET('/api/documents/{id}/permissions', { params: { path: { id: documentId } } })),
    enabled: opened,
  });
  const owner = permissions.data?.owner;
  const users = useQuery({
    queryKey: ['users', 'search', debounced],
    queryFn: () =>
      unwrap(api.GET('/api/users', { params: { query: { Search: debounced || undefined, PageSize: 20 } } })),
    enabled: opened && canManage,
  });
  // The owner already has every right and can't be an approver (FR-V6): never offered.
  const candidates = (users.data?.items ?? []).filter((u) => u.isActive && u.id !== owner?.id);
  const nodes = useMemo(() => flatten(tree), [tree]);

  const refresh = async () => {
    // Grants change my-permissions and the editor's rights at once, plus signatures (approvers).
    await queryClient.invalidateQueries({ queryKey: ['doc', documentId] });
  };

  const grant = useMutation({
    mutationFn: () =>
      unwrap(
        api.POST('/api/documents/{id}/permissions', {
          params: { path: { id: documentId } },
          body: {
            userId: Number(userId),
            role,
            logicalNodeId: role === 'Editor' && scope !== 'document' ? scope : null,
          },
        }),
      ),
    onSuccess: async () => {
      setUserId(null);
      setSearch('');
      setScope('document');
      await refresh();
    },
  });
  const revoke = useMutation({
    mutationFn: (grantId: number) =>
      unwrap(
        api.DELETE('/api/documents/{id}/permissions/{grantId}', { params: { path: { id: documentId, grantId } } }),
      ),
    onSuccess: refresh,
  });

  return (
    <Modal opened={opened} onClose={onClose} title={canManage ? 'Share / Permissions' : 'Permissions'} size={720}>
      <Stack gap={16}>
        {!canManage && (
          <Text size="sm" className="dh-muted">
            Only the owner changes roles.
          </Text>
        )}
        <Table verticalSpacing={8} data-testid="grants">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>User</Table.Th>
              <Table.Th>Role</Table.Th>
              <Table.Th>Scope</Table.Th>
              <Table.Th>Granted</Table.Th>
              {canManage && <Table.Th />}
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {owner && (
              <Table.Tr>
                <Table.Td>{owner.displayName}</Table.Td>
                <Table.Td>Owner</Table.Td>
                <Table.Td>Whole document</Table.Td>
                <Table.Td />
                {canManage && <Table.Td />}
              </Table.Tr>
            )}
            {(permissions.data?.grants ?? []).map((g) => (
              <Table.Tr key={g.id} data-testid={`grant-${g.id}`}>
                <Table.Td>{g.user.displayName}</Table.Td>
                <Table.Td>{g.role}</Table.Td>
                <Table.Td>
                  {g.logicalNodeId === null ? (
                    'Whole document'
                  ) : (
                    <>
                      {nodes.find((n) => n.node.logicalNodeId === g.logicalNodeId)?.node.number ?? ''}{' '}
                      {g.nodeTitle ?? ''}
                      {g.nodeInCurrentVersion === false && (
                        <Tooltip label="This node is not in the current version">
                          <span className="dh-warn"> ⚠ not in current draft</span>
                        </Tooltip>
                      )}
                    </>
                  )}
                </Table.Td>
                <Table.Td>
                  <Text size="xs" className="dh-muted">
                    {g.grantedBy.displayName} · {new Date(g.grantedAt).toLocaleDateString()}
                  </Text>
                </Table.Td>
                {canManage && (
                  <Table.Td>
                    <ActionIcon
                      variant="soft"
                      color="red"
                      aria-label={`Remove ${g.role} ${g.user.displayName}`}
                      loading={revoke.isPending}
                      onClick={() => revoke.mutate(g.id)}
                    >
                      ✕
                    </ActionIcon>
                  </Table.Td>
                )}
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>

        {canManage && (
          <Stack gap={10} className="dh-card" p={14}>
            <Text fw={500} size="sm">
              Add a role
            </Text>
            <Group gap={10} align="flex-end" wrap="wrap">
              <Select
                label="User"
                aria-label="User"
                w={240}
                searchable
                placeholder="Type a name or login"
                data={candidates.map((u) => ({ value: String(u.id), label: `${u.displayName} (${u.login})` }))}
                value={userId}
                onChange={setUserId}
                searchValue={search}
                onSearchChange={setSearch}
                nothingFoundMessage="No users"
                comboboxProps={{ withinPortal: true }}
              />
              <SegmentedControl
                aria-label="Role"
                value={role}
                onChange={(v) => setRole(v as Role)}
                data={[
                  { value: 'Editor', label: 'Editor' },
                  { value: 'Approver', label: 'Approver' },
                ]}
              />
              {role === 'Editor' && (
                <Select
                  label="Scope"
                  aria-label="Scope"
                  w={240}
                  searchable
                  allowDeselect={false}
                  data={[
                    { value: 'document', label: 'Whole document' },
                    ...nodes.map((f) => ({
                      value: f.node.logicalNodeId,
                      label: `${'  '.repeat(f.depth)}${f.node.number} ${f.node.title}`,
                    })),
                  ]}
                  value={scope}
                  onChange={(v) => v && setScope(v)}
                  comboboxProps={{ withinPortal: true }}
                />
              )}
              <Button loading={grant.isPending} disabled={!userId} onClick={() => grant.mutate()}>
                Add
              </Button>
            </Group>
            <Text size="xs" className="dh-muted">
              Editors edit text only (of the whole document or of one node and its sub-nodes); approvers comment,
              resolve and sign.
            </Text>
          </Stack>
        )}
      </Stack>
    </Modal>
  );
}
