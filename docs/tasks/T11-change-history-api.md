# T11 — Change history API (node text & structure)

| | |
|---|---|
| **Depends on** | T03, T09 |
| **Blocks** | T15 (history panel) |
| **Can run in parallel with** | T12 (shares the diff engine — agree on who builds it first, see §3) |
| **Size** | L ([sizing](../process.md#9-sizing-and-agent-effort)) |
| **Requirements** | FR-H1, FR-H3, FR-H4, FR-H5 |
| **Read first** (nothing else) | [04-change-tracking](../requirements/04-change-tracking.md) · [content-format](../content-format.md) · [architecture](../architecture.md) (only sections linked in the text) · [06-permissions](../requirements/06-permissions.md) (FR-P5) · [process](../process.md) |

## Goal
Read `audit.ChangeLog` (written by triggers — T03) and present a human-readable history for nodes and documents, including changes made by support scripts.

## API
| Method | Route | Notes |
|---|---|---|
| GET | `/api/documents/{id}/nodes/{logicalNodeId}/history?page&pageSize` | history of one node **across all versions**, newest first |
| GET | `/api/history/entries/{entryId}/diff` | diff between `OldValues` and `NewValues` of a content entry |
| GET | `/api/history/entries/{entryId}/content` | resolves the entry's document and calls `EnsureCanView` (FR-P5) — the node's content (`contentJson` + `contentHtml`) and header (title, type) **as of** that entry — for "View" and "Restore this text" in the editor (T15) |
| GET | `/api/versions/{versionId}/change-summary?since=latestSigned|v:{versionId}|e:{entryId}|d:{isoDate}` | **one call for all section badges**: `{ nodes: [{ logicalNodeId, changeCount, lastChangedAt, lastChangedBy, hasScriptChange, hasChangeAfterSigning, structural: [{ kind: Added|Moved|Renamed|TypeChanged, oldNumber?, newNumber?, oldTitle?, newTitle?, oldNodeTypeId?, newNodeTypeId? }] }], removed: [{ logicalNodeId, title, number, nodeTypeId, formerParentLogicalNodeId, formerPosition, lastEntryId }] }` — `lastEntryId` feeds `entries/{id}/content` to expand a deleted placeholder |
| GET | `/api/documents/{id}/nodes/{logicalNodeId}/changes?since=latestSigned|v:{versionId}|e:{entryId}|d:{isoDate}&until=current|e:{entryId}` | **attributed diff** for track changes: same block/ops format as `/diff`, each `insert`/`delete`/`format` op carrying `{ entryId, userId, displayName, source, ticket, changedAt }` |
| GET | `/api/documents/{id}/history?versionId?&from?&to?&userId?&source?&page&pageSize` | document-wide activity feed (nodes, content, versions, permissions, comments) |
| GET | `/api/admin/audit?from&to&table&operation&source&userId&dbLogin&ticket&page&pageSize` | **admin only**; `from`/`to` required, range ≤ 31 days (partition elimination; `400` otherwise); indexed filters (T03); raw `audit.ChangeLog` rows (all tables incl. folders, node types, styles, users) for the Admin tab (FR-UI3) |

### History entry shape
```json
{
  "id": 123456,
  "changedAt": "2026-09-25T10:15:00Z",
  "versionId": 42, "versionLabel": "Draft (based on v2)",
  "kind": "ContentChanged",
  "user": { "id": 2, "displayName": "Alice" },
  "source": "App",
  "dbLogin": null, "ticket": null, "reason": null,
  "summary": "Content changed (+12 / −3 words)",
  "changes": [ { "field": "title", "old": "Section 1", "new": "Scope" } ],
  "hasContentDiff": true
}
```
`kind` ∈ `NodeCreated, NodeDeleted, NodeRenamed, NodeTypeChanged, NodeMoved, ContentChanged, CopiedToNewDraft, VersionSigned, VersionCreated, VersionDiscarded, PermissionGranted, PermissionRevoked, CommentAdded, …` — derived from `TableName`, `Operation` and `ChangedColumns`.

For `source = "Script"`, `user` is `null` (or the acting user from `usp_SetSupportContext`) and `dbLogin`, `ticket`, `reason` are filled; the UI highlights these entries.

## Rules
1. **Grouping**: rows with the same `CorrelationId` + entity are merged into one entry (e.g. node title + node type changed in one request).
2. **Draft copies**: inserting nodes/content during "new draft" (T07) must not flood the history — collapse all `I` rows of one copy operation into a single `CopiedToNewDraft` entry per node (detect via `CorrelationId` of the draft creation, or T07 sets session context key `OperationContext = 'CopyVersion'`, stored in `audit.ChangeLog.OperationContext` — use it).
3. `NodeMoved`: `ParentNodeId` or `SortOrder` changed — resolve old/new parent titles for display.
4. **Content diff** (`/diff`): take `OldValues.ContentJson` and `NewValues.ContentJson`, build block trees, word-level diff, and report **formatting-only** changes separately (e.g. `{"op":"format","text":"Scope","changes":["bold added","fontSize 11pt→14pt"]}`, paragraph `styleId Normal→Heading2`); response:
   ```json
   { "blocks": [ { "type": "paragraph", "ops": [ {"op":"equal","text":"The "}, {"op":"delete","text":"old"}, {"op":"insert","text":"new"} ] },
                 { "type": "table", "rows": [[ { "ops": [...] } ]] } ],
     "html": "<p>The <del>old</del><ins>new</ins> …</p>",
     "stats": { "inserted": 12, "deleted": 3 } }
   ```
   `html` is ready to render (generated by the T09 renderer, text escaped).
5. **Tampering flag**: entries that modified a Signed version after `SignedAt` get `"afterSigning": true` (uses `audit.vSignedVersionTampering`, T03).
6. Read access: anyone who can view the document (T10).
7. Performance: history for one node uses the `(LogicalNodeId, ChangedAt)` index; filter by `DocumentId` too.

## 2a. Attributed diff (track changes)
Built by **folding consecutive content states** from `audit.ChangeLog` (baseline → each `ContentChanged` entry → target). Each step is diffed and attributed to that entry's user. Runs are carried forward: text inserted by Alice and later deleted by Bob is shown neither as an insert nor as a delete; text whose formatting Bob changed keeps Alice as the author of the text and records Bob for the format change. The baseline state comes from the baseline version's `ContentJson` (for a version) or from the last entry before the date. The result is cached per `(logicalNodeId, baselineKey, lastEntryId)` (NFR-L9). Budget: p95 ≤ 1.5 s for a node with 200 changes.

## 3. Diff engine (shared with T12)
`IContentDiffService` in `DocHub.Infrastructure`:
- `ContentBlockReader` (Content Schema v1 JSON → `IReadOnlyList<Block>`: paragraph/heading/list-item/table→rows→cells, each with text runs + marks + block attributes);
- block alignment (LCS on block text hashes) + word-level `DiffPlex` inside changed blocks;
- rendering to `ops` JSON and to `<ins>/<del>` HTML (via the T09 `ContentHtmlRenderer`, keeping the original styles visible in the diff).
Unit tests with fixtures in `tests/DocHub.Domain.Tests/DiffFixtures/`. Whoever starts first (T11 or T12) builds it.

## Implementation notes (Q14)
- **Diff engine** (`Infrastructure/Content/Diff`): a tree diff of the content JSON. Children are aligned by LCS, and changed stretches are paired by word similarity (Jaccard ≥ 0.3). Paired text blocks get a DiffPlex word diff. Tables are diffed row by row, then cell by cell. The HTML is the T09 renderer output of a diff document with `diffInsert`/`diffDelete`/`diffFormat` marks and `ds-diff-*` classes (styles in `stylesheet.css`).
- **Track changes** (`AttributedDiff`): folds the states character by character. The ops of changed text blocks come from that fold, so the attribution is exact. The `html` of `/changes` is the plain baseline→final diff.
- **Baselines**: `latestSigned`/`v:` count rows after the baseline's signing in the target and its ancestors after it. `e:`/`d:` rebuild the tree and content as of the point from the log. `/changes` tracks the draft, else the current version.
- **Entry ids**: an entry is the content row of its group if it has one (this is what `/diff` and `/content` take), else its last row.
- **Performance data**: the perf tests use 1 M log rows (weak-environment profile, decisions log Q19) instead of 24 M.

## Acceptance criteria
- [ ] Edit content of a node 3 times in v1-draft, sign, create draft, edit once more → node history shows 4 `ContentChanged` + `VersionSigned` context + 1 `CopiedToNewDraft`, in the correct order, with correct users.
- [ ] Rename + change type in one request → one grouped entry.
- [ ] `change-summary` since v1 returns correct counts for changed, unchanged, added, moved and removed nodes in one call.
- [ ] Attributed diff: Alice inserts a sentence, Bob changes one word in it, and a script fixes a typo. The result attributes each run correctly (Alice / Bob / Script + ticket), and text added and then removed within the range doesn't appear.
- [ ] `entries/{id}/content` returns exactly the historical content and title.
- [ ] For a deleted document, `entries/{id}/content`, `entries/{id}/diff`, `change-summary` and `/changes` → `404` for non-owner/non-admin (FR-P5).
- [ ] `change-summary` returns old/new number, title and type for moved/renamed/retyped nodes and the removed-node shape; `since=e:{entryId}` works for both endpoints.
- [ ] A direct SQL update (no session context) appears with `source = Script` and `dbLogin`; with `usp_SetSupportContext` it shows `ticket` and `reason`.
- [ ] Script edit of a Signed version appears with `afterSigning = true`.
- [ ] Diff of a table where one cell changed marks only that cell.
- [ ] `GET /api/admin/audit` filters by `source=Script` and `ticket`; non-admin → `403`; a 31-day filtered query on 24 M rows returns the first page in < 1 s (tagged perf test).
- [ ] Derived-only rebuilds never appear (not logged, T03 §2c).
- [ ] Making a word bold (no text change) is reported as a formatting change, not as delete+insert.
- [ ] A deleted document's node history, document history and diff → `404` for non-owner/non-admin users, `200` for owner and admin (FR-P5, via `EnsureCanView`).
