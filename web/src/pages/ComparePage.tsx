import { useMemo, useState } from 'react';
import {
  ActionIcon,
  Button,
  Center,
  Group,
  Loader,
  SegmentedControl,
  Select,
  Switch,
  Text,
  Tooltip,
} from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { api, unwrap, type Schemas } from '../api/client';
import { useDocument, type VersionHeader } from '../document/api';
import { diffSide } from '../document/diffSide';
import { TrackChanges } from '../document/TrackChanges';

type CompareNode = Schemas['CompareNode'];

/** Base: the latest signed version, or the one before it when the target is that version. */
function defaults(versions: VersionHeader[]): { base: string; target: string } {
  const signed = versions
    .filter((v) => v.status === 'Signed')
    .sort((a, b) => (b.versionNumber ?? 0) - (a.versionNumber ?? 0));
  const draft = versions.some((v) => v.status === 'Draft');
  if (draft) {
    return { base: signed[0] ? String(signed[0].id) : 'latestSigned', target: 'draft' };
  }

  return { base: String(signed[1]?.id ?? signed[0]?.id ?? ''), target: String(signed[0]?.id ?? '') };
}

function flatten(nodes: CompareNode[], depth = 0, out: { node: CompareNode; depth: number }[] = []) {
  for (const node of nodes) {
    out.push({ node, depth });
    flatten(node.children, depth + 1, out);
  }

  return out;
}

const statusTone: Record<string, string | undefined> = { Added: 'green', Removed: 'red', Modified: 'amber' };

/** Version comparison (T16 §1, FR-C1/FR-C2): summary, merged tree with statuses, and the selected node's text diff. */
export function ComparePage() {
  const { id } = useParams();
  const documentId = Number(id);
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const document = useDocument(documentId);
  const versions = document.data?.versions ?? [];
  const fallback = defaults(versions);
  const base = params.get('base') ?? fallback.base;
  const target = params.get('target') ?? fallback.target;
  const [changedOnly, setChangedOnly] = useState(true);
  const [layout, setLayout] = useState<'inline' | 'side'>('inline');
  const selected = params.get('node');

  const set = (changes: Record<string, string | null>) =>
    setParams(
      (current) => {
        const next = new URLSearchParams(current);
        for (const [key, value] of Object.entries(changes)) {
          if (value === null) {
            next.delete(key);
          } else {
            next.set(key, value);
          }
        }

        return next;
      },
      { replace: true },
    );

  const ready = document.isSuccess && base !== '' && target !== '';
  const comparison = useQuery({
    queryKey: ['doc', documentId, 'compare', base, target, !changedOnly],
    queryFn: () =>
      unwrap(
        api.GET('/api/documents/{id}/compare', {
          params: { path: { id: documentId }, query: { base, target, includeUnchanged: !changedOnly } },
        }),
      ),
    enabled: ready,
  });
  const nodeDiff = useQuery({
    queryKey: ['doc', documentId, 'compare-node', base, target, selected ?? ''],
    queryFn: () =>
      unwrap(
        api.GET('/api/documents/{id}/compare/nodes/{logicalNodeId}', {
          params: { path: { id: documentId, logicalNodeId: selected ?? '' }, query: { base, target } },
        }),
      ),
    enabled: ready && selected !== null,
  });

  const rows = useMemo(() => flatten(comparison.data?.tree ?? []), [comparison.data]);
  const selectedNode = rows.find((r) => r.node.logicalNodeId === selected)?.node;

  const options = [
    ...versions.filter((v) => v.status === 'Signed').map((v) => ({ value: String(v.id), label: v.label })),
    ...(versions.some((v) => v.status === 'Draft') ? [{ value: 'draft', label: 'Current draft' }] : []),
  ];

  if (document.isError) {
    return <Text c="red">The document could not be opened.</Text>;
  }

  if (!document.data) {
    return (
      <Center h={300}>
        <Loader />
      </Center>
    );
  }

  const summary = comparison.data?.summary;
  return (
    <div className="dh-document">
      <Group justify="space-between" wrap="wrap" gap={12}>
        <Group gap={12} wrap="wrap">
          <Button variant="soft" color="gray" onClick={() => navigate(`/documents/${documentId}`)}>
            ← Document
          </Button>
          <span className="dh-section-title">Compare · {document.data.title}</span>
        </Group>
        <Group gap={8} wrap="wrap">
          <Select
            aria-label="Base"
            label="Base"
            w={170}
            data={options}
            value={base}
            allowDeselect={false}
            onChange={(v) => v && set({ base: v, node: null })}
            comboboxProps={{ withinPortal: true }}
          />
          <Tooltip label="Swap base and target">
            <ActionIcon
              variant="soft"
              size={38}
              mt={24}
              aria-label="Swap base and target"
              onClick={() => set({ base: target, target: base, node: null })}
            >
              ⇄
            </ActionIcon>
          </Tooltip>
          <Select
            aria-label="Target"
            label="Target"
            w={170}
            data={options}
            value={target}
            allowDeselect={false}
            onChange={(v) => v && set({ target: v, node: null })}
            comboboxProps={{ withinPortal: true }}
          />
        </Group>
      </Group>

      {summary && (
        <Group gap={8} className="dh-compare-summary" data-testid="compare-summary">
          <span className="dh-chip" data-tone="green">
            +{summary.added} added
          </span>
          <span className="dh-chip" data-tone="red">
            −{summary.removed} removed
          </span>
          <span className="dh-chip">{summary.moved} moved</span>
          <span className="dh-chip">{summary.renamed} renamed</span>
          <span className="dh-chip">{summary.typeChanged} type changed</span>
          <span className="dh-chip" data-tone="amber">
            {summary.contentChanged} content changed
          </span>
          <Switch
            label="Changed only"
            checked={changedOnly}
            onChange={(e) => setChangedOnly(e.currentTarget.checked)}
            ml="auto"
          />
        </Group>
      )}

      <div className="dh-document-body">
        <section className="dh-card dh-structure" aria-label="Compared structure">
          <div className="dh-card-header">
            <span className="dh-card-title">
              {comparison.data ? `${comparison.data.base.label} → ${comparison.data.target.label}` : 'Structure'}
            </span>
          </div>
          <div className="dh-card-body dh-tree-body" role="tree" aria-label="Compared structure">
            {comparison.isPending && ready && <Loader size="sm" />}
            {comparison.isError && (
              <Text c="red" size="sm">
                These versions cannot be compared.
              </Text>
            )}
            {comparison.isSuccess && rows.length === 0 && (
              <Text className="dh-muted" size="sm" px={8}>
                No differences.
              </Text>
            )}
            {rows.map(({ node, depth }) => {
              const side = node.target ?? node.base;
              const moved = node.changes.includes('Moved');
              return (
                <div
                  key={node.logicalNodeId}
                  role="treeitem"
                  aria-selected={node.logicalNodeId === selected}
                  className="dh-tree-row dh-compare-row"
                  data-status={node.status}
                  data-selected={node.logicalNodeId === selected || undefined}
                  data-testid={`compare-${node.logicalNodeId}`}
                  style={{ paddingLeft: 10 + depth * 16 }}
                  tabIndex={0}
                  onClick={() => set({ node: node.logicalNodeId })}
                  onKeyDown={(e) => e.key === 'Enter' && set({ node: node.logicalNodeId })}
                >
                  <span className="dh-tree-number">{side?.number}</span>
                  <span className="dh-tree-title">
                    {node.changes.includes('Renamed') && node.base ? (
                      <>
                        <del>{node.base.title}</del> {node.target?.title}
                      </>
                    ) : (
                      side?.title
                    )}
                  </span>
                  {moved && (
                    <Tooltip label={`Moved from ${node.base?.number ?? '?'} → ${node.target?.number ?? '?'}`}>
                      <span aria-label={`Moved from ${node.base?.number ?? '?'} to ${node.target?.number ?? '?'}`}>
                        ↪
                      </span>
                    </Tooltip>
                  )}
                  {node.changes.includes('TypeChanged') && (
                    <Tooltip label={`Type ${node.base?.nodeType ?? '?'} → ${node.target?.nodeType ?? '?'}`}>
                      <span className="dh-chip dh-tree-type">{node.target?.nodeType}</span>
                    </Tooltip>
                  )}
                  {node.status !== 'Unchanged' && (
                    <span className="dh-chip dh-tree-type" data-tone={statusTone[node.status]}>
                      {node.status}
                    </span>
                  )}
                  {node.contentStats && (node.contentStats.inserted > 0 || node.contentStats.deleted > 0) && (
                    <span className="dh-tree-changes">
                      +{node.contentStats.inserted}/−{node.contentStats.deleted}
                    </span>
                  )}
                </div>
              );
            })}
          </div>
        </section>

        <section className="dh-card" aria-label="Text differences">
          <div className="dh-card-header">
            <span className="dh-card-title">
              {selectedNode
                ? `${(selectedNode.target ?? selectedNode.base)?.number ?? ''} ${(selectedNode.target ?? selectedNode.base)?.title ?? ''}`
                : 'Text differences'}
            </span>
            <SegmentedControl
              size="xs"
              value={layout}
              onChange={(v) => setLayout(v as 'inline' | 'side')}
              data={[
                { value: 'inline', label: 'Inline' },
                { value: 'side', label: 'Side by side' },
              ]}
            />
          </div>
          <div className="dh-card-body" style={{ background: 'var(--dh-surface)' }}>
            {!selected && <Text className="dh-muted">Select a node to see its text changes.</Text>}
            {selected && nodeDiff.isPending && <Loader size="sm" />}
            {nodeDiff.isError && <Text c="red">The text differences could not be loaded.</Text>}
            {selected &&
              nodeDiff.isSuccess &&
              (layout === 'inline' ? (
                <TrackChanges blocks={nodeDiff.data.blocks} emptyText="The text is the same in both versions." />
              ) : (
                <div className="dh-side-by-side">
                  <div>
                    <Text size="xs" className="dh-muted" mb={4}>
                      {comparison.data?.base.label}
                    </Text>
                    <TrackChanges
                      blocks={diffSide(nodeDiff.data.blocks, 'base')}
                      emptyText="The text is the same in both versions."
                    />
                  </div>
                  <div>
                    <Text size="xs" className="dh-muted" mb={4}>
                      {comparison.data?.target.label}
                    </Text>
                    <TrackChanges
                      blocks={diffSide(nodeDiff.data.blocks, 'target')}
                      emptyText="The text is the same in both versions."
                    />
                  </div>
                </div>
              ))}
          </div>
        </section>
      </div>
    </div>
  );
}
