import { render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import { fakeApi } from '../test/fakeApi';
import { theme } from '../theme';
import type { VersionHeader } from './api';
import { SectionHistory } from './SectionHistory';

const logicalNodeId = '00000000-0000-0000-0000-000000000001';

const entry = (id: number, changedAt: string, summary: string) => ({
  id,
  changedAt,
  versionId: 10,
  versionLabel: 'v1',
  kind: 'ContentChanged',
  logicalNodeId,
  user: { id: 2, displayName: 'Alice Author' },
  source: 'App',
  dbLogin: null,
  ticket: null,
  reason: null,
  summary,
  changes: [],
  hasContentDiff: true,
  afterSigning: false,
});

const signed = {
  id: 10,
  documentId: 7,
  rowVersion: 'v',
  status: 'Signed',
  versionNumber: 1,
  label: 'v1',
  createdAt: '2026-09-02T09:00:00Z',
  createdBy: { id: 2, displayName: 'Alice Author' },
  // Half a second after the first edit: its text sorts after "…10:15:00Z" ('.' < 'Z') although it is later.
  signedAt: '2026-09-02T10:15:00.5Z',
  signedBy: [{ id: 4, displayName: 'Carol Clark' }],
  basedOnVersionId: null,
  isCurrent: true,
  modifiedAfterSigning: false,
} as VersionHeader;

describe('section history timeline (T15 §4)', () => {
  it('orders entries and version markers by time, not by timestamp text', async () => {
    fakeApi({
      'GET /api/documents/7/nodes/[^/]+/history': () => ({
        items: [
          entry(2, '2026-09-02T10:15:01Z', 'Edited after signing'),
          entry(1, '2026-09-02T10:15:00Z', 'Edited before signing'),
        ],
        page: 1,
        pageSize: 20,
        totalCount: 2,
      }),
    });
    render(
      <MantineProvider theme={theme} env="test">
        <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
          <SectionHistory
            documentId={7}
            logicalNodeId={logicalNodeId}
            versions={[signed]}
            activeEntryId={null}
            canRestore={false}
            onView={vi.fn()}
            onCompare={vi.fn()}
            onRestore={vi.fn()}
          />
        </QueryClientProvider>
      </MantineProvider>,
    );

    const after = await screen.findByText(/Edited after signing/);
    const marker = screen.getByText(/v1 signed/);
    const before = screen.getByText(/Edited before signing/);

    // Newest first: the later edit, then the signing, then the earlier edit.
    expect(after.compareDocumentPosition(marker) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(marker.compareDocumentPosition(before) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
});
