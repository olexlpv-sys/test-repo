# T08 — Document tree (hierarchy) API

| | |
|---|---|
| **Depends on** | T05, T07 |
| **Blocks** | T09, T11, T12, T15 |
| **Size** | M ([sizing](../process.md#9-sizing-and-agent-effort)) |
| **Requirements** | FR-T1 … FR-T3, FR-T5, FR-V4, NFR-6, FR-D3 |
| **Read first** (nothing else) | [02-document-tree](../requirements/02-document-tree.md) · [03-versioning-and-signing](../requirements/03-versioning-and-signing.md) · [09-non-functional](../requirements/09-non-functional.md) · [architecture](../architecture.md) (only sections linked in the text) · [11-data-access](../requirements/11-data-access.md) · [06-permissions](../requirements/06-permissions.md) (FR-P5) · [12-load-and-performance](../requirements/12-load-and-performance.md) (NFR-L9) · [process](../process.md) |

## Goal
Build and edit the node tree of a **draft** version: arbitrary depth, arbitrary titles, typed nodes, ordering and moving.
**The structure is defined by the document Owner only** — Editors change node text (T09), never the tree.

## API
| Method | Route | Notes |
|---|---|---|
| GET | `/api/versions/{versionId}/tree` | full nested tree: `[{ id, logicalNodeId, nodeTypeId, title, number: "1.2.1", hasContent, sortOrder, rowVersion, children: [...] }]`; works for any version status |
| GET | `/api/nodes/{nodeId}` | node header + `path` (ancestors) |
| POST | `/api/versions/{versionId}/nodes` | `{ parentNodeId?, nodeTypeId, title, position? }` → `201`; `position` = 0-based index among siblings, default last; new `LogicalNodeId = NEWID()`; creates an empty `NodeContent` |
| PATCH | `/api/nodes/{nodeId}` | `{ title?, nodeTypeId?, rowVersion }` |
| POST | `/api/nodes/{nodeId}/move` | `{ newParentNodeId?, position, rowVersion }` — re-parent and/or reorder within the same version |
| DELETE | `/api/nodes/{nodeId}?rowVersion=…` | deletes node **with its whole subtree** and contents; response `{ deletedCount }` |
| POST | `/api/versions/{versionId}/nodes/bulk` | *(optional, nice to have)* create a whole subtree in one call: nested `[{ nodeTypeId, title, contentJson?, children }]` — `contentJson` validated/canonicalized exactly like T09, derived columns and `ContentStyleUsage` written in the same way (never HTML) — useful for templates, seeding and tests |

## Rules
- Every mutating call (create, rename, change type, move, delete, bulk): `IVersionGuard.EnsureEditable` (T07) → `409 version-not-editable`, then `IDocumentAuthorization.CanEditStructure(docId)` (owner only) → `403`.
- Title: trimmed, 1–500 chars. Node type must exist and be **active** for create/change.
- Parent must be in the same version (also enforced by the composite FK).
- Move under itself/descendant → `409 invalid-move`.
- Sort order with gaps; renumber siblings when a gap is exhausted — encapsulate in a `SiblingOrdering` helper (unit tested in `DocHub.Domain.Tests`), reused by folders (T06).
- `number` is computed on read (1-based position path), not stored.
- Deleting a subtree: stored procedure **`app.usp_DeleteSubtree @NodeId`** (FR-D3) — recursive CTE collecting ids, delete children-first inside a transaction. All other node CRUD via EF Core (FR-D1). Triggers from T03 must log every deleted node and content row.
- Each mutation updates `ModifiedAt/ModifiedByUserId` of the node.

## Performance
- Tree items carry `nodeTypeId` only; the UI resolves names from the separately cached `/api/node-types?includeInactive=true` (inactive types still need names; they are only excluded from create/change pickers), so a dictionary rename never makes a cached tree stale.
- **Caching** (NFR-L9): tree responses carry `ETag` = `VersionStamp.LastChangeLogId`, `Cache-Control: private, no-cache`, `Vary: X-User-Id`, honor `If-None-Match` → `304`; Signed-version trees are kept in `HybridCache` keyed by `versionId + LastChangeLogId` (any change incl. script edits → new stamp → miss). `EnsureCanView` always runs first.
- [AC] After a script edit of a Signed version, the next tree/content read returns the new data (no stale `304`); after the document is deleted, another user's revalidation gets `404`.
- Tree load = one query for nodes of the version (+ `hasContent` via `EXISTS`/`LEN(PlainText) > 0` projection, **not** loading `ContentHtml`), assembled in memory.
- NFR-6: tree of 2 000 nodes / depth 15 loads in < 500 ms — add a test with generated data.

## Acceptance criteria
- [ ] Build via API the example tree from [FR-T1](../requirements/02-document-tree.md) and a random 15-level tree; `GET /tree` returns correct nesting, order and numbering.
- [ ] Reorder siblings and move a subtree to another parent; numbering updates accordingly.
- [ ] Move into own descendant → `409 invalid-move`.
- [ ] Any mutation on a Signed version → `409 version-not-editable`.
- [ ] Deleting a node with 50 descendants removes all of them and writes 50+ `D` audit rows (nodes and contents).
- [ ] Inactive node type on create → `400`.
- [ ] Stale `rowVersion` → `409 concurrency-conflict`.
- [ ] Editor (document- or node-level) and Approver get `403` on every structural operation.
- [ ] A deleted document's tree and node reads → `404` for non-owner/non-admin users, `200` for owner and admin (FR-P5, via `EnsureCanView`).
