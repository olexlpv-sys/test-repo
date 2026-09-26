import { Progress, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { api, describeError, unwrap, type Schemas } from '../api/client';

type ExportJob = Schemas['ExportJobStatus'];

/** How often a running export is polled (T20 UI). */
export const PollIntervalMs = 2000;

const tracked = new Set<number>();

const toastId = (jobId: number) => `pdf-export-${jobId}`;

/** Saves the job's PDF through a temporary link (the request carries the acting user, so a plain link can't be used). */
async function download(job: ExportJob): Promise<void> {
  const { data, response } = await api.GET('/api/exports/{jobId}/file', {
    params: { path: { jobId: job.jobId } },
    parseAs: 'blob',
  });
  if (!response.ok || !data) {
    throw new Error(
      response.status === 404 ? 'The file has expired; export again.' : `Download failed (${response.status}).`,
    );
  }

  const url = URL.createObjectURL(data as Blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = job.fileName ?? 'export.pdf';
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

function progressMessage(job: ExportJob) {
  return (
    <Stack gap={6}>
      <Text size="sm">{job.status === 'Queued' ? 'Waiting to start…' : `Rendering… ${job.progress}%`}</Text>
      <Progress value={job.status === 'Queued' ? 0 : job.progress} size="sm" animated aria-label="Export progress" />
    </Stack>
  );
}

/**
 * Follows an export job in a toast: progress every {@link PollIntervalMs} ms, then the automatic download (FR-E5) or the error.
 * It runs on its own, so closing the dialog or leaving the page doesn't stop it.
 */
export async function trackExport(first: ExportJob, label: string): Promise<void> {
  if (tracked.has(first.jobId)) {
    return;
  }

  tracked.add(first.jobId);
  const id = toastId(first.jobId);
  notifications.show({
    id,
    loading: true,
    title: `Exporting ${label}`,
    message: progressMessage(first),
    autoClose: false,
    withCloseButton: false,
  });
  try {
    let job = first;
    while (job.status === 'Queued' || job.status === 'Running') {
      await new Promise((resolve) => setTimeout(resolve, PollIntervalMs));
      job = await unwrap(api.GET('/api/exports/{jobId}', { params: { path: { jobId: job.jobId } } }));
      notifications.update({
        id,
        loading: true,
        title: `Exporting ${label}`,
        message: progressMessage(job),
        autoClose: false,
        withCloseButton: false,
      });
    }

    if (job.status === 'Failed') {
      notifications.update({
        id,
        loading: false,
        color: 'red',
        title: 'PDF export failed',
        message: job.error ?? 'The export failed.',
        autoClose: false,
        withCloseButton: true,
      });
      return;
    }

    await download(job);
    notifications.update({
      id,
      loading: false,
      color: 'green',
      title: 'PDF ready',
      message: job.fileName ?? 'Downloaded.',
      autoClose: 6000,
      withCloseButton: true,
    });
  } catch (error) {
    notifications.update({
      id,
      loading: false,
      color: 'red',
      autoClose: false,
      withCloseButton: true,
      ...describeError(error),
    });
  } finally {
    tracked.delete(first.jobId);
  }
}
