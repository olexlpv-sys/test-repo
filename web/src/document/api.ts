import { useQuery } from '@tanstack/react-query';
import { api, unwrap, type Schemas } from '../api/client';
import type { ContentSchemaInfo } from '../editor/contentSchema';

export type DocumentDetails = Schemas['DocumentDetails'];
export type VersionHeader = Schemas['VersionHeader'];
export type TreeNode = Schemas['TreeNode'];
export type MyRoles = Schemas['MyRoles'];
export type NodeContentView = Schemas['NodeContentView'];
export type HistoryEntry = Schemas['HistoryEntry'];
export type ChangeSummary = Schemas['ChangeSummary'];
export type NodeChangeSummary = Schemas['NodeChangeSummary'];
export type ContentDiff = Schemas['ContentDiff'];
export type DiffBlock = Schemas['DiffBlock'];
export type DiffOp = Schemas['DiffOp'];
export type DiffAuthor = Schemas['DiffAuthor'];
export type SignatureStatus = Schemas['SignatureStatus'];

/** Query keys of the document form; everything of one document starts with `['doc', id]`. */
export const keys = {
  document: (id: number) => ['doc', id, 'details'] as const,
  myRoles: (id: number) => ['doc', id, 'my-permissions'] as const,
  tree: (id: number, versionId: number) => ['doc', id, 'tree', versionId] as const,
  content: (id: number, nodeId: number) => ['doc', id, 'content', nodeId] as const,
  signatures: (id: number, versionId: number) => ['doc', id, 'signatures', versionId] as const,
  summary: (id: number, versionId: number, since: string) => ['doc', id, 'summary', versionId, since] as const,
  nodeHistory: (id: number, logicalNodeId: string) => ['doc', id, 'node-history', logicalNodeId] as const,
  changes: (id: number, logicalNodeId: string, since: string, versionId: number) =>
    ['doc', id, 'changes', logicalNodeId, since, versionId] as const,
  entryContent: (entryId: number) => ['history-entry', entryId, 'content'] as const,
};

export function useDocument(id: number) {
  return useQuery({
    queryKey: keys.document(id),
    queryFn: () => unwrap(api.GET('/api/documents/{id}', { params: { path: { id } } })),
  });
}

export function useMyRoles(id: number) {
  return useQuery({
    queryKey: keys.myRoles(id),
    queryFn: () => unwrap(api.GET('/api/documents/{id}/my-permissions', { params: { path: { id } } })),
  });
}

export function useTree(documentId: number, versionId: number | null) {
  return useQuery({
    queryKey: keys.tree(documentId, versionId ?? 0),
    queryFn: () =>
      unwrap(api.GET('/api/versions/{versionId}/tree', { params: { path: { versionId: versionId ?? 0 } } })),
    enabled: versionId !== null,
  });
}

export function useSignatures(documentId: number, versionId: number | null, enabled: boolean) {
  return useQuery({
    queryKey: keys.signatures(documentId, versionId ?? 0),
    queryFn: () =>
      unwrap(api.GET('/api/versions/{versionId}/signatures', { params: { path: { versionId: versionId ?? 0 } } })),
    enabled: enabled && versionId !== null,
  });
}

export function useChangeSummary(documentId: number, versionId: number | null, since: string) {
  return useQuery({
    queryKey: keys.summary(documentId, versionId ?? 0, since),
    queryFn: () =>
      unwrap(
        api.GET('/api/versions/{versionId}/change-summary', {
          params: { path: { versionId: versionId ?? 0 }, query: { since } },
        }),
      ),
    enabled: versionId !== null,
    meta: { silent: true }, // e.g. no signed version yet: the badges are simply not shown
  });
}

export function useNodeTypes() {
  return useQuery({
    queryKey: ['node-types', 'all'],
    queryFn: () => unwrap(api.GET('/api/node-types', { params: { query: { includeInactive: true } } })),
    staleTime: 5 * 60_000,
  });
}

export function useContentStyles() {
  return useQuery({
    queryKey: ['content-styles'],
    queryFn: () => unwrap(api.GET('/api/content-styles')),
    staleTime: 5 * 60_000,
  });
}

export function useContentSchema() {
  return useQuery({
    queryKey: ['content-schema'],
    queryFn: async () => (await unwrap(api.GET('/api/content-schema'))) as ContentSchemaInfo,
    staleTime: Infinity,
  });
}

/** The style catalog CSS (`ds-style-*` classes) — the editor surface looks like the rendered output. */
export function useStylesheet() {
  return useQuery({
    queryKey: ['content-stylesheet'],
    queryFn: async () => {
      const { data, error, response } = await api.GET('/api/content-styles/stylesheet.css', { parseAs: 'text' });
      if (!response.ok || error !== undefined) {
        return '';
      }

      return data ?? '';
    },
    staleTime: 5 * 60_000,
    meta: { silent: true },
  });
}

/** The version shown by default: the draft if there is one, else the latest signed version. */
export function defaultVersion(versions: VersionHeader[]): VersionHeader | undefined {
  const live = versions.filter((v) => v.status !== 'Deleted');
  return (
    live.find((v) => v.status === 'Draft') ??
    live.filter((v) => v.status === 'Signed').sort((a, b) => (b.versionNumber ?? 0) - (a.versionNumber ?? 0))[0] ??
    versions[0]
  );
}

/** Flattened tree in document order with depth and parent (sections, numbering, drop validation). */
export interface FlatNode {
  node: TreeNode;
  depth: number;
  parent: TreeNode | null;
  index: number;
  siblings: TreeNode[];
}

export function flatten(
  nodes: TreeNode[],
  depth = 0,
  parent: TreeNode | null = null,
  out: FlatNode[] = [],
): FlatNode[] {
  nodes.forEach((node, index) => {
    out.push({ node, depth, parent, index, siblings: nodes });
    flatten(node.children, depth + 1, node, out);
  });
  return out;
}

export function descendantCount(node: TreeNode): number {
  return node.children.reduce((sum, child) => sum + 1 + descendantCount(child), 0);
}

/** Whether `candidate` is `node` or lies in its subtree (no drop into the own subtree). */
export function isInSubtree(node: TreeNode, candidateId: number): boolean {
  return node.id === candidateId || node.children.some((child) => isInSubtree(child, candidateId));
}
