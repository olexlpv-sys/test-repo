import { Fragment, useState } from 'react';
import {
  Button,
  Code,
  Group,
  Pagination,
  Select,
  SimpleGrid,
  Table,
  Text,
  TextInput,
  UnstyledButton,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { api, unwrap } from '../api/client';

const PageSize = 50;
const tables = [
  'app.Folder',
  'app.Document',
  'app.DocumentVersion',
  'app.DocumentNode',
  'app.NodeContent',
  'app.DocumentPermission',
  'app.VersionSignature',
  'app.Comment',
  'app.NodeType',
  'app.ContentStyle',
  'app.User',
];

interface Filters {
  table: string | null;
  operation: string | null;
  source: string | null;
  userId: string;
  dbLogin: string;
  ticket: string;
  from: string;
  to: string;
}

const day = (offset: number) => {
  const d = new Date();
  d.setDate(d.getDate() + offset);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

/** The API needs a date range of at most 31 days: the last 7 days by default. */
const initial = (): Filters => ({
  table: null,
  operation: null,
  source: null,
  userId: '',
  dbLogin: '',
  ticket: '',
  from: day(-7),
  to: day(0),
});

const MaxRangeDays = 31;

function rangeError(from: string, to: string): string | null {
  if (!from || !to) {
    return 'Choose a date range (at most 31 days).';
  }

  const days = (new Date(`${to}T00:00:00`).getTime() - new Date(`${from}T00:00:00`).getTime()) / 86_400_000;
  return days < 0 ? '"From" is after "To".' : days > MaxRangeDays - 1 ? 'The date range can be at most 31 days.' : null;
}

function pretty(json: string | null | undefined): string {
  if (!json) {
    return '—';
  }

  try {
    return JSON.stringify(JSON.parse(json), null, 2);
  } catch {
    return json;
  }
}

/** Audit log (FR-UI3, FR-H1/H3): every recorded change with filters; old/new values; script changes highlighted. */
export function AuditAdmin() {
  const [filters, setFilters] = useState<Filters>(initial);
  const invalidRange = rangeError(filters.from, filters.to);
  const [page, setPage] = useState(1);
  const [open, setOpen] = useState<number | null>(null);
  const change = (next: Partial<Filters>) => {
    setFilters((f) => ({ ...f, ...next }));
    setPage(1);
  };

  const log = useQuery({
    queryKey: ['admin', 'audit', filters, page],
    queryFn: () =>
      unwrap(
        api.GET('/api/admin/audit', {
          params: {
            query: {
              table: filters.table ?? undefined,
              operation: filters.operation ?? undefined,
              source: filters.source ?? undefined,
              userId: filters.userId ? Number(filters.userId) : undefined,
              dbLogin: filters.dbLogin || undefined,
              ticket: filters.ticket || undefined,
              from: new Date(`${filters.from}T00:00:00`).toISOString(),
              to: new Date(`${filters.to}T23:59:59.999`).toISOString(),
              Page: page,
              PageSize,
            },
          },
        }),
      ),
    placeholderData: keepPreviousData,
    enabled: invalidRange === null,
  });
  const total = invalidRange ? 0 : (log.data?.totalCount ?? 0);

  return (
    <section className="dh-card" aria-label="Audit log">
      <div className="dh-card-header">
        <span className="dh-card-title">Audit log</span>
        <Text size="sm" className="dh-muted">
          {total} change{total === 1 ? '' : 's'}
        </Text>
      </div>
      <div className="dh-card-body" style={{ background: 'var(--dh-surface)' }}>
        <SimpleGrid cols={{ base: 2, md: 4 }} spacing={10} mb={12}>
          <Select
            label="Table"
            clearable
            data={tables}
            value={filters.table}
            onChange={(v) => change({ table: v })}
            comboboxProps={{ withinPortal: true }}
          />
          <Select
            label="Operation"
            clearable
            data={[
              { value: 'I', label: 'Insert' },
              { value: 'U', label: 'Update' },
              { value: 'D', label: 'Delete' },
            ]}
            value={filters.operation}
            onChange={(v) => change({ operation: v })}
            comboboxProps={{ withinPortal: true }}
          />
          <Select
            label="Source"
            clearable
            data={['App', 'Script']}
            value={filters.source}
            onChange={(v) => change({ source: v })}
            comboboxProps={{ withinPortal: true }}
          />
          <TextInput
            label="User id"
            value={filters.userId}
            onChange={(e) => change({ userId: e.currentTarget.value.replace(/\D/g, '') })}
          />
          <TextInput
            label="DB login"
            value={filters.dbLogin}
            onChange={(e) => change({ dbLogin: e.currentTarget.value })}
          />
          <TextInput
            label="Ticket"
            value={filters.ticket}
            onChange={(e) => change({ ticket: e.currentTarget.value })}
          />
          <TextInput
            label="From"
            type="date"
            value={filters.from}
            onChange={(e) => change({ from: e.currentTarget.value })}
            error={invalidRange ?? undefined}
          />
          <TextInput
            label="To"
            type="date"
            value={filters.to}
            onChange={(e) => change({ to: e.currentTarget.value })}
          />
        </SimpleGrid>
        <Table verticalSpacing={6} fz="sm" data-testid="audit">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>When</Table.Th>
              <Table.Th>Table</Table.Th>
              <Table.Th>Op</Table.Th>
              <Table.Th>Entity</Table.Th>
              <Table.Th>Source</Table.Th>
              <Table.Th>User / login</Table.Th>
              <Table.Th>Ticket</Table.Th>
              <Table.Th>Columns</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {(invalidRange ? [] : (log.data?.items ?? [])).map((row) => (
              <Fragment key={row.id}>
                <Table.Tr className="dh-audit-row" data-source={row.source} data-testid={`audit-${row.id}`}>
                  <Table.Td>
                    <UnstyledButton
                      aria-expanded={open === row.id}
                      onClick={() => setOpen(open === row.id ? null : (row.id ?? null))}
                      className="dh-link"
                    >
                      {row.changedAt ? new Date(row.changedAt).toLocaleString() : ''} {open === row.id ? '▲' : '▼'}
                    </UnstyledButton>
                  </Table.Td>
                  <Table.Td>{row.tableName}</Table.Td>
                  <Table.Td>{row.operation}</Table.Td>
                  <Table.Td>
                    {row.entityId}
                    {row.documentId ? (
                      <>
                        {' · '}
                        <Link
                          to={`/documents/${row.documentId}${row.documentVersionId ? `?version=${row.documentVersionId}` : ''}`}
                        >
                          doc {row.documentId}
                        </Link>
                      </>
                    ) : null}
                  </Table.Td>
                  <Table.Td>
                    {row.source === 'Script' ? (
                      <span className="dh-chip" data-tone="amber">
                        Script
                      </span>
                    ) : (
                      row.source
                    )}
                  </Table.Td>
                  <Table.Td>
                    {row.userId ? `#${row.userId} · ` : ''}
                    {row.dbLogin}
                  </Table.Td>
                  <Table.Td>{row.ticket ?? ''}</Table.Td>
                  <Table.Td>
                    <Text size="xs" className="dh-muted" lineClamp={1} maw={220}>
                      {row.changedColumns}
                    </Text>
                  </Table.Td>
                </Table.Tr>
                {open === row.id && (
                  <Table.Tr>
                    <Table.Td colSpan={8}>
                      {row.reason && (
                        <Text size="sm" mb={6}>
                          Reason: {row.reason}
                        </Text>
                      )}
                      <SimpleGrid cols={2} spacing={12}>
                        <div>
                          <Text size="xs" className="dh-muted">
                            Old values
                          </Text>
                          <Code block className="dh-json">
                            {pretty(row.oldValues)}
                          </Code>
                        </div>
                        <div>
                          <Text size="xs" className="dh-muted">
                            New values
                          </Text>
                          <Code block className="dh-json">
                            {pretty(row.newValues)}
                          </Code>
                        </div>
                      </SimpleGrid>
                    </Table.Td>
                  </Table.Tr>
                )}
              </Fragment>
            ))}
          </Table.Tbody>
        </Table>
        {!invalidRange && log.isSuccess && total === 0 && (
          <Text className="dh-muted">No changes match the filters.</Text>
        )}
        {total > PageSize && (
          <Group justify="center" mt={12}>
            <Pagination total={Math.ceil(total / PageSize)} value={page} onChange={setPage} size="sm" />
          </Group>
        )}
      </div>
    </section>
  );
}

/** Tamper findings (T21, FR-H6): reconciliation results with a "Run reconciliation now" button. */
export function FindingsAdmin() {
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const findings = useQuery({
    queryKey: ['admin', 'findings', page],
    queryFn: () => unwrap(api.GET('/api/admin/audit/findings', { params: { query: { Page: page, PageSize } } })),
    placeholderData: keepPreviousData,
  });
  const run = useMutation({
    mutationFn: () => unwrap(api.POST('/api/admin/audit/reconcile')),
    onSuccess: async (result) => {
      notifications.show({
        color: result.newFindings > 0 || result.moduleProblems > 0 ? 'orange' : 'green',
        title: 'Reconciliation finished',
        message: `${result.newFindings} new finding${result.newFindings === 1 ? '' : 's'}, ${result.moduleProblems} audit module problem${result.moduleProblems === 1 ? '' : 's'}.`,
      });
      await queryClient.invalidateQueries({ queryKey: ['admin', 'findings'] });
    },
  });
  const total = findings.data?.totalCount ?? 0;

  return (
    <section className="dh-card" aria-label="Tamper findings">
      <div className="dh-card-header">
        <span className="dh-card-title">Tamper findings</span>
        <Button loading={run.isPending} onClick={() => run.mutate()}>
          Run reconciliation now
        </Button>
      </div>
      <div className="dh-card-body" style={{ background: 'var(--dh-surface)' }}>
        <Table verticalSpacing={6} fz="sm" data-testid="findings">
          <Table.Thead>
            <Table.Tr>
              <Table.Th>Detected</Table.Th>
              <Table.Th>Kind</Table.Th>
              <Table.Th>Table / entity</Table.Th>
              <Table.Th>Document</Table.Th>
              <Table.Th>Transaction principal</Table.Th>
              <Table.Th>Detail</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {(findings.data?.items ?? []).map((f) => (
              <Table.Tr key={f.id} data-testid={`finding-${f.id}`}>
                <Table.Td>{new Date(f.detectedAtUtc).toLocaleString()}</Table.Td>
                <Table.Td>
                  <span className="dh-chip" data-tone="red">
                    {f.kind}
                  </span>
                  {f.afterSigning && ' ⚠ after signing'}
                </Table.Td>
                <Table.Td>
                  {f.tableName ?? '—'}
                  {f.entityId !== null ? ` #${f.entityId}` : ''}
                </Table.Td>
                <Table.Td>
                  {f.documentId ? (
                    <Link
                      to={`/documents/${f.documentId}${f.documentVersionId ? `?version=${f.documentVersionId}` : ''}`}
                    >
                      doc {f.documentId}
                    </Link>
                  ) : (
                    '—'
                  )}
                </Table.Td>
                <Table.Td>
                  {f.principal ?? '—'}
                  {f.transactionCommitTimeUtc && (
                    <Text size="xs" className="dh-muted">
                      committed {new Date(f.transactionCommitTimeUtc).toLocaleString()}
                    </Text>
                  )}
                </Table.Td>
                <Table.Td>
                  <Text size="xs" lineClamp={3} maw={360}>
                    {f.detail}
                  </Text>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        {findings.isSuccess && total === 0 && (
          <Text className="dh-muted">No findings — the ledger and the audit log agree.</Text>
        )}
        {total > PageSize && (
          <Group justify="center" mt={12}>
            <Pagination total={Math.ceil(total / PageSize)} value={page} onChange={setPage} size="sm" />
          </Group>
        )}
      </div>
    </section>
  );
}
