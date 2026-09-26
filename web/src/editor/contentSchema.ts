import type { JSONContent } from '@tiptap/core';

/** The parts of `GET /api/content-schema` the editor needs (DocHub Content Schema v1). */
export interface AttributeRule {
  kind: string;
  min: number | null;
  max: number | null;
  values: string[] | null;
  default: unknown;
}

export interface ContentSchemaInfo {
  version: number;
  fontFamilies: string[];
  nodes: Record<string, { attributes: Record<string, AttributeRule>; children: string[] | null }>;
  marks: Record<string, { attributes: Record<string, AttributeRule> }>;
}

/** Stored JSON (Content Schema v1) — plain JSON, as the API sends and accepts it. */
export type SchemaJson = JSONContent;

/** A table column width in the editor (px) ⇄ Word twips (1 px = 15 twips at 96 dpi). */
const TwipsPerPixel = 15;

const emptyDocument: SchemaJson = { type: 'doc', content: [] };

function cleanAttrs(
  attrs: Record<string, unknown> | undefined,
  rules: Record<string, AttributeRule> | undefined,
): Record<string, unknown> | undefined {
  if (!attrs || !rules) {
    return undefined;
  }

  const result: Record<string, unknown> = {};
  for (const [name, value] of Object.entries(attrs)) {
    const rule = rules[name];
    // Only schema attributes; null and default values are left out (the server's canonical form does the same).
    if (rule && value !== null && value !== undefined && value !== rule.default) {
      result[name] = value;
    }
  }

  return Object.keys(result).length > 0 ? result : undefined;
}

/**
 * Editor JSON → Content Schema v1: attributes the schema doesn't know (TipTap's own, e.g. table `colwidth`) are mapped or
 * dropped, nulls and defaults are left out, marks without attributes that need them are dropped. The server validates
 * anyway; this keeps saves valid and comparable with the canonical JSON the server returns.
 */
export function toSchema(node: JSONContent, schema: ContentSchemaInfo): SchemaJson {
  const type = node.type ?? 'doc';
  const rules = schema.nodes[type]?.attributes;
  const attrs: Record<string, unknown> = { ...(node.attrs ?? {}) };
  if ((type === 'tableCell' || type === 'tableHeader') && attrs.width == null && Array.isArray(attrs.colwidth)) {
    const px = (attrs.colwidth as unknown[]).find((w): w is number => typeof w === 'number');
    if (px !== undefined) {
      attrs.width = Math.round(px * TwipsPerPixel);
    }
  }

  const result: SchemaJson = { type };
  const cleaned = cleanAttrs(attrs, rules);
  if (cleaned) {
    result.attrs = cleaned;
  }

  if (type === 'text') {
    result.text = node.text ?? '';
    const marks = (node.marks ?? [])
      .map((mark) => {
        const markRules = schema.marks[mark.type]?.attributes;
        if (!markRules) {
          return null;
        }

        const markAttrs = cleanAttrs(mark.attrs, markRules);
        if (mark.type === 'link' && !/^(https?:|mailto:)/i.test(String(markAttrs?.href ?? ''))) {
          return null; // the schema allows http, https and mailto links only
        }

        // textStyle / highlight / link / charStyle without any value carry nothing.
        if (
          !markAttrs &&
          Object.keys(markRules).length > 0 &&
          ['textStyle', 'highlight', 'link', 'charStyle'].includes(mark.type)
        ) {
          return null;
        }

        return markAttrs ? { type: mark.type, attrs: markAttrs } : { type: mark.type };
      })
      .filter((mark): mark is NonNullable<typeof mark> => mark !== null);
    if (marks.length > 0) {
      result.marks = marks;
    }

    return result;
  }

  if (node.content) {
    const children = node.content
      .filter((child) => child.type !== 'text' || (child.text ?? '') !== '')
      .map((child) => toSchema(child, schema));
    if (children.length > 0 || type === 'doc') {
      result.content = children;
    }
  } else if (type === 'doc') {
    result.content = [];
  }

  // An empty document is `{"type":"doc","content":[]}` — never a lone empty paragraph.
  if (
    type === 'doc' &&
    result.content?.length === 1 &&
    result.content[0]?.type === 'paragraph' &&
    !result.content[0].content &&
    !result.content[0].attrs
  ) {
    result.content = [];
  }

  return result;
}

/** Content Schema v1 → editor JSON (table cell widths become TipTap column widths; an empty document gets one paragraph). */
export function fromSchema(json: unknown): JSONContent {
  const walk = (node: JSONContent): JSONContent => {
    const result: JSONContent = { ...node };
    if ((node.type === 'tableCell' || node.type === 'tableHeader') && typeof node.attrs?.width === 'number') {
      result.attrs = { ...node.attrs, colwidth: [Math.round(node.attrs.width / TwipsPerPixel)] };
    }

    if (node.content) {
      result.content = node.content.map(walk);
    }

    return result;
  };

  const doc = isDocument(json) ? walk(json) : emptyDocument;
  return doc.content && doc.content.length > 0 ? doc : { type: 'doc', content: [{ type: 'paragraph' }] };
}

function isDocument(value: unknown): value is JSONContent {
  return typeof value === 'object' && value !== null && (value as { type?: unknown }).type === 'doc';
}

/** Key-sorted JSON text, for comparing the editor's state with the server's canonical JSON. */
export function stableStringify(value: unknown): string {
  if (Array.isArray(value)) {
    return `[${value.map(stableStringify).join(',')}]`;
  }

  if (value && typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>)
      .filter(([, v]) => v !== undefined)
      .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
    return `{${entries.map(([k, v]) => `${JSON.stringify(k)}:${stableStringify(v)}`).join(',')}}`;
  }

  return JSON.stringify(value);
}

/** Whether two documents are the same content (after the same normalization). */
export function sameContent(a: JSONContent, b: unknown, schema: ContentSchemaInfo): boolean {
  return stableStringify(toSchema(a, schema)) === stableStringify(toSchema(isDocument(b) ? b : emptyDocument, schema));
}
