# T09 — Node rich-text content API (Word-like styled content)

| | |
|---|---|
| **Depends on** | T05 (style catalog), T08 |
| **Blocks** | T11, T12, T15 |
| **Size** | L (3–4 days) |
| **Requirements** | FR-T4, FR-V4, NFR-5 |
| **Read first** (nothing else) | [02-document-tree](../requirements/02-document-tree.md) · [03-versioning-and-signing](../requirements/03-versioning-and-signing.md) · [09-non-functional](../requirements/09-non-functional.md) · [content-format](../content-format.md) · [architecture](../architecture.md) (only sections linked in the text) · [process](../process.md) |

## Goal
Read and edit the styled rich-text content (paragraph styles, fonts, spacing, lists, tables with merged cells and borders) of a node.
The canonical format is **TipTap/ProseMirror JSON validated against DocHub Content Schema v1** — read [content-format.md](../content-format.md) first; it is the specification for this task.

## API
| Method | Route | Notes |
|---|---|---|
| GET | `/api/nodes/{nodeId}/content?format=json|html|both` | `{ nodeId, logicalNodeId, schemaVersion, contentJson, contentHtml?, modifiedAt, modifiedBy {id,displayName}, rowVersion }` (default `json`) |
| PUT | `/api/nodes/{nodeId}/content` | `{ contentJson, rowVersion }` → returns the **canonicalized** stored JSON + new `rowVersion`; validation errors → `400` with JSON paths |
| GET | `/api/versions/{versionId}/content?nodeIds=1,2,3&format=html` | batch read for rendering a whole document or a subtree (max 200 ids) |
| GET | `/api/content-schema` | machine-readable schema v1 (allowed nodes/marks/attributes/ranges, font list) — used by the editor and tests |

## Rules
1. `IVersionGuard.EnsureEditable` → `409`, then `IDocumentAuthorization.CanEditContent(docId, logicalNodeId)` → `403` (owner, document-level editor, or node-level editor on this node or an ancestor — T10).
2. **Validation** (`ContentSchemaValidator` in `DocHub.Domain`): node/mark types, attribute whitelists and ranges, `styleId` exists in `app.ContentStyle`, link schemes, depth ≤ 20, size ≤ 2 MB. Unknown `wordExt` attributes are accepted as opaque JSON objects (≤ 16 KB each).
3. **Canonicalization** before save: sorted keys, default-valued attributes removed, empty trailing paragraphs removed, adjacent text nodes with equal marks merged. Empty content = `{"type":"doc","content":[]}`.
4. **Derived columns** (computed server-side on save):
   - `ContentHtml` — rendered by `ContentHtmlRenderer` (JSON → HTML; text always HTML-encoded; styles as CSS classes `ds-style-{styleId}`, direct formatting as inline style from whitelisted values only).
   - `PlainText` — newline between blocks, tab between table cells.
   - `ContentHash` — SHA-256 of canonical JSON.
5. Same `ContentHash` as stored → no DB update (no audit noise), return `200` with current state.
6. The UI autosaves (debounced) — `PUT` is a single `UPDATE … WHERE NodeId = @id AND RowVersion = @rv`.
7. `GET /api/content-styles/stylesheet.css` (T05) is what the renderer's CSS classes refer to.

## DB changes (in T02)
`app.NodeContent`: `ContentJson nvarchar(max)` (`ISJSON` check) + `SchemaVersion tinyint` are the source of truth; `ContentHtml`, `PlainText`, `ContentHash` are derived.

## Acceptance criteria
- [ ] Round-trip fixture set (in `tests/.../ContentFixtures/`): headings with `Heading1–3` styles, justified paragraph with first-line indent and 1.5 line spacing, mixed fonts/sizes/colors/highlight, nested numbered lists, a table with `colspan`/`rowspan`, column widths, borders and cell shading — stored and returned identically (after canonicalization).
- [ ] Invalid inputs → `400` with the JSON path: unknown node type, `color: "red; background:url(…)"`, `href: "javascript:…"`, font size 1000, unknown `styleId`.
- [ ] HTML renderer output for all fixtures matches approved snapshots; text containing `<script>` is rendered escaped.
- [ ] Saving identical content twice creates **one** audit row.
- [ ] Signed version → `409 version-not-editable`; no content permission → `403`; stale `rowVersion` → `409 concurrency-conflict`.
- [ ] `PlainText` of a 2×2 table is `a\tb\nc\td`.
