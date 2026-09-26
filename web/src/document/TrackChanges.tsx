import type { ReactNode } from 'react';
import type { DiffBlock, DiffOp } from './api';
import { authorColor, authorText } from './authors';

function Op({ op }: { op: DiffOp }) {
  const text = op.text.split('\n').flatMap((part, i) => (i === 0 ? [part] : [<br key={i} />, part]));
  if (op.op === 'equal') {
    return <>{text}</>;
  }

  const color = authorColor(op.by);
  const what =
    op.op === 'insert' ? 'Inserted' : op.op === 'delete' ? 'Deleted' : `Formatting (${(op.changes ?? []).join(', ')})`;
  // Version comparison has no authors: then the kind of change alone.
  const title = `${what}${op.by ? ` by ${authorText(op.by)}` : ''}${
    op.formatBy ? `; formatted by ${authorText(op.formatBy)}` : ''
  }`;
  const common = {
    title,
    'data-author': op.by?.displayName ?? op.by?.source ?? '',
    className: `dh-tc-${op.op}`,
    style: { '--dh-author': color } as React.CSSProperties,
  };
  if (op.op === 'insert') {
    return <ins {...common}>{text}</ins>;
  }

  if (op.op === 'delete') {
    return <del {...common}>{text}</del>;
  }

  return <span {...common}>{text}</span>;
}

function Ops({ ops }: { ops: DiffOp[] }) {
  return (
    <>
      {ops.map((op, i) => (
        <Op key={i} op={op} />
      ))}
    </>
  );
}

function Block({ block }: { block: DiffBlock }): ReactNode {
  const status = block.status === 'equal' ? undefined : block.status;
  const children = (block.children ?? []).map((child, i) => <Block key={i} block={child} />);
  switch (block.type) {
    case 'paragraph':
    case 'heading': {
      const content = block.ops && block.ops.length > 0 ? <Ops ops={block.ops} /> : <br />;
      return block.type === 'heading' ? (
        <div role="heading" aria-level={3} className="dh-tc-block dh-tc-heading" data-status={status}>
          {content}
        </div>
      ) : (
        <p className="dh-tc-block" data-status={status}>
          {content}
        </p>
      );
    }
    case 'bulletList':
      return (
        <ul className="dh-tc-block" data-status={status}>
          {children}
        </ul>
      );
    case 'orderedList':
      return (
        <ol className="dh-tc-block" data-status={status}>
          {children}
        </ol>
      );
    case 'listItem':
      return <li data-status={status}>{children}</li>;
    case 'table':
      return (
        <table className="dh-tc-block dh-tc-table" data-status={status}>
          <tbody>
            {(block.rows ?? []).map((row, r) => (
              <tr key={r}>
                {row.map((cell, c) => (
                  <td key={c} data-status={cell.status === 'equal' ? undefined : cell.status}>
                    {cell.blocks.length > 0 ? (
                      cell.blocks.map((b, i) => <Block key={i} block={b} />)
                    ) : (
                      <Ops ops={cell.ops} />
                    )}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      );
    case 'horizontalRule':
    case 'pageBreak':
      return <hr className="dh-tc-block" data-status={status} />;
    default:
      return block.ops ? (
        <p className="dh-tc-block" data-status={status}>
          <Ops ops={block.ops} />
        </p>
      ) : (
        <div data-status={status}>{children}</div>
      );
  }
}

/**
 * Track changes of one section (T15 §4): insertions underlined in the author's colour, deletions struck through,
 * formatting changes dotted-underlined; hover names author, time and source. Display only — never stored.
 */
export function TrackChanges({
  blocks,
  emptyText = 'No text changes since the baseline.',
}: {
  blocks: DiffBlock[];
  emptyText?: string;
}) {
  const changed = blocks.some(function hasChange(b: DiffBlock): boolean {
    return (
      b.status !== 'equal' ||
      (b.ops ?? []).some((o) => o.op !== 'equal') ||
      (b.children ?? []).some(hasChange) ||
      (b.rows ?? []).some((r) => r.some((c) => c.status !== 'equal'))
    );
  });
  return (
    <div className="dh-section-text dh-track-changes" data-testid="track-changes">
      {blocks.map((block, i) => (
        <Block key={i} block={block} />
      ))}
      {!changed && <p className="dh-muted dh-tc-none">{emptyText}</p>}
    </div>
  );
}
