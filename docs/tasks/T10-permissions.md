# T10 — Permissions: Owner, Editor (document / node), Approver

| | |
|---|---|
| **Depends on** | T07 |
| **Blocks** | T13 (full rules), T16 (permissions dialog) |
| **Can run in parallel with** | T08, T09 (they call `IDocumentAuthorization`) |
| **Size** | M (2 days) |
| **Requirements** | FR-P1 … FR-P5 |

## Goal
Manage document roles and enforce them in one place — the full implementation of `IDocumentAuthorization` introduced in T07.

## Role matrix

| Action | Owner | Editor (document) | Editor (node N) | Approver | Other user |
|---|:-:|:-:|:-:|:-:|:-:|
| View document, versions, tree, content, history, compare | ✔ | ✔ | ✔ | ✔ | ✔ (Q5) |
| Rename document | ✔ | ✔ | – | – | – |
| Add/edit/move/delete any node, edit any content | ✔ | ✔ | – | – | – |
| Edit title/type/content of N and of any descendant of N | ✔ | ✔ | ✔ | – | – |
| Add/move/delete descendants of N (not N itself) | ✔ | ✔ | ✔ | – | – |
| Move/delete N itself | ✔ | ✔ | – | – | – |
| Comment (document or node) | ✔ | ✔ | ✔ | ✔ | – |
| Resolve comments | ✔ | – | – | ✔ | – |
| Sign, new draft, discard draft, delete/move document | ✔ | – | – | – | – |
| Grant/revoke roles | ✔ | – | – | – | – |

Admins (`User.IsAdmin`) get no document rights by default (**assumption**) — adjust if support needs it.

## API
| Method | Route | Who | Notes |
|---|---|---|---|
| GET | `/api/documents/{id}/permissions` | any | `{ owner {id,displayName}, grants: [{ id, user, role, logicalNodeId?, nodeTitle?, grantedAt, grantedBy }] }` — `nodeTitle` resolved from the draft or latest version |
| POST | `/api/documents/{id}/permissions` | owner | `{ userId, role: "Editor"|"Approver", logicalNodeId? }` → `201` |
| DELETE | `/api/documents/{id}/permissions/{grantId}` | owner | `204` |
| POST | `/api/documents/{id}/transfer-ownership` | owner | *(optional)* `{ userId }` |
| GET | `/api/documents/{id}/my-permissions` | any | effective rights for UI: `{ isOwner, canEditDocument, editableLogicalNodeIds: [...], canComment, canResolve, canManage }` |

## Rules
- `logicalNodeId` only with role `Editor` (`400` otherwise); it must exist in the current draft or the latest signed version.
- Granting the owner a role → `400`. Duplicate grant → `409`.
- Node-scoped check `CanEditNode(doc, L)`: true if the user has a document-level Editor grant, or a node grant on `L` or on any **ancestor** of `L` in the version being edited. Ancestor resolution uses the in-memory tree or a recursive CTE; cache per request.
- If a granted node was deleted in the draft, the grant stays (it may exist again in older versions) but has no effect; UI shows it as "node not in current draft".
- `GET /api/documents/{id}` (T07) returns `myRoles` using the same service.
- Replace the owner-only implementation from T07; keep a unit-test suite for the matrix (table-driven tests).

## Acceptance criteria
- [ ] Table-driven tests cover every row of the matrix for every role.
- [ ] Node-scoped editor on "Chapter 2" can edit "Chapter 2 / Section 1 / Subsection 2" content, cannot edit "Chapter 1", cannot delete "Chapter 2" itself.
- [ ] Grants survive creating a new draft (same `LogicalNodeId`).
- [ ] Non-owner grant/revoke → `403`. Every grant/revoke is visible in `audit.ChangeLog`.
