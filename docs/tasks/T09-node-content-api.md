# T09 — Node rich-text content API (Word-like styled content)

| | |
|---|---|
| **Depends on** | T05 (style catalog), T08 |
| **Blocks** | T11, T12, T15 |
| **Size** | L (3–4 days) |
| **Requirements** | FR-T4, FR-V4, NFR-5 |
| **Read first** (nothing else) | [02-document-tree](../requirements/02-document-tree.md) · [03-versioning-and-signing](../requirements/03-versioning-and-signing.md) · [09-non-functional](../requirements/09-non-functional.md) · [content-format](../content-format.md) · [architecture](../architecture.md) (only sections linked in the text) · [06-permissions](../requirements/06-permissions.md) (FR-P5) · [12-load-and-performance](../requirements/12-load-and-performance.md) (NFR-L9) · [process](../process.md) |

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
2. **Validation** (`ContentSchemaValidator` in `DocHub.Domain`): node/mark types, attribute whitelists and ranges, `styleId` exists in `app.ContentStyle` (active **or inactive** — content that already uses a deactivated style stays saveable; the editor only *offers* active styles), link schemes, depth ≤ 20, size ≤ 2 MB. Unknown `wordExt` attributes are accepted as opaque JSON objects (≤ 16 KB each).
3. **Canonicalization** before save: sorted keys, default-valued attributes removed, empty trailing paragraphs removed, adjacent text nodes with equal marks merged. Empty content = `{"type":"doc","content":[]}`.
4. **Derived columns** (computed server-side on save):
   - `ContentHtml` — rendered by `ContentHtmlRenderer` (JSON → HTML; text always HTML-encoded; styles as CSS classes `ds-style-{styleId}`, direct formatting as inline style from whitelisted values only).
   - `PlainText` — newline between blocks, tab between table cells.
   - `ContentHash` — SHA-256 of canonical JSON.
5. If the new canonical JSON equals the **stored canonical `ContentJson`** (compared by value, not via the stored `ContentHash` column) → no DB update (no audit noise). Otherwise every save recomputes **all** derived columns and the hash.
6. **Caching** (NFR-L9): same `ETag` (`VersionStamp.LastChangeLogId`) / `no-cache` / `HybridCache` scheme as T08; `EnsureCanView` first.
6a. The content `PUT` updates `ContentJson` **and** all derived columns in one statement (so the trigger does not flag `DerivedStale`) and maintains `app.ContentStyleUsage` for the node.
7. The UI autosaves (debounced) — `PUT` is a single `UPDATE … WHERE NodeId = @id AND RowVersion = @rv`.
7a. `GET /api/content-styles/stylesheet.css` (T05) is what the renderer's CSS classes refer to (it includes inactive styles).
8. **Script edits**: support scripts edit `ContentJson` only. The T03 trigger sets `NodeContent.DerivedStale = 1` whenever `ContentJson` changes outside the API content path (`OperationContext <> 'ApiContentSave'`). A hosted service `DerivedContentRefresher` (every 30 s, batches of 500, filtered index on `DerivedStale = 1`), running as the seeded `system` user (label `OperationContext = 'RebuildDerived'`), re-renders `ContentHtml`, `PlainText`, `ContentHash`, rebuilds `ContentStyleUsage` and clears the flag — it changes derived columns only, so by the column-based rule it is not audited and creates no history entry (T03 §2c). This is the **only allowed write to Signed versions** and touches derived columns only — never `ContentJson`, so tamper detection (T07 rule 4) is unaffected. Until refreshed, reads render from `ContentJson` on the fly; search sees the new text within ≤ 1 min.

## DB changes (in T02)
`app.NodeContent`: `ContentJson nvarchar(max)` (`ISJSON` check) + `SchemaVersion tinyint` are the source of truth; `ContentHtml`, `PlainText`, `ContentHash` are derived.

## Acceptance criteria
- [ ] Round-trip fixture set (in `tests/.../ContentFixtures/`): headings with `Heading1–3` styles, justified paragraph with first-line indent and 1.5 line spacing, mixed fonts/sizes/colors/highlight, nested numbered lists, a table with `colspan`/`rowspan`, column widths, borders and cell shading — stored and returned identically (after canonicalization).
- [ ] Invalid inputs → `400` with the JSON path: unknown node type, `color: "red; background:url(…)"`, `href: "javascript:…"`, font size 1000, unknown `styleId`.
- [ ] HTML renderer output for all fixtures matches approved snapshots; text containing `<script>` is rendered escaped.
- [ ] Saving identical content twice creates **one** audit row.
- [ ] Signed version → `409 version-not-editable`; no content permission → `403`; stale `rowVersion` → `409 concurrency-conflict`.
- [ ] `PlainText` of a 2×2 table is `a\tb\nc\td`.
- [ ] Content using a deactivated style can still be saved.
- [ ] After a direct SQL update of `ContentJson` (also in a Signed version), `GET …/content?format=html` immediately returns HTML of the new JSON; within 1 min the refresher persists `ContentHtml`/`PlainText`/`ContentHash`, clears `DerivedStale`, and search finds the new text; the version is still flagged `modifiedAfterSigning`.
- [ ] After such a script edit, an API save of the pre-script content is **not** skipped.
- [ ] A deleted document's content reads (single and batch) → `404` for non-owner/non-admin users, `200` for owner and admin (FR-P5, via `EnsureCanView`).
