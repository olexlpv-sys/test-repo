# T06 — Virtual folders API

| | |
|---|---|
| **Depends on** | T04 |
| **Blocks** | T07, T14 |
| **Size** | S–M (1–1.5 days) |
| **Requirements** | FR-F1 … FR-F4 |
| **Read first** (nothing else) | [01-folders](../requirements/01-folders.md) · [architecture](../architecture.md) (only sections linked in the text) · [process](../process.md) |

## Goal
CRUD for the hierarchical virtual-folder structure that documents are attached to.

## API
| Method | Route | Notes |
|---|---|---|
| GET | `/api/folders/tree` | whole folder tree, nested: `[{ id, name, sortOrder, documentCount, children: [...] }]` (`documentCount` = non-deleted docs directly in the folder) |
| GET | `/api/folders/{id}` | `{ id, parentFolderId, name, sortOrder, path: [{id,name}], rowVersion }` — `path` = breadcrumb from root |
| POST | `/api/folders` | `{ parentFolderId?, name }` → `201`; appended as last sibling |
| PUT | `/api/folders/{id}` | rename: `{ name, rowVersion }` |
| POST | `/api/folders/{id}/move` | `{ newParentFolderId?, position?, rowVersion }` — re-parent and/or reorder; `position` = 0-based index among new siblings, default last |
| DELETE | `/api/folders/{id}?rowVersion=…` | `409 in-use` if it has sub-folders or **any** documents (deleted ones included — an admin moves them elsewhere first, T07) |

## Rules
- Name: trimmed, 1–200 chars, no `/ \`, unique among siblings (case-insensitive — collation) → `409` on duplicate.
- Move: target must exist; moving a folder into itself or into its descendant → `409 invalid-move` (check with recursive CTE or in-memory tree).
- Sort order uses gaps (see [ADR-02](../architecture.md)); moving renumbers siblings only when needed.
- Permissions: **admins only** for create/rename/move/delete (`403` otherwise, policy `AdminOnly`); every user can read the tree and folder details.

## Implementation notes
- Load the whole folder table in one query and build the tree in memory (folder count is expected to be small, < 10 000).
- Move must run in a transaction with `SERIALIZABLE` or an app lock (`sp_getapplock 'folders'`) to avoid concurrent cycle creation.

## Acceptance criteria
- [ ] Create a 5-level folder hierarchy via API; `GET /tree` returns it nested and ordered.
- [ ] Rename with stale `rowVersion` → `409 concurrency-conflict`.
- [ ] Move into own descendant → `409 invalid-move`; valid move changes the tree.
- [ ] Delete a folder with sub-folders, documents or only deleted documents → `409 in-use`; after admin moves the deleted documents out → `204`.
- [ ] Duplicate sibling name → `409`; same name under different parents is allowed.
- [ ] Non-admin create/rename/move/delete → `403`; non-admin `GET /tree` → `200`.
