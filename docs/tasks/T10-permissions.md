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

Principle: **the Owner defines the structure, Editors edit text, Approvers review and sign.**

| Action | Owner | Editor (document) | Editor (node N) | Approver | Other user |
|---|:-:|:-:|:-:|:-:|:-:|
| View document, versions, tree, content, history, compare | ✔ | ✔ | ✔ | ✔ | ✔ |
| **Structure**: add/delete/move/rename nodes, change node type, rename document | ✔ | – | – | – | – |
| Edit **text** (content) of any node | ✔ | ✔ | – | – | – |
| Edit **text** of N and of its descendants | ✔ | ✔ | ✔ | – | – |
| Comment (document or node) | ✔ | ✔ | ✔ | ✔ | – |
| Resolve / reopen comments | ✔ | – | – | ✔ | – |
| **Sign** the draft (→ vN) | – | – | – | ✔ | – |
| New draft, discard draft, delete/move document | ✔ | – | – | – | – |
| Grant/revoke roles | ✔ | – | – | – | – |

- The owner **cannot** be granted a role and therefore cannot sign their own document (four-eyes principle, Q11). A document without an Approver cannot be signed — the UI tells the owner to add one.
- With several approvers, a signature from **any one** of them is enough (Q10).
- Admins (`User.IsAdmin`) get no document rights by default (**assumption**).

## API
| Method | Route | Who | Notes |
|---|---|---|---|
| GET | `/api/documents/{id}/permissions` | any | `{ owner {id,displayName}, grants: [{ id, user, role, logicalNodeId?, nodeTitle?, grantedAt, grantedBy }] }` — `nodeTitle` resolved from the draft or latest version |
| POST | `/api/documents/{id}/permissions` | owner | `{ userId, role: "Editor"|"Approver", logicalNodeId? }` → `201` |
| DELETE | `/api/documents/{id}/permissions/{grantId}` | owner | `204` |
| POST | `/api/documents/{id}/transfer-ownership` | owner | *(optional)* `{ userId }` |
| GET | `/api/documents/{id}/my-permissions` | any | effective rights for UI: `{ isOwner, canEditStructure, canEditAllContent, editableLogicalNodeIds: [...], canComment, canResolve, canSign, canManage }` |

## Rules
- `logicalNodeId` only with role `Editor` (`400` otherwise); it must exist in the current draft or the latest signed version.
- Granting the owner a role → `400`. Duplicate grant → `409`.
- `CanEditStructure(doc)`: owner only. `CanSign(doc)`: has an Approver grant. `CanEditContent(doc, L)`: owner, or a document-level Editor grant, or a node grant on `L` or on any **ancestor** of `L` in the version being edited. Ancestor resolution uses the in-memory tree or a recursive CTE; cache per request.
- If a granted node was deleted in the draft, the grant stays (it may exist again in older versions) but has no effect; UI shows it as "node not in current draft".
- `GET /api/documents/{id}` (T07) returns `myRoles` using the same service.
- Replace the owner-only implementation from T07; keep a unit-test suite for the matrix (table-driven tests).

## Acceptance criteria
- [ ] Table-driven tests cover every row of the matrix for every role.
- [ ] Node-scoped editor on "Chapter 2" can edit the text of "Chapter 2 / Section 1 / Subsection 2", cannot edit the text of "Chapter 1", and gets `403` on any structural operation (including inside "Chapter 2").
- [ ] Only an approver can sign; owner, editors → `403`. Granting a role to the owner → `400`.
- [ ] Grants survive creating a new draft (same `LogicalNodeId`).
- [ ] Non-owner grant/revoke → `403`. Every grant/revoke is visible in `audit.ChangeLog`.
