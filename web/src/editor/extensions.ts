import { Extension, Mark, Node, mergeAttributes, type Extensions } from '@tiptap/core';
import StarterKit from '@tiptap/starter-kit';
import { TextStyle } from '@tiptap/extension-text-style';
import Highlight from '@tiptap/extension-highlight';
import Subscript from '@tiptap/extension-subscript';
import Superscript from '@tiptap/extension-superscript';
import { Table, TableCell, TableHeader, TableRow } from '@tiptap/extension-table';
import { ListItem } from '@tiptap/extension-list';
import type { ContentSchemaInfo } from './contentSchema';

/** Word units: twips (1/20 pt) for spacing and indents, half-points for font sizes (content-format.md §2). */
export const pt = (twips: number) => `${twips / 20}pt`;

/** A CSS length (pt, px, in, cm) in twips, or null. */
export function toTwips(css: string | null | undefined): number | null {
  const match = css?.trim().match(/^(-?[\d.]+)(pt|px|in|cm|mm)?$/);
  if (!match?.[1]) {
    return null;
  }

  const value = Number(match[1]);
  const factor = { pt: 20, px: 15, in: 1440, cm: 567, mm: 56.7 }[match[2] ?? 'px'] ?? 15;
  const twips = Math.round(value * factor);
  return Number.isFinite(twips) && twips >= 0 && twips <= 31680 ? twips : null;
}

/** A CSS color as #RRGGBB, or null (the schema accepts only that form). */
export function toHexColor(css: string | null | undefined): string | null {
  const value = css?.trim().toLowerCase();
  if (!value || value === 'transparent' || value === 'inherit' || value === 'auto' || value === 'windowtext') {
    return value === 'windowtext' ? '#000000' : null;
  }

  if (/^#[0-9a-f]{6}$/.test(value)) {
    return value.toUpperCase();
  }

  if (/^#[0-9a-f]{3}$/.test(value)) {
    return `#${[...value.slice(1)].map((c) => c + c).join('')}`.toUpperCase();
  }

  const rgb = value.match(/^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)/);
  if (rgb) {
    return `#${rgb
      .slice(1, 4)
      .map((c) => Number(c).toString(16).padStart(2, '0'))
      .join('')}`.toUpperCase();
  }

  const named: Record<string, string> = {
    black: '#000000',
    white: '#FFFFFF',
    red: '#FF0000',
    green: '#008000',
    blue: '#0000FF',
    yellow: '#FFFF00',
    gray: '#808080',
    grey: '#808080',
    navy: '#000080',
    maroon: '#800000',
    purple: '#800080',
    teal: '#008080',
    silver: '#C0C0C0',
    orange: '#FFA500',
  };
  return named[value] ?? null;
}

/** Word's paste classes (`MsoTitle`, …) and our own `ds-style-*` classes → a catalog style id. */
function styleIdFrom(element: HTMLElement, styleIds: Set<string>): string | null {
  for (const cls of Array.from(element.classList)) {
    const id = cls.startsWith('ds-style-')
      ? cls.slice('ds-style-'.length)
      : cls.startsWith('Mso')
        ? cls.slice(3)
        : null;
    if (id && id !== 'Normal' && styleIds.has(id)) {
      return id;
    }
  }

  return null;
}

interface Options {
  schema: ContentSchemaInfo;
  styleIds: Set<string>;
}

/** Paragraph/heading attributes (`w:pPr`): same names as the schema, rendered as inline CSS like the server renderer. */
const ParagraphFormat = Extension.create<Options>({
  name: 'paragraphFormat',
  addOptions: () => ({ schema: { version: 1, fontFamilies: [], nodes: {}, marks: {} }, styleIds: new Set<string>() }),
  addGlobalAttributes() {
    const styleIds = this.options.styleIds;
    const length = (css: string) => ({
      default: null,
      parseHTML: (element: HTMLElement) => toTwips(element.style.getPropertyValue(css)),
    });
    return [
      {
        types: ['paragraph', 'heading'],
        attributes: {
          styleId: {
            default: null,
            parseHTML: (element) => {
              const id = styleIdFrom(element, styleIds);
              // <hN class="ds-style-HeadingN"> is the renderer's fallback for a heading without a style, not a style.
              return /^H[1-6]$/.test(element.tagName) && id === `Heading${element.tagName.slice(1)}` ? null : id;
            },
            // Like the server renderer: a heading without its own style uses HeadingN of the catalog.
            renderHTML: (attrs) =>
              attrs.styleId
                ? { class: `ds-style-${String(attrs.styleId)}` }
                : typeof attrs.level === 'number'
                  ? { class: `ds-style-Heading${attrs.level}` }
                  : {},
          },
          align: {
            default: null,
            parseHTML: (element) =>
              ['left', 'center', 'right', 'justify'].includes(element.style.textAlign) ? element.style.textAlign : null,
            renderHTML: (attrs) => (attrs.align ? { style: `text-align:${String(attrs.align)}` } : {}),
          },
          indentLeft: {
            ...length('margin-left'),
            renderHTML: (a) => (typeof a.indentLeft === 'number' ? { style: `margin-left:${pt(a.indentLeft)}` } : {}),
          },
          indentRight: {
            ...length('margin-right'),
            renderHTML: (a) =>
              typeof a.indentRight === 'number' ? { style: `margin-right:${pt(a.indentRight)}` } : {},
          },
          indentFirstLine: {
            default: null,
            parseHTML: (element) => {
              const twips = toTwips(element.style.textIndent);
              return twips !== null && !element.style.textIndent.startsWith('-') ? twips : null;
            },
            renderHTML: (a) =>
              typeof a.indentFirstLine === 'number' && !a.hanging
                ? { style: `text-indent:${pt(a.indentFirstLine)}` }
                : {},
          },
          hanging: {
            default: null,
            parseHTML: (element) =>
              element.style.textIndent.startsWith('-') ? toTwips(element.style.textIndent.slice(1)) : null,
            renderHTML: (a) => (typeof a.hanging === 'number' ? { style: `text-indent:-${pt(a.hanging)}` } : {}),
          },
          spacingBefore: {
            ...length('margin-top'),
            renderHTML: (a) =>
              typeof a.spacingBefore === 'number' ? { style: `margin-top:${pt(a.spacingBefore)}` } : {},
          },
          spacingAfter: {
            ...length('margin-bottom'),
            renderHTML: (a) =>
              typeof a.spacingAfter === 'number' ? { style: `margin-bottom:${pt(a.spacingAfter)}` } : {},
          },
          lineSpacing: {
            default: null,
            parseHTML: (element) => {
              const value = element.style.lineHeight;
              if (!value || value === 'normal') {
                return null;
              }

              // A unitless or % line height is a multiple of single (240); a length is exact.
              if (/^[\d.]+$/.test(value)) {
                return Math.round(Number(value) * 240);
              }

              return value.endsWith('%') ? Math.round((Number(value.slice(0, -1)) / 100) * 240) : toTwips(value);
            },
            renderHTML: (a) =>
              typeof a.lineSpacing === 'number'
                ? {
                    style: `line-height:${a.lineRule === 'exact' || a.lineRule === 'atLeast' ? pt(a.lineSpacing) : String(a.lineSpacing / 240)}`,
                  }
                : {},
          },
          lineRule: {
            default: null,
            parseHTML: (element) =>
              element.style.lineHeight &&
              !/^[\d.]+%?$/.test(element.style.lineHeight) &&
              element.style.lineHeight !== 'normal'
                ? 'exact'
                : null,
            renderHTML: () => ({}),
          },
          keepWithNext: { default: false, parseHTML: () => false, renderHTML: () => ({}) },
          pageBreakBefore: {
            default: false,
            parseHTML: (element) => element.style.breakBefore === 'page' || element.style.pageBreakBefore === 'always',
            renderHTML: (a) => (a.pageBreakBefore ? { style: 'break-before:page' } : {}),
          },
          wordExt: { default: null, rendered: false },
        },
      },
      {
        types: ['bulletList', 'orderedList'],
        attributes: {
          listStyle: {
            default: null,
            renderHTML: (a) =>
              a.listStyle
                ? { 'data-list-style': String(a.listStyle), style: `list-style-type:${listCss(String(a.listStyle))}` }
                : {},
            parseHTML: (element) => element.getAttribute('data-list-style'),
          },
          level: { default: 0, rendered: false },
          wordExt: { default: null, rendered: false },
        },
      },
      { types: ['listItem'], attributes: { wordExt: { default: null, rendered: false } } },
      {
        types: ['table'],
        attributes: {
          styleId: { default: null, renderHTML: (a) => (a.styleId ? { class: `ds-style-${String(a.styleId)}` } : {}) },
          width: { default: null, rendered: false },
          widthType: { default: null, rendered: false },
          align: { default: null, rendered: false },
          borders: { default: null, rendered: false },
          columnWidths: { default: null, rendered: false },
          layout: { default: null, rendered: false },
          wordExt: { default: null, rendered: false },
        },
      },
      {
        types: ['tableRow'],
        attributes: {
          height: {
            default: null,
            renderHTML: (a) => (typeof a.height === 'number' ? { style: `height:${pt(a.height)}` } : {}),
          },
          heightRule: { default: null, rendered: false },
          isHeader: { default: false, rendered: false },
          cantSplit: { default: false, rendered: false },
          wordExt: { default: null, rendered: false },
        },
      },
      {
        types: ['tableCell', 'tableHeader'],
        attributes: {
          width: { default: null, rendered: false },
          verticalAlign: {
            default: null,
            parseHTML: (element) =>
              ({ top: 'top', middle: 'center', bottom: 'bottom' })[element.style.verticalAlign] ?? null,
            renderHTML: (a) =>
              a.verticalAlign
                ? { style: `vertical-align:${a.verticalAlign === 'center' ? 'middle' : String(a.verticalAlign)}` }
                : {},
          },
          shading: {
            default: null,
            parseHTML: (element) => toHexColor(element.style.backgroundColor),
            renderHTML: (a) => (a.shading ? { style: `background-color:${String(a.shading)}` } : {}),
          },
          borders: {
            default: null,
            renderHTML: (a) => bordersCss(a.borders),
          },
          wordExt: { default: null, rendered: false },
        },
      },
    ];
  },
});

function listCss(listStyle: string): string {
  return (
    {
      bullet: 'disc',
      circle: 'circle',
      square: 'square',
      decimal: 'decimal',
      lowerLetter: 'lower-alpha',
      upperLetter: 'upper-alpha',
      lowerRoman: 'lower-roman',
      upperRoman: 'upper-roman',
    }[listStyle] ?? 'disc'
  );
}

function bordersCss(borders: unknown): Record<string, string> {
  if (!borders || typeof borders !== 'object') {
    return {};
  }

  const css = Object.entries(borders as Record<string, { style?: string; size?: number; color?: string }>)
    .filter(([side, b]) => ['top', 'left', 'bottom', 'right'].includes(side) && b.style && b.style !== 'none')
    .map(
      ([side, b]) =>
        `border-${side}:${Math.max(1, Math.round(((b.size ?? 4) / 8) * 1.333))}px ${b.style === 'thick' || b.style === 'single' ? 'solid' : String(b.style)} ${b.color ?? '#000000'}`,
    )
    .join(';');
  return css ? { style: css } : {};
}

/** Character formatting on `textStyle` (`w:rPr`): font family (catalog list), size in half-points, color #RRGGBB. */
const CharacterFormat = Extension.create<Options>({
  name: 'characterFormat',
  addOptions: () => ({ schema: { version: 1, fontFamilies: [], nodes: {}, marks: {} }, styleIds: new Set<string>() }),
  addGlobalAttributes() {
    const families = this.options.schema.fontFamilies;
    return [
      {
        types: ['textStyle'],
        attributes: {
          fontFamily: {
            default: null,
            parseHTML: (element) => {
              const first = element.style.fontFamily
                .split(',')[0]
                ?.trim()
                .replace(/^["']|["']$/g, '')
                .toLowerCase();
              return families.find((f) => f.toLowerCase() === first) ?? null;
            },
            renderHTML: (a) => (a.fontFamily ? { style: `font-family:"${String(a.fontFamily)}"` } : {}),
          },
          fontSize: {
            default: null,
            parseHTML: (element) => {
              const twips = toTwips(element.style.fontSize);
              const halfPoints = twips === null ? null : Math.round(twips / 10);
              return halfPoints !== null && halfPoints >= 2 && halfPoints <= 400 ? halfPoints : null;
            },
            renderHTML: (a) => (typeof a.fontSize === 'number' ? { style: `font-size:${a.fontSize / 2}pt` } : {}),
          },
          color: {
            default: null,
            parseHTML: (element) => toHexColor(element.style.color),
            renderHTML: (a) => (a.color ? { style: `color:${String(a.color)}` } : {}),
          },
        },
      },
      {
        types: ['highlight'],
        attributes: {
          color: {
            default: null,
            parseHTML: (element) => toHexColor(element.getAttribute('data-color') ?? element.style.backgroundColor),
            renderHTML: (a) =>
              a.color ? { 'data-color': String(a.color), style: `background-color:${String(a.color)}` } : {},
          },
        },
      },
      {
        types: ['underline'],
        attributes: {
          style: {
            default: null,
            parseHTML: (element) =>
              element.style.textDecorationStyle === 'double'
                ? 'double'
                : element.style.textDecorationStyle === 'dotted'
                  ? 'dotted'
                  : null,
            renderHTML: (a) =>
              a.style && a.style !== 'single' ? { style: `text-decoration-style:${String(a.style)}` } : {},
          },
        },
      },
    ];
  },
});

/** Hard page break (`w:br w:type="page"`). */
export const PageBreak = Node.create({
  name: 'pageBreak',
  group: 'block',
  atom: true,
  selectable: true,
  parseHTML: () => [{ tag: 'div.ds-page-break' }, { tag: 'br[style*="page-break-before"]' }],
  renderHTML: () => ['div', { class: 'ds-page-break', 'data-label': 'Page break' }],
  addCommands() {
    return {
      setPageBreak:
        () =>
        ({ commands }) =>
          commands.insertContent({ type: this.name }),
    };
  },
});

declare module '@tiptap/core' {
  interface Commands<ReturnType> {
    pageBreak: { setPageBreak: () => ReturnType };
  }
}

const SmallCaps = Mark.create({
  name: 'smallCaps',
  parseHTML: () => [{ style: 'font-variant=small-caps' }],
  renderHTML: ({ HTMLAttributes }) => [
    'span',
    mergeAttributes(HTMLAttributes, { style: 'font-variant:small-caps' }),
    0,
  ],
});

const AllCaps = Mark.create({
  name: 'allCaps',
  parseHTML: () => [{ style: 'text-transform=uppercase' }],
  renderHTML: ({ HTMLAttributes }) => [
    'span',
    mergeAttributes(HTMLAttributes, { style: 'text-transform:uppercase' }),
    0,
  ],
});

const CharStyle = Mark.create<Options>({
  name: 'charStyle',
  addOptions: () => ({ schema: { version: 1, fontFamilies: [], nodes: {}, marks: {} }, styleIds: new Set<string>() }),
  addAttributes() {
    return { styleId: { default: null } };
  },
  parseHTML() {
    const styleIds = this.options.styleIds;
    return [
      {
        tag: 'span[class*="ds-style-"]',
        getAttrs: (element) => {
          const id = styleIdFrom(element, styleIds);
          return id ? { styleId: id } : false;
        },
      },
    ];
  },
  renderHTML: ({ HTMLAttributes }) => [
    'span',
    { class: HTMLAttributes.styleId ? `ds-style-${String(HTMLAttributes.styleId)}` : undefined },
    0,
  ],
});

/** Every extension of the section editor (content-format.md §2 "Editor"), configured from the schema and the style catalog. */
export function createExtensions(schema: ContentSchemaInfo, styleIds: Set<string>): Extensions {
  const options = { schema, styleIds };
  return [
    StarterKit.configure({
      code: false,
      codeBlock: false,
      blockquote: false,
      heading: { levels: [1, 2, 3, 4, 5, 6] },
      link: { openOnClick: false, autolink: true, protocols: ['http', 'https', 'mailto'], defaultProtocol: 'https' },
      trailingNode: false,
      listItem: false, // replaced below: Content Schema v1 allows fewer children
    }),
    // Allowed children exactly as in Content Schema v1 (no tables, rules or page breaks in cells and list items).
    ListItem.extend({ content: '(paragraph|heading) (paragraph|heading|bulletList|orderedList)*' }),
    TextStyle,
    Highlight.configure({ multicolor: true }),
    Subscript,
    Superscript,
    Table.configure({ resizable: true, allowTableNodeSelection: true }),
    TableRow,
    TableHeader.extend({ content: '(paragraph|heading|bulletList|orderedList)+' }),
    TableCell.extend({ content: '(paragraph|heading|bulletList|orderedList)+' }),
    PageBreak,
    SmallCaps,
    AllCaps,
    CharStyle.configure(options),
    ParagraphFormat.configure(options),
    CharacterFormat.configure(options),
  ];
}
