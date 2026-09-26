import { useState } from 'react';
import { Loader, UnstyledButton } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { api, unwrap } from '../api/client';
import { keys, type ChangeSummary } from './api';

/** A section deleted since the baseline: a collapsed, struck-through placeholder at its former position; expands to its last content. */
export function RemovedSection({ removed, depth }: { removed: ChangeSummary['removed'][number]; depth: number }) {
  const [open, setOpen] = useState(false);
  const last = useQuery({
    queryKey: keys.entryContent(removed.lastEntryId ?? 0),
    queryFn: () =>
      unwrap(
        api.GET('/api/history/entries/{entryId}/content', { params: { path: { entryId: removed.lastEntryId ?? 0 } } }),
      ),
    enabled: open && removed.lastEntryId !== null,
  });
  return (
    <section
      className="dh-page-section dh-removed-section"
      data-testid={`removed-${removed.logicalNodeId}`}
      aria-label={`Deleted: ${removed.number} ${removed.title}`}
    >
      <UnstyledButton
        className={`dh-page-section-title ds-style-Heading${Math.min(depth + 1, 6)}`}
        aria-expanded={open}
        onClick={() => setOpen((o) => !o)}
      >
        <del>
          <span className="dh-page-section-number">{removed.number}</span> {removed.title}
        </del>{' '}
        <span className="dh-structural">deleted since the baseline {open ? '▲' : '▼'}</span>
      </UnstyledButton>
      {open &&
        (removed.lastEntryId === null ? (
          <p className="dh-muted">No earlier content recorded.</p>
        ) : last.isSuccess ? (
          // Server-rendered HTML (text HTML-encoded by the renderer).
          <div className="dh-section-text dh-historical" dangerouslySetInnerHTML={{ __html: last.data.contentHtml }} />
        ) : (
          <Loader size="sm" />
        ))}
    </section>
  );
}
