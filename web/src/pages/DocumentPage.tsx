import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Center, Group, Loader, Select, Switch, Text, TextInput } from '@mantine/core';
import { useQuery } from '@tanstack/react-query';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useParams, useSearchParams } from 'react-router-dom';
import type { Editor } from '@tiptap/core';
import { api, unwrap } from '../api/client';
import {
  defaultVersion,
  flatten,
  useChangeSummary,
  useContentSchema,
  useContentStyles,
  useDocument,
  useMyRoles,
  useNodeTypes,
  useSignatures,
  useStylesheet,
  useTree,
  type TreeNode,
} from '../document/api';
import { Section } from '../document/Section';
import { RemovedSection } from '../document/RemovedSection';
import { StructureTree } from '../document/StructureTree';
import { VersionBar } from '../document/VersionBar';
import { createExtensions } from '../editor/extensions';
import { EditorHubContext, type EditorHub } from '../editor/editorHub';
import { Ribbon } from '../editor/Ribbon';

type Removed = NonNullable<ReturnType<typeof useChangeSummary>['data']>['removed'][number];
type Item = { kind: 'node'; node: TreeNode; depth: number } | { kind: 'removed'; removed: Removed; depth: number };

/** The document form (T15, FR-UI2): version bar, ribbon, structure tree and the page of sections with inline history. */
export function DocumentPage() {
  const { id } = useParams();
  const documentId = Number(id);
  const [params, setParams] = useSearchParams();
  const document = useDocument(documentId);
  const roles = useMyRoles(documentId);
  const schema = useContentSchema();
  const styles = useContentStyles();
  const stylesheet = useStylesheet();
  const nodeTypes = useNodeTypes();

  const versions = useMemo(() => document.data?.versions ?? [], [document.data]);
  const requested = Number(params.get('version'));
  const version = versions.find((v) => v.id === requested) ?? defaultVersion(versions);
  const isDraft = version?.status === 'Draft' && document.data?.status !== 'Deleted';
  const tree = useTree(documentId, version?.id ?? null);
  const signatures = useSignatures(documentId, version?.id ?? null, isDraft);
  const hasSigned = versions.some((v) => v.status === 'Signed');

  // "Show changes since": off by default; the badges always count since the latest signed version (or the chosen baseline).
  const [trackOn, setTrackOn] = useState(false);
  const [baseline, setBaseline] = useState('latestSigned');
  const [date, setDate] = useState('');
  const since = baseline === 'date' ? (date ? `d:${date}` : 'latestSigned') : baseline;
  const summary = useChangeSummary(documentId, version?.id ?? null, since);
  const summaryMap = useMemo(
    () => new Map((summary.data?.nodes ?? []).map((n) => [n.logicalNodeId, n])),
    [summary.data],
  );
  const comments = useQuery({
    queryKey: ['doc', documentId, 'comment-counts', version?.id ?? 0],
    queryFn: () =>
      unwrap(
        api.GET('/api/documents/{id}/comments/counts', {
          params: { path: { id: documentId }, query: { versionId: version?.id } },
        }),
      ),
    enabled: version !== undefined,
    meta: { silent: true },
  });

  const [active, setActive] = useState<Editor | null>(null);
  const [warned] = useState(() => new Set<number>());
  const hub = useMemo<EditorHub>(() => ({ active, setActive, warned }), [active, warned]);

  const styleIds = useMemo(
    () => new Set((styles.data ?? []).filter((s) => s.isActive).map((s) => s.styleId)),
    [styles.data],
  );
  const extensions = useMemo(
    () => (schema.data ? createExtensions(schema.data, styleIds) : null),
    [schema.data, styleIds],
  );
  const paragraphStyles = useMemo(
    () =>
      (styles.data ?? [])
        .filter((s) => s.isActive && String(s.kind).toLowerCase() === 'paragraph')
        .map((s) => ({ styleId: s.styleId, name: s.name })),
    [styles.data],
  );
  const typeMap = useMemo(() => new Map((nodeTypes.data ?? []).map((t) => [t.id, t])), [nodeTypes.data]);

  const flat = useMemo(() => flatten(tree.data ?? []), [tree.data]);
  const items = useMemo<Item[]>(() => {
    const list: Item[] = flat.map((f) => ({ kind: 'node', node: f.node, depth: f.depth }));
    if (!trackOn) {
      return list;
    }

    // Sections deleted since the baseline: placeholders at their former position (under the former parent, by index).
    for (const removed of summary.data?.removed ?? []) {
      const parentIndex = removed.formerParentLogicalNodeId
        ? list.findIndex((i) => i.kind === 'node' && i.node.logicalNodeId === removed.formerParentLogicalNodeId)
        : -1;
      const parent = parentIndex >= 0 ? (list[parentIndex] as Extract<Item, { kind: 'node' }>) : null;
      const siblings = parent ? parent.node.children : (tree.data ?? []);
      const next = siblings[removed.formerPosition];
      let at = next ? list.findIndex((i) => i.kind === 'node' && i.node.id === next.id) : -1;
      if (at < 0) {
        // After the parent's last descendant (or at the end for top-level nodes).
        at = parentIndex >= 0 ? parentIndex + 1 : list.length;
        while (parent && at < list.length && (list[at]?.depth ?? 0) > parent.depth) {
          at++;
        }
      }

      list.splice(at, 0, { kind: 'removed', removed, depth: parent ? parent.depth + 1 : 0 });
    }

    return list;
  }, [flat, trackOn, summary.data, tree.data]);

  const selectedParam = Number(params.get('node'));
  const selectedId = flat.some((f) => f.node.id === selectedParam) ? selectedParam : null;
  const scrollRef = useRef<HTMLDivElement>(null);
  // eslint-disable-next-line react-hooks/incompatible-library -- TanStack Virtual is the documented virtualizer here
  const virtualizer = useVirtualizer({
    count: items.length,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => 160,
    initialRect: { width: 800, height: 900 }, // before the first measurement (and in tests)
    overscan: 4,
    getItemKey: (index) => {
      const item = items[index];
      return item?.kind === 'node' ? `n${item.node.id}` : `r${item?.removed.logicalNodeId ?? index}`;
    },
  });

  const setUrl = useCallback(
    (changes: Record<string, string | null>) =>
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
      ),
    [setParams],
  );

  const scrollTo = useCallback(
    (nodeId: number) => {
      const index = items.findIndex((i) => i.kind === 'node' && i.node.id === nodeId);
      if (index >= 0) {
        virtualizer.scrollToIndex(index, { align: 'start' });
      }
    },
    [items, virtualizer],
  );

  // A deep link (?node=) scrolls to its section once the tree is there.
  const scrolledFor = useRef<number | null>(null);
  useEffect(() => {
    if (selectedId !== null && scrolledFor.current !== selectedId && items.length > 0) {
      scrolledFor.current = selectedId;
      scrollTo(selectedId);
    }
  }, [selectedId, items.length, scrollTo]);

  const onActivate = useCallback(
    (node: TreeNode) => {
      if (node.id !== selectedParam) {
        scrolledFor.current = node.id; // the section is already in view
        setUrl({ node: String(node.id) });
      }
    },
    [selectedParam, setUrl],
  );

  if (document.isError) {
    return <Text c="red">The document could not be opened.</Text>;
  }

  if (!document.data || !version || !schema.data || !extensions) {
    return (
      <Center h={300}>
        <Loader />
      </Center>
    );
  }

  const myRoles = roles.data;
  const canEditStructure = isDraft && (myRoles?.canEditStructure ?? false);
  const editableIds = new Set(myRoles?.editableLogicalNodeIds ?? []);
  const isEditable = (logicalNodeId: string) =>
    isDraft && ((myRoles?.canEditAllContent ?? false) || editableIds.has(logicalNodeId));
  const validSignatures = (signatures.data?.signatures ?? []).filter((s) => s.isValid).length;

  return (
    <EditorHubContext.Provider value={hub}>
      {stylesheet.data && <style>{stylesheet.data}</style>}
      <div className="dh-document">
        <VersionBar
          document={document.data}
          version={version}
          roles={myRoles}
          signatures={signatures.data}
          onSelectVersion={(versionId) => {
            setTrackOn(false);
            setUrl({ version: String(versionId), node: null });
          }}
        />
        <Group className="dh-toolbar-row" justify="space-between" wrap="nowrap" align="flex-start">
          <Ribbon
            editor={active && !active.isDestroyed ? active : null}
            schema={schema.data}
            styles={paragraphStyles}
          />
          <Group gap={8} wrap="nowrap" className="dh-track-toggle">
            <Text size="sm">Show changes since</Text>
            <Select
              aria-label="Changes since"
              size="xs"
              w={170}
              allowDeselect={false}
              data={[
                { value: 'latestSigned', label: 'Latest signed version' },
                ...versions.filter((v) => v.status === 'Signed').map((v) => ({ value: `v:${v.id}`, label: v.label })),
                { value: 'date', label: 'A date…' },
              ]}
              value={baseline}
              onChange={(value) => value && setBaseline(value)}
              comboboxProps={{ withinPortal: true }}
            />
            {baseline === 'date' && (
              <TextInput
                aria-label="Since date"
                type="date"
                size="xs"
                value={date}
                onChange={(e) => setDate(e.currentTarget.value)}
              />
            )}
            <Switch
              aria-label="Show changes"
              checked={trackOn}
              disabled={!hasSigned && baseline !== 'date'}
              onChange={(e) => setTrackOn(e.currentTarget.checked)}
            />
          </Group>
        </Group>
        <div className="dh-document-body">
          <StructureTree
            documentId={documentId}
            versionId={version.id}
            tree={tree.data ?? []}
            nodeTypes={nodeTypes.data ?? []}
            canEditStructure={canEditStructure}
            isEditable={isEditable}
            selectedId={selectedId}
            onSelect={(node) => {
              setUrl({ node: String(node.id) });
              scrolledFor.current = node.id;
              scrollTo(node.id);
            }}
            summary={summaryMap}
            comments={comments.data?.nodes ?? {}}
          />
          <div className="dh-page-scroll" ref={scrollRef} data-testid="page-scroll">
            <div className="dh-paper" style={{ height: virtualizer.getTotalSize() }}>
              {tree.isSuccess && items.length === 0 && (
                <Text className="dh-muted" p={40}>
                  {canEditStructure
                    ? 'Empty document — add the first node in the structure panel.'
                    : 'This version has no sections.'}
                </Text>
              )}
              {virtualizer.getVirtualItems().map((row) => {
                const item = items[row.index];
                if (!item) {
                  return null;
                }

                return (
                  <div
                    key={row.key}
                    data-index={row.index}
                    ref={virtualizer.measureElement}
                    className="dh-paper-row"
                    style={{ transform: `translateY(${row.start}px)` }}
                  >
                    {item.kind === 'node' ? (
                      <Section
                        documentId={documentId}
                        version={version}
                        versions={versions}
                        node={item.node}
                        depth={item.depth}
                        editable={isEditable(item.node.logicalNodeId)}
                        canRestore={isEditable(item.node.logicalNodeId)}
                        schema={schema.data}
                        extensions={extensions}
                        summary={summaryMap.get(item.node.logicalNodeId)}
                        trackSince={trackOn ? since : null}
                        nodeTypes={typeMap}
                        comments={comments.data?.nodes[item.node.logicalNodeId] ?? 0}
                        signaturesToOutdate={validSignatures}
                        selected={item.node.id === selectedId}
                        onActivate={onActivate}
                      />
                    ) : (
                      <RemovedSection removed={item.removed} depth={item.depth} />
                    )}
                  </div>
                );
              })}
            </div>
          </div>
        </div>
      </div>
    </EditorHubContext.Provider>
  );
}
