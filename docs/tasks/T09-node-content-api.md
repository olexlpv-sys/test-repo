# T09 — Node rich-text content API

| | |
|---|---|
| **Depends on** | T08 |
| **Blocks** | T11, T12, T15 |
| **Size** | M (2 days) |
| **Requirements** | FR-T4, FR-V4, NFR-5 |

## Goal
Read and edit the rich-text content (with tables) of a node. Content is **sanitized HTML** ([ADR-05](../architecture.md)).

## API
| Method | Route | Notes |
|---|---|---|
| GET | `/api/nodes/{nodeId}/content` | `{ nodeId, logicalNodeId, contentHtml, modifiedAt, modifiedBy {id,displayName}, rowVersion }` |
| PUT | `/api/nodes/{nodeId}/content` | `{ contentHtml, rowVersion }` → returns the **sanitized** stored content + new `rowVersion` |
| GET | `/api/versions/{versionId}/content?nodeIds=1,2,3` | batch read for rendering a whole document or a subtree (max 200 ids) |

## Rules
1. `IVersionGuard.EnsureEditable` → `409`, then `IDocumentAuthorization.CanEditContent(docId, logicalNodeId)` → `403` (owner, document-level editor, or node-level editor on this node or an ancestor — T10).
2. **Sanitization** with `Ganss.Xss.HtmlSanitizer`, allow-list:
   - blocks: `p, h1–h6, ul, ol, li, blockquote, pre, code, hr, br`
   - inline: `strong, b, em, i, u, s, sub, sup, span, a[href]` (only `http`, `https`, `mailto`; add `rel="noopener noreferrer"`)
   - tables: `table, thead, tbody, tfoot, tr, th, td, colgroup, col` with attributes `colspan, rowspan, colwidth` (TipTap), `style` limited to `text-align`, `width`
   - everything else (scripts, event handlers, `iframe`, `img` for now, inline styles beyond the list) is stripped.
   Sanitizer configuration lives in `DocHub.Infrastructure` and is covered by unit tests (XSS payload list).
3. Normalization before save: trim, collapse empty `<p></p>` at the end; empty editor content is stored as empty string.
4. Derive and store `PlainText` (text extraction with block separators: newline between blocks, tab between table cells) and `ContentHash` (SHA-256 of normalized HTML).
5. If the new `ContentHash` equals the current one → no DB update (no audit noise), return `200` with current state.
6. Max size of `contentHtml`: 1 MB (configurable) → `413`/`400`.
7. The UI will save frequently (debounced autosave) — make `PUT` cheap: single `UPDATE … WHERE NodeId = @id AND RowVersion = @rv`.

## Acceptance criteria
- [ ] Round-trip of a HTML fragment with a table containing `colspan/rowspan`, lists, headings, links — returned intact.
- [ ] `<script>`, `onerror=`, `javascript:` links, `<iframe>` are removed (unit tests with an OWASP-style payload list).
- [ ] Saving identical content twice creates **one** audit row, not two.
- [ ] Editing content of a Signed version → `409 version-not-editable`; without edit permission → `403`.
- [ ] Stale `rowVersion` → `409 concurrency-conflict`.
- [ ] `PlainText` of a table is `cell1\tcell2\ncell3\tcell4`.
