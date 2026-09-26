import type { HistoryEntry } from './api';

const kindLabels: Record<string, string> = {
  ContentChanged: 'content',
  NodeRenamed: 'rename',
  NodeTypeChanged: 'type change',
  NodeMoved: 'move',
  NodeCreated: 'created',
  NodeDeleted: 'deleted',
  CopiedToNewDraft: 'copied to draft',
  VersionSigned: 'signed',
  VersionCreated: 'draft created',
  VersionDiscarded: 'draft discarded',
  SignatureAdded: 'signature',
  SignatureWithdrawn: 'signature withdrawn',
  PdfExportRequested: 'PDF export',
};

/** Short kind label for a timeline row (content, rename, type change, move, created, copied to draft). */
export const kindLabel = (kind: string) => kindLabels[kind] ?? kind.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();

/** Who made a change: the user, or "Script · login · ticket" for support scripts (FR-H3). */
export function sourceLabel(entry: Pick<HistoryEntry, 'source' | 'user' | 'dbLogin' | 'ticket'>): string {
  if (entry.source === 'Script') {
    return ['Script', entry.dbLogin, entry.ticket].filter(Boolean).join(' · ');
  }

  return entry.user?.displayName ?? 'System';
}

/** The server's one-line summary (e.g. "Content changed (+12 / −3 words)"). */
export const describeEntry = (entry: HistoryEntry) => entry.summary;
