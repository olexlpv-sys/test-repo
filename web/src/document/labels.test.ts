import { describe, expect, it } from 'vitest';
import { defaultVersion, descendantCount, flatten, isInSubtree, type TreeNode, type VersionHeader } from './api';
import { authorColor, authorText } from './authors';
import { kindLabel, sourceLabel } from './historyText';
import { approverStates, versionLabel } from './versions';

const user = (id: number, displayName: string) => ({ id, displayName });
const version = (
  id: number,
  status: string,
  versionNumber: number | null,
  extra: Partial<VersionHeader> = {},
): VersionHeader =>
  ({
    id,
    documentId: 1,
    rowVersion: 'rv',
    status,
    versionNumber,
    label: versionNumber ? `v${versionNumber}` : 'Draft',
    createdAt: '2026-09-01T00:00:00Z',
    createdBy: user(2, 'Alice'),
    signedAt: null,
    signedBy: [],
    basedOnVersionId: null,
    isCurrent: false,
    modifiedAfterSigning: false,
    ...extra,
  }) as VersionHeader;

const node = (id: number, children: TreeNode[] = []): TreeNode => ({
  id,
  logicalNodeId: `l${id}`,
  nodeTypeId: 1,
  title: `N${id}`,
  number: String(id),
  hasContent: true,
  sortOrder: id,
  rowVersion: 'r',
  children,
});

describe('versions', () => {
  it('opens the draft by default, else the latest signed version', () => {
    expect(defaultVersion([version(1, 'Signed', 1), version(3, 'Draft', null), version(2, 'Signed', 2)])?.id).toBe(3);
    expect(defaultVersion([version(1, 'Signed', 1), version(2, 'Signed', 2), version(3, 'Deleted', null)])?.id).toBe(2);
  });

  it('labels a draft with the version it is based on', () => {
    const versions = [version(1, 'Signed', 1), version(2, 'Draft', null, { basedOnVersionId: 1 })];
    expect(versionLabel(versions[1] as VersionHeader, versions)).toBe('Draft (based on v1)');
    expect(versionLabel(versions[0] as VersionHeader, versions)).toBe('v1');
  });

  it('tells signed, outdated and pending approvers apart', () => {
    const states = approverStates({
      requiredApprovers: [user(4, 'Carol'), user(5, 'Dave'), user(6, 'Erin')],
      signatures: [
        { user: user(4, 'Carol'), signedAt: '2026-09-01T00:00:00Z', isValid: false, comment: null },
        { user: user(5, 'Dave'), signedAt: '2026-09-01T00:00:00Z', isValid: true, comment: 'ok' },
      ],
      pendingApprovers: [user(6, 'Erin')],
      isComplete: false,
    });
    expect(states.map((s) => [s.name, s.state])).toEqual([
      ['Carol', 'outdated'],
      ['Dave', 'signed'],
      ['Erin', 'pending'],
    ]);
  });
});

describe('history labels', () => {
  it('names script changes with login and ticket', () => {
    expect(sourceLabel({ source: 'Script', user: null, dbLogin: 'jdoe', ticket: 'INC-1234' })).toBe(
      'Script · jdoe · INC-1234',
    );
    expect(sourceLabel({ source: 'App', user: user(2, 'Alice'), dbLogin: null, ticket: null })).toBe('Alice');
    expect(kindLabel('NodeTypeChanged')).toBe('type change');
    expect(kindLabel('CopiedToNewDraft')).toBe('copied to draft');
    expect(kindLabel('SomethingNew')).toBe('something new');
  });

  it('describes change authors and gives each a stable colour', () => {
    const alice = {
      entryId: 1,
      userId: 2,
      displayName: 'Alice',
      source: 'App',
      ticket: null,
      changedAt: '2026-09-01T10:15:00Z',
    };
    const script = {
      entryId: 2,
      userId: null,
      displayName: 'jdoe',
      source: 'Script',
      ticket: 'INC-1',
      changedAt: '2026-09-01T10:15:00Z',
    };
    expect(authorText(alice)).toMatch(/^Alice · /);
    expect(authorText(script)).toMatch(/^Script · jdoe · INC-1 · /);
    expect(authorColor(alice)).toBe(authorColor({ ...alice, entryId: 9 }));
  });
});

describe('tree helpers', () => {
  it('flattens in document order and validates drops', () => {
    const tree = [node(1, [node(2, [node(3)]), node(4)]), node(5)];
    expect(flatten(tree).map((f) => [f.node.id, f.depth, f.parent?.id ?? null])).toEqual([
      [1, 0, null],
      [2, 1, 1],
      [3, 2, 2],
      [4, 1, 1],
      [5, 0, null],
    ]);
    expect(descendantCount(tree[0] as TreeNode)).toBe(3);
    expect(isInSubtree(tree[0] as TreeNode, 3)).toBe(true);
    expect(isInSubtree(tree[0] as TreeNode, 5)).toBe(false);
  });
});
