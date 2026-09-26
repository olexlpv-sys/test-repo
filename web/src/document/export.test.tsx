import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MantineProvider } from '@mantine/core';
import { Notifications, notifications } from '@mantine/notifications';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeApi, Reply } from '../test/fakeApi';
import { theme } from '../theme';
import type { TreeNode, VersionHeader } from './api';
import { ExportPdfDialog } from './ExportPdf';
import { PollIntervalMs } from './exportJobs';

const version = (status: 'Draft' | 'Signed') =>
  ({
    id: 11,
    documentId: 7,
    rowVersion: 'v',
    status,
    versionNumber: status === 'Signed' ? 1 : null,
    label: status === 'Signed' ? 'v1' : 'Draft',
    createdAt: '2026-09-01T00:00:00Z',
    createdBy: { id: 2, displayName: 'Alice' },
    signedAt: null,
    signedBy: [],
    basedOnVersionId: null,
    isCurrent: true,
    modifiedAfterSigning: false,
  }) as VersionHeader;

const section = {
  id: 5,
  logicalNodeId: '00000000-0000-0000-0000-000000000005',
  nodeTypeId: 1,
  title: 'Scope',
  number: '1.2',
  hasContent: true,
  sortOrder: 1024,
  rowVersion: 'r',
  children: [],
} as TreeNode;

const job = (status: string, extra: Record<string, unknown> = {}) => ({
  jobId: 42,
  status,
  progress: 0,
  error: null,
  fileName: 'Manual - Draft.pdf',
  fileSize: null,
  ...extra,
});

function renderDialog(status: 'Draft' | 'Signed', sectionFirst: boolean, onClose = vi.fn()) {
  render(
    <MantineProvider theme={theme} env="test">
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <Notifications />
        <ExportPdfDialog
          opened
          onClose={onClose}
          version={version(status)}
          section={section}
          sectionFirst={sectionFirst}
        />
      </QueryClientProvider>
    </MantineProvider>,
  );
  return onClose;
}

describe('PDF export (T20 UI)', () => {
  const click = vi.fn();
  const createObjectURL = vi.fn((blob: Blob) => `blob:${blob.size}`);

  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    URL.createObjectURL = createObjectURL;
    URL.revokeObjectURL = vi.fn();
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
      click(this.download, this.href);
    });
  });

  afterEach(() => {
    act(() => notifications.clean());
    vi.useRealTimers();
    vi.restoreAllMocks();
    click.mockReset();
    createObjectURL.mockClear();
  });

  it('exports the chosen section with the options, shows progress and downloads the file', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const statuses = [job('Running', { progress: 40 }), job('Succeeded', { progress: 100, fileSize: 4 })];
    const { calls } = fakeApi({
      'POST /api/versions/11/exports/pdf': () => new Reply(202, job('Queued')),
      'GET /api/exports/42': () => statuses.shift() ?? job('Succeeded', { progress: 100 }),
      'GET /api/exports/42/file': () => '%PDF',
    });
    const onClose = renderDialog('Draft', true);

    expect(screen.getByRole('radio', { name: /Section 1\.2 Scope/ })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: /Signature page/ })).toBeDisabled();
    await user.click(screen.getByRole('radio', { name: 'Letter' }));
    await user.click(screen.getByRole('checkbox', { name: 'Table of contents' }));
    await user.click(screen.getByRole('button', { name: 'Export' }));

    await waitFor(() => expect(onClose).toHaveBeenCalled());
    expect(calls.find((c) => c.method === 'POST')?.body).toEqual({
      logicalNodeId: section.logicalNodeId,
      pageSize: 'Letter',
      titlePage: true,
      toc: false,
      headerFooter: true,
      signaturePage: true,
    });
    expect(await screen.findByText('Exporting 1.2 Scope')).toBeInTheDocument();
    expect(screen.getByText('Waiting to start…')).toBeInTheDocument();

    await act(() => vi.advanceTimersByTimeAsync(PollIntervalMs));
    expect(await screen.findByText('Rendering… 40%')).toBeInTheDocument();
    await act(() => vi.advanceTimersByTimeAsync(PollIntervalMs));

    expect(await screen.findByText('PDF ready')).toBeInTheDocument();
    expect(click).toHaveBeenCalledWith('Manual - Draft.pdf', expect.stringMatching(/^blob:/));
    expect(calls.filter((c) => c.path === '/api/exports/42')).toHaveLength(2);
  });

  it('downloads a cached export at once and defaults to the whole document', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const { calls } = fakeApi({
      'POST /api/versions/11/exports/pdf': () =>
        new Reply(202, job('Succeeded', { progress: 100, fileName: 'Manual - v1.pdf' })),
      'GET /api/exports/42/file': () => '%PDF',
    });
    renderDialog('Signed', false);

    expect(screen.getByRole('radio', { name: 'Whole document (v1)' })).toBeChecked();
    expect(screen.getByRole('checkbox', { name: /Signature page/ })).toBeEnabled();
    await user.click(screen.getByRole('button', { name: 'Export' }));

    expect(await screen.findByText('PDF ready')).toBeInTheDocument();
    expect(click).toHaveBeenCalledWith('Manual - v1.pdf', expect.any(String));
    expect(calls.find((c) => c.method === 'POST')?.body).toMatchObject({ logicalNodeId: null, pageSize: 'A4' });
    expect(calls.some((c) => c.path === '/api/exports/42')).toBe(false);
  });

  it('shows the error of a failed export and downloads nothing', async () => {
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    fakeApi({
      'POST /api/versions/11/exports/pdf': () => new Reply(202, job('Queued')),
      'GET /api/exports/42': () => job('Failed', { error: 'The PDF could not be rendered.' }),
    });
    renderDialog('Draft', false);

    await user.click(screen.getByRole('button', { name: 'Export' }));
    await act(() => vi.advanceTimersByTimeAsync(PollIntervalMs));

    expect(await screen.findByText('PDF export failed')).toBeInTheDocument();
    expect(screen.getByText('The PDF could not be rendered.')).toBeInTheDocument();
    expect(click).not.toHaveBeenCalled();
  });
});
