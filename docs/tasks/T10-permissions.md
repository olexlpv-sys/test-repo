# T10 — Permissions: Owner, Editor (document / node), Approver

| | |
|---|---|
| **Depends on** | T07 |
| **Blocks** | T13 (full rules), T16 (permissions dialog) |
| **Can run in parallel with** | T08, T09 (they call `IDocumentAuthorization`) |
| **Size** | M (2 days) |
| **Requirements** | FR-P1 … FR-P6, FR-V6, FR-D2 |
| **Read first** (nothing else) | [06-permissions](../requirements/06-permissions.md) · [03-versioning-and-signing](../requirements/03-versioning-and-signing.md) (FR-V6 only) · [11-data-access](../requirements/11-data-access.md) · [architecture](../architecture.md) (only sections linked in the text) · [process](../process.md) |

## Goal
Manage document roles and enforce them in one place — the full implementation of `IDocumentAuthorization` introduced in T07.

## Role matrix

Principle: **the Owner defines the structure, Editors edit text, Approvers review and sign.**

| Action | Owner | Editor (document) | Editor (node N) | Approver | Other user |
|---|:-:|:-:|:-:|:-:|:-:|
| View document, versions, tree, content, history, compare | ✔ | ✔ | ✔ | ✔ | ✔ |
| **Structure**: add/delete/move/rename nodes, change node type | ✔ | – | – | – | – |
| Edit **text** (content) of any node | ✔ | ✔ | – | – | – |
| Edit **text** of N and of its descendants | ✔ | ✔ | ✔ | – | – |
| Comment (document or node) | ✔ | ✔ | ✔ | ✔ | – |
| Resolve / reopen comments | ✔ | – | – | ✔ | – |
| **Sign** the draft — version is finalized when **all** approvers signed | – | – | – | ✔ | – |
| New draft, discard draft, rename, delete document | ✔ | – | – | – | – |
| Move document to another folder | ✔ | – | – | – | admin (also deleted docs) |
| View a **deleted** document | ✔ | – | – | – | admin |
| Grant/revoke roles | ✔ | – | – | – | – |
| Restore deleted document | ✔ | – | – | – | admin |

- The owner **cannot** be granted a role and therefore cannot sign their own document (Q11). A document without an Approver cannot be signed — the UI tells the owner to add one. For manual testing use the test-mode "Acting as" dropdown (T14) to switch between owner and approvers.
- With several approvers **all of them must sign** (Q10). Adding an approver to a draft that is being signed makes it wait for the new approver too; revoking an approver re-runs the finalization check (T07 rule 2).
- Admins (`User.IsAdmin`) manage folders, node types and content styles and can restore deleted documents; they get no other document rights by default.

## API
| Method | Route | Who | Notes |
|---|---|---|---|
| GET | `/api/documents/{id}/permissions` | any | `{ owner {id,displayName}, grants: [{ id, user, role, logicalNodeId?, nodeTitle?, grantedAt, grantedBy }] }` — `nodeTitle` resolved from the draft or latest version |
| POST | `/api/documents/{id}/permissions` | owner | `{ userId, role: "Editor"|"Approver", logicalNodeId? }` → `201` |
| DELETE | `/api/documents/{id}/permissions/{grantId}` | owner | `204` |
| POST | `/api/documents/{id}/transfer-ownership` | owner | *(optional)* `{ userId }` — `400 target-has-role` if the target holds any grant on the document (owner can never be an approver) |
| GET | `/api/documents/{id}/my-permissions` | any | effective rights for UI: `{ isOwner, canEditStructure, canEditAllContent, editableLogicalNodeIds: [...], canComment, canResolve, canSign, canManage }` |

## Rules
- `logicalNodeId` only with role `Editor` (`400` otherwise); it must exist in the current draft or the latest signed version.
- Granting the owner a role → `400 owner-cannot-have-role`. Duplicate grant → `409 duplicate-grant`. Grant/revoke on a deleted document → `409 document-deleted` (T07 rule 1).
- **Data access** ([FR-D2](../requirements/11-data-access.md)): `IDocumentAuthorization` is implemented on top of **`app.usp_CheckPermission`** (single action) and **`app.usp_GetEffectivePermissions`** (all rights + `EditableLogicalNodeIds` for `my-permissions` and T07 `myRoles`); results cached per request. Grant CRUD itself goes through EF Core. Both procedures have DB tests covering the full matrix.
- `CanEditStructure(doc)`: owner only. `CanSign(doc)`: has an Approver grant. `CanEditContent(doc, L)`: owner, or a document-level Editor grant, or a node grant on `L` or on any **ancestor** of `L` in the version being edited. Ancestor resolution uses the in-memory tree or a recursive CTE; cache per request.
- If a granted node was deleted in the draft, the grant stays (it may exist again in older versions) but has no effect; UI shows it as "node not in current draft".
- `GET /api/documents/{id}` (T07) returns `myRoles` using the same service.
- Replace the owner-only implementation from T07; keep a unit-test suite for the matrix (table-driven tests).

## Acceptance criteria
- [ ] Table-driven tests cover every row of the matrix for every role.
- [ ] Node-scoped editor on "Chapter 2" can edit the text of "Chapter 2 / Section 1 / Subsection 2", cannot edit the text of "Chapter 1", and gets `403` on any structural operation (including inside "Chapter 2").
- [ ] Only an approver can sign; owner, editors → `403`. Granting a role to the owner → `400`.
- [ ] Grants survive creating a new draft (same `LogicalNodeId`).
- [ ] `GET /permissions` and `/my-permissions` of a deleted document → `404` for non-owner/non-admin (FR-P5).
- [ ] Node editor on "Chapter 2": after the owner moves "Section X" from Chapter 2 to Chapter 1 **in the draft**, the editor can no longer edit Section X in the draft (while v1 is unaffected); moving a node into Chapter 2 grants edit rights on it — `usp_CheckPermission` walks the tree of the given `@DocumentVersionId`.
- [ ] `usp_CheckPermission` with an unknown `@Action` raises an error; `EditContent` without `@DocumentVersionId` raises an error.
- [ ] Admin move of an active and of a deleted document → `200`; owner move of a deleted document → `409 document-deleted`.
- [ ] Permission check < 10 ms and effective permissions < 50 ms on the NFR-6 data set (tagged perf test).
- [ ] Non-owner grant/revoke → `403`. Every grant/revoke is visible in `audit.ChangeLog`.
