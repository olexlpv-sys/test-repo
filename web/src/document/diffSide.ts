import type { DiffBlock } from './api';

/** One side of a diff for a side-by-side view: the base without insertions, or the target without deletions. */
export function diffSide(blocks: DiffBlock[], side: 'base' | 'target'): DiffBlock[] {
  const drop = side === 'base' ? 'insert' : 'delete';
  const dropBlock = side === 'base' ? 'inserted' : 'deleted';
  const walk = (list: DiffBlock[]): DiffBlock[] =>
    list
      .filter((b) => b.status !== dropBlock)
      .map((b) => ({
        ...b,
        ops: b.ops?.filter((o) => o.op !== drop),
        children: b.children ? walk(b.children) : b.children,
        rows: b.rows?.map((row) =>
          row.map((cell) => ({ ...cell, ops: cell.ops.filter((o) => o.op !== drop), blocks: walk(cell.blocks) })),
        ),
      }));
  return walk(blocks);
}
