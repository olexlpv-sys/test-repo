import { memo, useCallback, useState } from 'react';
import { Button, Group, Loader, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import type { Extensions, JSONContent } from '@tiptap/core';
import { api, unwrap, type Schemas } from '../api/client';
import { fromSchema, type ContentSchemaInfo } from '../editor/contentSchema';
import { SectionEditor, type SaveStatus } from '../editor/SectionEditor';
import { keys, type HistoryEntry, type NodeChangeSummary, type TreeNode, type VersionHeader } from './api';
import { SectionHistory } from './SectionHistory';
import { sinceText } from './historyText';
import { TrackChanges } from './TrackChanges';

type NodeType = Schemas['NodeTypeResponse'];

export interface SectionProps {
  documentId: number;
  version: VersionHeader;
  versions: VersionHeader[];
  node: TreeNode;
  depth: number;
  editable: boolean;
  canRestore: boolean;
  schema: ContentSchemaInfo;
  extensions: Extensions;
  summary: NodeChangeSummary | undefined;
  /** Document-wide "Show changes since" baseline, or null (off). */
  trackSince: string | null;
  nodeTypes: Map<number, NodeType>;
  comments: number;
  signaturesToOutdate: number;
  selected: boolean;
  onActivate: (node: TreeNode) => void;
  /** Opens the comments panel (the section is activated first, so its threads show). */
  onOpenComments: () => void;
}

type Mode = { kind: 'edit' } | { kind: 'view'; entry: HistoryEntry } | { kind: 'compare'; entry: HistoryEntry };

function statusText(status: SaveStatus): string | null {
  switch (status.kind) {
    case 'saving':
      return 'Saving…';
    case 'saved':
      return `Saved · ${status.at.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}`;
    case 'unsaved':
      return 'Unsaved changes';
    case 'conflict':
      return 'Conflict';
    case 'invalid':
      return `Validation error: ${status.message}`;
    case 'error':
      return `Not saved: ${status.message}`;
    default:
      return null;
  }
}

/** "moved from 2.1", "renamed from 'Scope'", "type Section → Subsection", "new since v2". */
function structuralText(
  summary: NodeChangeSummary | undefined,
  nodeTypes: Map<number, NodeType>,
  sinceLabel: string,
): string[] {
  return (summary?.structural ?? []).map((s) => {
    switch (s.kind) {
      case 'Added':
        return `new since ${sinceLabel}`;
      case 'Moved':
        return `moved from ${s.oldNumber ?? '?'}`;
      case 'Renamed':
        return `renamed from '${s.oldTitle ?? ''}'`;
      case 'TypeChanged':
        return `type ${nodeTypes.get(s.oldNodeTypeId ?? 0)?.name ?? '?'} → ${nodeTypes.get(s.newNodeTypeId ?? 0)?.name ?? '?'}`;
      default:
        return s.kind;
    }
  });
}

/** One section of the page: numbered heading, change indicators, inline history, and the text (editor, historical view or track changes). */
export const Section = memo(function Section(props: SectionProps) {
  const {
    documentId,
    version,
    versions,
    node,
    depth,
    editable,
    canRestore,
    schema,
    extensions,
    summary,
    trackSince,
    nodeTypes,
    comments,
    signaturesToOutdate,
    selected,
    onActivate,
    onOpenComments,
  } = props;
  const [historyOpen, setHistoryOpen] = useState(false);
  const [mode, setMode] = useState<Mode>({ kind: 'edit' });
  const [status, setStatus] = useState<SaveStatus>({ kind: 'idle' });
  const [replacement, setReplacement] = useState<JSONContent | null>(null);
  const clearReplacement = useCallback(() => setReplacement(null), []);

  const content = useQuery({
    queryKey: keys.content(documentId, node.id),
    queryFn: () => unwrap(api.GET('/api/nodes/{nodeId}/content', { params: { path: { nodeId: node.id } } })),
    staleTime: version.status === 'Draft' ? 0 : Infinity,
    refetchOnWindowFocus: version.status === 'Draft', // others may have changed the draft meanwhile
  });

  // Compare with an entry, or the document-wide baseline: attributed diff (loaded only while shown).
  const since = mode.kind === 'compare' ? `e:${mode.entry.id}` : trackSince;
  const changes = useQuery({
    queryKey: keys.changes(documentId, node.logicalNodeId, since ?? '', version.id),
    queryFn: () =>
      unwrap(
        api.GET('/api/documents/{id}/nodes/{logicalNodeId}/changes', {
          params: { path: { id: documentId, logicalNodeId: node.logicalNodeId }, query: { since: since ?? undefined } },
        }),
      ),
    enabled:
      since !== null &&
      mode.kind !== 'view' &&
      (mode.kind === 'compare' || (summary?.changeCount ?? 0) > 0 || (summary?.structural.length ?? 0) > 0),
  });

  const historical = useQuery({
    queryKey: keys.entryContent(mode.kind === 'view' ? mode.entry.id : 0),
    queryFn: () =>
      unwrap(
        api.GET('/api/history/entries/{entryId}/content', {
          params: { path: { entryId: mode.kind === 'view' ? mode.entry.id : 0 } },
        }),
      ),
    enabled: mode.kind === 'view',
  });

  const restore = async (entry: HistoryEntry) => {
    const old = await unwrap(
      api.GET('/api/history/entries/{entryId}/content', { params: { path: { entryId: entry.id } } }),
    );
    setMode({ kind: 'edit' });
    setReplacement(fromSchema(old.contentJson));
  };

  const changed = (summary?.changeCount ?? 0) > 0 || (summary?.structural.length ?? 0) > 0;
  const warn = summary?.hasScriptChange || summary?.hasChangeAfterSigning;
  const headingLevel = Math.min(depth + 1, 6);
  const trackOn = trackSince !== null && changed;
  const readOnlyView = mode.kind !== 'edit' || trackOn;
  const structural = trackSince !== null ? structuralText(summary, nodeTypes, sinceText(trackSince, versions)) : [];
  const saveText = statusText(status);

  return (
    <section
      className="dh-page-section"
      data-changed={changed || undefined}
      data-selected={selected || undefined}
      data-testid={`section-${node.id}`}
      aria-label={`${node.number} ${node.title}`}
      onFocusCapture={() => onActivate(node)}
      onClick={() => onActivate(node)}
    >
      <div className="dh-page-section-header">
        <div
          className={`dh-page-section-title ds-style-Heading${headingLevel}`}
          data-muted={!editable && version.status === 'Draft' ? true : undefined}
          role="heading"
          aria-level={headingLevel}
        >
          <span className="dh-page-section-number">{node.number}</span> {node.title}
        </div>
        <Group gap={6} wrap="nowrap" className="dh-page-section-tools">
          {saveText && (
            <Text size="xs" className="dh-save-status" data-kind={status.kind} data-testid="save-status">
              {saveText}
            </Text>
          )}
          {warn && (
            <Tooltip label={summary?.hasChangeAfterSigning ? 'Changed after signing' : 'Changed by a support script'}>
              <span className="dh-warn" aria-label="Script or after-signing change">
                ⚠
              </span>
            </Tooltip>
          )}
          <Tooltip label={`${summary?.changeCount ?? 0} change(s) since the baseline — show history`}>
            <UnstyledButton
              className="dh-history-toggle"
              aria-expanded={historyOpen}
              aria-label={`History of ${node.title}`}
              data-testid={`history-toggle-${node.id}`}
              onClick={(e) => {
                e.stopPropagation();
                setHistoryOpen((o) => !o);
              }}
            >
              🕘 {summary?.changeCount ?? 0} {historyOpen ? '▲' : ''}
            </UnstyledButton>
          </Tooltip>
          <Tooltip label={comments > 0 ? `${comments} comment(s) — open` : 'Comments'}>
            <UnstyledButton
              className="dh-section-tool dh-comment-count"
              aria-label={`Comments on ${node.title}`}
              onClick={() => {
                onActivate(node);
                onOpenComments();
              }}
            >
              💬 {comments > 0 ? comments : ''}
            </UnstyledButton>
          </Tooltip>
        </Group>
      </div>
      {structural.length > 0 && <div className="dh-structural">{structural.join(' · ')}</div>}

      {historyOpen && (
        <SectionHistory
          documentId={documentId}
          logicalNodeId={node.logicalNodeId}
          versions={versions}
          activeEntryId={mode.kind === 'edit' ? null : mode.entry.id}
          canRestore={canRestore && editable}
          onView={(entry) => setMode({ kind: 'view', entry })}
          onCompare={(entry) => setMode({ kind: 'compare', entry })}
          onRestore={(entry) => void restore(entry)}
        />
      )}

      {readOnlyView && (
        <div className="dh-view-banner">
          <span>
            {mode.kind === 'view'
              ? `As it was on ${new Date(mode.entry.changedAt).toLocaleString()} (read-only)`
              : mode.kind === 'compare'
                ? `Changes since ${new Date(mode.entry.changedAt).toLocaleString()}`
                : `Changes since ${sinceText(trackSince ?? '', versions)} — turn off "Show changes" to edit`}
          </span>
          {mode.kind !== 'edit' && (
            <Button size="compact-xs" variant="soft" color="gray" onClick={() => setMode({ kind: 'edit' })}>
              Back to current text
            </Button>
          )}
        </div>
      )}

      {mode.kind === 'view' ? (
        historical.isSuccess ? (
          // Server-rendered HTML: text is HTML-encoded by the renderer (content-format.md §2), no user markup.
          <div
            className="dh-section-text dh-historical"
            dangerouslySetInnerHTML={{ __html: historical.data.contentHtml }}
          />
        ) : (
          <Loader size="sm" />
        )
      ) : readOnlyView ? (
        changes.isSuccess ? (
          <TrackChanges blocks={changes.data.blocks} />
        ) : changes.isError ? (
          <Text size="sm" c="red">
            Changes could not be loaded.
          </Text>
        ) : (
          <Loader size="sm" />
        )
      ) : content.isSuccess ? (
        <SectionEditor
          documentId={documentId}
          versionId={version.id}
          content={content.data}
          editable={editable}
          schema={schema}
          extensions={extensions}
          signaturesToOutdate={signaturesToOutdate}
          replacement={replacement}
          onReplaced={clearReplacement}
          onStatus={setStatus}
        />
      ) : content.isError ? (
        <Text size="sm" c="red">
          The text could not be loaded.
        </Text>
      ) : (
        <div className="dh-section-placeholder" />
      )}
    </section>
  );
});
