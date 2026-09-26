import { Button, Group, Stack, Text, Tooltip } from '@mantine/core';
import { useInfiniteQuery } from '@tanstack/react-query';
import { api, unwrap } from '../api/client';
import { keys, type HistoryEntry, type VersionHeader } from './api';
import { kindLabel, sourceLabel } from './historyText';

type Row = { kind: 'entry'; at: string; entry: HistoryEntry } | { kind: 'version'; at: string; version: VersionHeader };

interface Props {
  documentId: number;
  logicalNodeId: string;
  versions: VersionHeader[];
  activeEntryId: number | null;
  canRestore: boolean;
  onView: (entry: HistoryEntry) => void;
  onCompare: (entry: HistoryEntry) => void;
  onRestore: (entry: HistoryEntry) => void;
}

const withContent = new Set([
  'ContentChanged',
  'NodeCreated',
  'CopiedToNewDraft',
  'NodeRenamed',
  'NodeTypeChanged',
  'NodeMoved',
]);

/** The node's timeline inside its section (T15 §4): every change across versions, newest first, with version markers. */
export function SectionHistory({
  documentId,
  logicalNodeId,
  versions,
  activeEntryId,
  canRestore,
  onView,
  onCompare,
  onRestore,
}: Props) {
  const history = useInfiniteQuery({
    queryKey: keys.nodeHistory(documentId, logicalNodeId),
    queryFn: ({ pageParam }) =>
      unwrap(
        api.GET('/api/documents/{id}/nodes/{logicalNodeId}/history', {
          params: { path: { id: documentId, logicalNodeId }, query: { Page: pageParam, PageSize: 20 } },
        }),
      ),
    initialPageParam: 1,
    getNextPageParam: (last) => (last.page * last.pageSize < last.totalCount ? last.page + 1 : undefined),
  });
  const entries = history.data?.pages.flatMap((p) => p.items) ?? [];
  const oldest = entries.at(-1)?.changedAt;
  const complete = !history.hasNextPage;

  // Version markers ("v2 signed · carol, dave") between the entries of the loaded range, newest first — compared as times, not
  // text (UTC timestamps have fractions of varying length before the Z).
  const rows: Row[] = [
    ...entries.map((entry): Row => ({ kind: 'entry', at: entry.changedAt, entry })),
    ...versions
      .filter(
        (v): v is VersionHeader & { signedAt: string } =>
          v.status === 'Signed' &&
          v.signedAt !== null &&
          (complete || (oldest !== undefined && Date.parse(v.signedAt) >= Date.parse(oldest))),
      )
      .map((version): Row => ({ kind: 'version', at: version.signedAt, version })),
  ].sort((a, b) => Date.parse(b.at) - Date.parse(a.at));

  return (
    <div className="dh-section-history" data-testid={`history-${logicalNodeId}`}>
      <Stack gap={4}>
        {history.isPending && (
          <Text size="sm" className="dh-muted">
            Loading history…
          </Text>
        )}
        {history.isSuccess && entries.length === 0 && (
          <Text size="sm" className="dh-muted">
            No changes recorded for this section.
          </Text>
        )}
        {rows.map((row) =>
          row.kind === 'version' ? (
            <div key={`v${row.version.id}`} className="dh-timeline-version">
              ● {row.version.label} signed {new Date(row.version.signedAt ?? row.at).toLocaleDateString()}
              {row.version.signedBy.length > 0 && ` (${row.version.signedBy.map((u) => u.displayName).join(', ')})`}
            </div>
          ) : (
            <Group
              key={row.entry.id}
              className="dh-timeline-item"
              data-active={row.entry.id === activeEntryId || undefined}
              justify="space-between"
              wrap="nowrap"
              gap={8}
            >
              <div style={{ minWidth: 0 }}>
                <Text size="sm">
                  <span className="dh-timeline-dot">●</span> {new Date(row.entry.changedAt).toLocaleString()}{' '}
                  <b>{sourceLabel(row.entry)}</b> <span className="dh-muted">{kindLabel(row.entry.kind)}</span>
                  {(row.entry.source === 'Script' || row.entry.afterSigning) && (
                    <Tooltip label={row.entry.afterSigning ? 'Changed after signing' : 'Support script change'}>
                      <span
                        className="dh-warn"
                        aria-label={row.entry.afterSigning ? 'Changed after signing' : 'Script change'}
                      >
                        {' '}
                        ⚠{row.entry.afterSigning ? ' after signing' : ''}
                      </span>
                    </Tooltip>
                  )}
                </Text>
                <Text size="xs" className="dh-muted" truncate>
                  {row.entry.versionLabel ? `${row.entry.versionLabel} · ` : ''}
                  {row.entry.summary}
                  {row.entry.reason ? ` · ${row.entry.reason}` : ''}
                </Text>
              </div>
              {withContent.has(row.entry.kind) && (
                <Group gap={4} wrap="nowrap">
                  <Button size="compact-xs" variant="soft" color="gray" onClick={() => onView(row.entry)}>
                    View
                  </Button>
                  <Button size="compact-xs" variant="soft" onClick={() => onCompare(row.entry)}>
                    Compare
                  </Button>
                  {canRestore && (
                    <Button size="compact-xs" variant="soft" color="gray" onClick={() => onRestore(row.entry)}>
                      Restore this text
                    </Button>
                  )}
                </Group>
              )}
            </Group>
          ),
        )}
        {history.hasNextPage && (
          <Button
            size="compact-sm"
            variant="subtle"
            loading={history.isFetchingNextPage}
            onClick={() => void history.fetchNextPage()}
          >
            Older changes
          </Button>
        )}
      </Stack>
    </div>
  );
}
