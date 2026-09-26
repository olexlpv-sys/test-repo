import { Table, Text } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { api, unwrap } from '../api/client';

/** Users (FR-H2): read-only — the user directory comes from seed data. */
export function UsersAdmin() {
  const users = useQuery({
    queryKey: ['users', 'admin-list'],
    queryFn: () => unwrap(api.GET('/api/users', { params: { query: { PageSize: 100, IncludeInactive: true } } })),
  });
  return (
    <section className="dh-card" aria-label="Users">
      <div className="dh-card-header">
        <span className="dh-card-title">Users</span>
        <Text size="sm" className="dh-muted">
          Users are managed by seed data.
        </Text>
      </div>
      <div className="dh-card-body" style={{ background: 'var(--dh-surface)' }}>
        <Table verticalSpacing={8} data-testid="users">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Login</Table.Th>
              <Table.Th>Display name</Table.Th>
              <Table.Th>Email</Table.Th>
              <Table.Th>Admin</Table.Th>
              <Table.Th>Active</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {(users.data?.items ?? []).map((u) => (
              <Table.Tr key={u.id} data-testid={`user-${u.login}`}>
                <Table.Td>{u.login}</Table.Td>
                <Table.Td>{u.displayName}</Table.Td>
                <Table.Td>{u.email ?? '—'}</Table.Td>
                <Table.Td>{u.isAdmin ? 'Yes' : 'No'}</Table.Td>
                <Table.Td>{u.isActive ? 'Yes' : 'No'}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        {users.data && users.data.totalCount > users.data.items.length && (
          <Text size="xs" className="dh-muted" mt={8}>
            Showing {users.data.items.length} of {users.data.totalCount}.
          </Text>
        )}
      </div>
    </section>
  );
}
