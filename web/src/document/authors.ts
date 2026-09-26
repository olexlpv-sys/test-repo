import type { DiffAuthor } from './api';

const palette = ['#1E5BDC', '#B42318', '#1F9D3A', '#B45309', '#7A2FD0', '#0E7490', '#C11574', '#4D5B1F'];

/** A stable colour per author (user id, or login/ticket for scripts). */
export function authorColor(author: DiffAuthor | null | undefined): string {
  const key = author
    ? String(author.userId ?? `${author.source}:${author.displayName ?? ''}:${author.ticket ?? ''}`)
    : '';
  let hash = 0;
  for (const c of key) {
    hash = (hash * 31 + c.charCodeAt(0)) | 0;
  }

  return palette[Math.abs(hash) % palette.length] ?? '#1E5BDC';
}

/** "Alice · 26.09.2026, 10:15" or "Script · INC-1234 · …" — shown when hovering a change. */
export function authorText(author: DiffAuthor | null | undefined): string {
  if (!author) {
    return 'Unknown author';
  }

  const who =
    author.source === 'Script'
      ? ['Script', author.displayName, author.ticket].filter(Boolean).join(' · ')
      : (author.displayName ?? 'Unknown user');
  return `${who} · ${new Date(author.changedAt).toLocaleString()}`;
}
