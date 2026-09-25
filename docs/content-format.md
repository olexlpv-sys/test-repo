# Rich-text content format (analysis & decision)

**Goal:** node text must carry styles that reproduce a Word document as closely as possible, and it should be possible
to export to / import from Word (`.docx`) later without losing formatting.

## 1. Options

| Criterion | A. Sanitized HTML | B. Structured JSON (ProseMirror / TipTap schema) — **chosen** | C. Word OOXML fragments (`w:p`, `w:tbl`) per node |
|---|---|---|---|
| Editing in the browser | native for the editor | native for TipTap (its internal model) | ✗ no browser editor edits OOXML; needs OOXML⇄HTML on every load/save (lossy both ways) |
| Word fidelity (styles, fonts, spacing, borders, merged cells) | medium — inline `style` must be allowed and parsed, ambiguous | high — every Word property we support is an explicit, typed attribute | highest in theory, but only what the editor can show survives round-trips anyway |
| Security | needs an HTML sanitizer, CSS parsing is a large attack surface | strict schema validation (allowed node/mark types, value whitelists); HTML is **generated** by us, text is always escaped | XML validation; rendering still requires conversion |
| Diff / compare (T11, T12) | parse HTML → blocks, attribute noise | direct block tree, can distinguish *text* vs *formatting* changes | parse XML, heavy |
| Export to `.docx` | HTML→OOXML converters are lossy | deterministic mapping JSON → Open XML SDK elements | trivial |
| Import from `.docx` | via converters | mapping OOXML → JSON; unknown properties kept in a passthrough attribute | trivial |
| Deterministic hash (signing, tampering) | fragile (attribute order, whitespace) | canonical JSON (sorted keys) | canonical XML — complex |
| DB size / audit volume | small | ~1.5–2× HTML | 3–5× HTML |

**Decision:** store **B — TipTap/ProseMirror JSON as the canonical format**, validated against our own versioned schema
(“DocHub Content Schema v1”) whose attributes are **modelled on WordprocessingML** so the mapping to `.docx` is 1:1.
Derived columns: `ContentHtml` (server-rendered, for display/diff rendering), `PlainText` (search, word counts), `ContentHash`.
Storing Word itself (option C) is **not** used as the primary format; when `.docx` import is added later,
unmapped Word properties are preserved in a `wordExt` passthrough attribute on the element so export can put them back.

## 2. Content Schema v1

### Document-level styles (Word "styles.xml" equivalent)
A system-wide **style catalog** `app.ContentStyle` (admin-editable, seeded) — paragraph, character and table styles
with Word-compatible ids: `Normal`, `Heading1`…`Heading6`, `Title`, `Subtitle`, `Quote`, `ListParagraph`, `Caption`,
`TableGrid`, `Emphasis`, `Strong`. Each style defines font family, size, color, bold/italic, alignment, spacing before/after,
line spacing, indents, and (for tables) borders/shading. Nodes reference styles by id; direct formatting overrides them — exactly like Word.

### Block nodes
| Node | Attributes (Word equivalent) |
|---|---|
| `paragraph` | `styleId` (`w:pStyle`), `align` left/center/right/justify (`w:jc`), `indentLeft`, `indentRight`, `indentFirstLine`/`hanging` in twips (`w:ind`), `spacingBefore`, `spacingAfter` (twips), `lineSpacing` (240 = single) + `lineRule` auto/exact/atLeast (`w:spacing`), `keepWithNext`, `pageBreakBefore` |
| `heading` | `level` 1–6 → `styleId = HeadingN` + the paragraph attributes |
| `bulletList`, `orderedList` | `listStyle` (decimal, lowerLetter, upperRoman, bullet, …), `start`, `level` (Word numbering `w:numPr`) |
| `listItem` | — (contains paragraphs) |
| `table` | `styleId`, `width` + `widthType` (dxa/pct), `align`, `borders` (top/left/bottom/right/insideH/insideV: style, size, color), `columnWidths[]` (`w:tblGrid`), `layout` fixed/auto |
| `tableRow` | `height`, `heightRule`, `isHeader` (`w:tblHeader`), `cantSplit` |
| `tableCell` / `tableHeader` | `colspan` (`w:gridSpan`), `rowspan` (`w:vMerge`), `width`, `verticalAlign`, `shading` (fill color), `borders` |
| `hardBreak`, `pageBreak`, `horizontalRule` | — |
| `image` | *reserved for a later task (attachments)* |

### Marks (character formatting, `w:rPr`)
`bold`, `italic`, `underline` (+`style` single/double/dotted), `strike`, `subscript`, `superscript`,
`textStyle` { `fontFamily`, `fontSize` (half-points like Word, e.g. 24 = 12pt), `color` (#RRGGBB) }, `highlight` { `color` },
`smallCaps`, `allCaps`, `link` { `href` (http/https/mailto only) }, `charStyle` { `styleId` }.

### Validation rules (server, on every save)
- Only the node and mark types above; unknown type → `400` with the JSON path.
- Value whitelists/ranges: colors `^#[0-9A-Fa-f]{6}$`; font family from the configured list; font size 2–400 half-points; twips 0–31680; `styleId` must exist in `app.ContentStyle`; `href` scheme whitelist.
- Max nesting depth 20, max size 2 MB JSON.
- `schemaVersion` stored per row (`1`); upgrades are done by migration code, never silently.

### Rendering & canonicalization
- `ContentHtml` is produced by a **server renderer** (JSON → HTML with CSS classes for styles + inline styles for direct formatting). Text nodes are HTML-encoded, so no sanitizer is needed for output; the editor never sends HTML.
- `ContentHash` = SHA-256 of **canonical JSON** (keys sorted, default-valued attributes removed, adjacent text nodes with equal marks merged).

### Editor (T15)
TipTap extensions: StarterKit, Underline, Subscript, Superscript, TextStyle, FontFamily, Color, Highlight, TextAlign, Link,
Table/TableRow/TableHeader/TableCell, plus **small custom extensions**: FontSize, LineSpacing, ParagraphSpacing, Indent,
ParagraphStyle (`styleId`), TableCellShading/Borders, PageBreak. Style catalog CSS is generated from `app.ContentStyle`
so the editor looks like the Word output.

## 3. Export
- **PDF** (in scope, [T20](tasks/T20-pdf-export.md)): the server HTML renderer + style catalog CSS + print stylesheet → headless Chromium. The editor, the HTML and the PDF therefore share one rendering path.

## 3a. Future: Word export / import (backlog)
- **Export**: `GET /api/versions/{id}/export.docx` — Open XML SDK: styles from `app.ContentStyle` → `styles.xml`; tree nodes → headings with the node title (heading level = tree depth, or per node type) followed by the node's content blocks; numbering → `numbering.xml`.
- **Import**: upload `.docx` → build the tree from heading levels, content from paragraphs/tables between headings; unknown properties → `wordExt`.
