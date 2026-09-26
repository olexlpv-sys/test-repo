import { describe, expect, it } from 'vitest';
import { fromSchema, sameContent, stableStringify, toSchema, type ContentSchemaInfo } from './contentSchema';
import { toHexColor, toTwips } from './extensions';

// The relevant slice of GET /api/content-schema (Content Schema v1).
const rule = (kind: string, def: unknown = null) => ({ kind, min: null, max: null, values: null, default: def });
const schema: ContentSchemaInfo = {
  version: 1,
  fontFamilies: ['Arial', 'Calibri'],
  nodes: {
    doc: { attributes: {}, children: ['paragraph', 'heading', 'table', 'orderedList'] },
    paragraph: {
      attributes: {
        styleId: rule('StyleId'),
        align: rule('Enum'),
        lineSpacing: rule('WholeNumber'),
        keepWithNext: rule('Boolean', false),
      },
      children: ['text'],
    },
    heading: { attributes: { level: rule('WholeNumber'), align: rule('Enum') }, children: ['text'] },
    orderedList: { attributes: { start: rule('WholeNumber', 1), listStyle: rule('Enum') }, children: ['listItem'] },
    listItem: { attributes: {}, children: ['paragraph'] },
    table: { attributes: { styleId: rule('StyleId') }, children: ['tableRow'] },
    tableRow: { attributes: {}, children: ['tableCell'] },
    tableCell: {
      attributes: {
        colspan: rule('WholeNumber', 1),
        rowspan: rule('WholeNumber', 1),
        width: rule('WholeNumber'),
        shading: rule('Color'),
      },
      children: ['paragraph'],
    },
    text: { attributes: {}, children: null },
  },
  marks: {
    bold: { attributes: {} },
    textStyle: { attributes: { fontFamily: rule('FontFamily'), fontSize: rule('WholeNumber'), color: rule('Color') } },
    link: { attributes: { href: rule('Href') } },
  },
};

describe('Content Schema v1 converter', () => {
  it('keeps only schema attributes, without nulls and defaults, and maps table column widths to twips', () => {
    const editor = {
      type: 'doc',
      content: [
        {
          type: 'paragraph',
          attrs: { styleId: null, align: 'center', textAlign: 'center', keepWithNext: false, lineSpacing: 360 },
          content: [{ type: 'text', text: 'Hi' }],
        },
        {
          type: 'orderedList',
          attrs: { start: 1, type: null, listStyle: null },
          content: [{ type: 'listItem', content: [{ type: 'paragraph', attrs: {} }] }],
        },
        {
          type: 'table',
          content: [
            {
              type: 'tableRow',
              content: [
                {
                  type: 'tableCell',
                  attrs: { colspan: 2, rowspan: 1, colwidth: [100], shading: '#FFF2CC' },
                  content: [{ type: 'paragraph' }],
                },
              ],
            },
          ],
        },
      ],
    };

    expect(toSchema(editor, schema)).toEqual({
      type: 'doc',
      content: [
        { type: 'paragraph', attrs: { align: 'center', lineSpacing: 360 }, content: [{ type: 'text', text: 'Hi' }] },
        { type: 'orderedList', content: [{ type: 'listItem', content: [{ type: 'paragraph' }] }] },
        {
          type: 'table',
          content: [
            {
              type: 'tableRow',
              content: [
                {
                  type: 'tableCell',
                  attrs: { colspan: 2, width: 1500, shading: '#FFF2CC' },
                  content: [{ type: 'paragraph' }],
                },
              ],
            },
          ],
        },
      ],
    });
  });

  it('drops empty marks, unknown marks and links the schema does not allow', () => {
    const editor = {
      type: 'doc',
      content: [
        {
          type: 'paragraph',
          content: [
            {
              type: 'text',
              text: 'a',
              marks: [
                { type: 'textStyle', attrs: { fontFamily: null, fontSize: null, color: null } },
                { type: 'bold' },
              ],
            },
            {
              type: 'text',
              text: 'b',
              marks: [{ type: 'link', attrs: { href: 'javascript:alert(1)', target: '_blank' } }, { type: 'code' }],
            },
            {
              type: 'text',
              text: 'c',
              marks: [{ type: 'link', attrs: { href: 'https://example.com', target: '_blank', rel: 'noopener' } }],
            },
          ],
        },
      ],
    };

    expect(toSchema(editor, schema).content?.[0]?.content).toEqual([
      { type: 'text', text: 'a', marks: [{ type: 'bold' }] },
      { type: 'text', text: 'b' },
      { type: 'text', text: 'c', marks: [{ type: 'link', attrs: { href: 'https://example.com' } }] },
    ]);
  });

  it('stores an empty editor as the canonical empty document and opens it with one paragraph', () => {
    expect(toSchema({ type: 'doc', content: [{ type: 'paragraph' }] }, schema)).toEqual({ type: 'doc', content: [] });
    expect(fromSchema({ type: 'doc', content: [] })).toEqual({ type: 'doc', content: [{ type: 'paragraph' }] });
    expect(fromSchema(null)).toEqual({ type: 'doc', content: [{ type: 'paragraph' }] });
  });

  it('opens table cell widths as editor column widths and compares content independent of key order', () => {
    const stored = {
      type: 'doc',
      content: [
        {
          type: 'table',
          content: [
            {
              type: 'tableRow',
              content: [{ type: 'tableCell', attrs: { width: 1500 }, content: [{ type: 'paragraph' }] }],
            },
          ],
        },
      ],
    };
    const opened = fromSchema(stored);
    expect(opened.content?.[0]?.content?.[0]?.content?.[0]?.attrs).toEqual({ width: 1500, colwidth: [100] });
    expect(sameContent(opened, stored, schema)).toBe(true);
    expect(stableStringify({ b: 1, a: [2, { d: 3, c: 4 }] })).toBe('{"a":[2,{"c":4,"d":3}],"b":1}');
  });
});

describe('Word paste mapping', () => {
  it('converts CSS lengths to twips within the schema range', () => {
    expect(toTwips('36pt')).toBe(720);
    expect(toTwips('1in')).toBe(1440);
    expect(toTwips('48px')).toBe(720);
    expect(toTwips('-5pt')).toBeNull();
    expect(toTwips('auto')).toBeNull();
    expect(toTwips('100in')).toBeNull();
  });

  it('converts CSS colors to #RRGGBB or drops them', () => {
    expect(toHexColor('rgb(255, 0, 0)')).toBe('#FF0000');
    expect(toHexColor('#abc')).toBe('#AABBCC');
    expect(toHexColor('#1e5bdc')).toBe('#1E5BDC');
    expect(toHexColor('windowtext')).toBe('#000000');
    expect(toHexColor('red')).toBe('#FF0000');
    expect(toHexColor('transparent')).toBeNull();
    expect(toHexColor('hsl(10, 20%, 30%)')).toBeNull();
  });
});
