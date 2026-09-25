# T13 — Comments API

| | |
|---|---|
| **Depends on** | T07, T10 (for full role rules; can start with the T07 owner-only seam) |
| **Blocks** | T16 (comments panel) |
| **Size** | S–M ([sizing](../process.md#9-sizing-and-agent-effort)) |
| **Requirements** | FR-CM1, FR-CM2, FR-P3 |
| **Read first** (nothing else) | [07-comments](../requirements/07-comments.md) · [06-permissions](../requirements/06-permissions.md) · [architecture](../architecture.md) (only sections linked in the text) · [process](../process.md) |

## Goal
Comments on the document as a whole or on a specific node, with one level of replies, edit/delete of own comments and resolve/reopen.

## API
| Method | Route | Notes |
|---|---|---|
| GET | `/api/versions/{versionId}/comments?logicalNodeId?&scope=all|document|node&includeResolved=true&includePreviousVersions=false` | threads: `[{ id, logicalNodeId?, nodeTitle?, author, body, createdAt, editedAt, resolvedAt, resolvedBy, rowVersion, replies: [...] }]` |
| GET | `/api/documents/{id}/comments/counts?versionId=` | `{ document: 2, nodes: { "<logicalNodeId>": 3, … } }` — for badges in the tree |
| POST | `/api/versions/{versionId}/comments` | `{ logicalNodeId?, parentCommentId?, body }` → `201` |
| PUT | `/api/comments/{id}` | `{ body, rowVersion }` — author only |
| DELETE | `/api/comments/{id}?rowVersion=…` | author or owner; soft delete (`body` shown as "deleted" if it has replies) |
| POST | `/api/comments/{id}/resolve` / `/reopen` | top-level comments only; owner or approver |

## Rules
- Who may comment: `IDocumentAuthorization.CanComment` (owner, editors, approvers — T10 matrix).
- Allowed on Draft and Signed versions; on a discarded (Deleted) version → `409 version-not-editable`; on a deleted document → `409 document-deleted`.
- `logicalNodeId` must exist in that version (`400`).
- Replies: `parentCommentId` must be a top-level comment of the same version and same node (no nested replies) → `400`.
- `body`: 1–4000 chars, plain text (render as text in the UI; no HTML).
- Comments are bound to a version; they do **not** copy to a new draft (Q6). `GET` on a draft can pass `includePreviousVersions=true` to also return comments from older versions for the same `LogicalNodeId`, flagged with `versionLabel`.

## Implementation notes (Q13)
- Counts are the not-deleted comments (replies included) of the version: by default the draft, else the current version.
- A deleted comment stays in the list (`isDeleted`, `body = null`) only while it has live replies. Replies to a deleted comment are refused.
- Editing, deleting and resolving follow the same checks as creating: 404, then 409 document-deleted, then 409 version-not-editable.
- `includePreviousVersions` adds node comments of older, not discarded versions, but only on nodes the version still has.

## Acceptance criteria
- [ ] Approver can comment on the document and on a node; a user without roles → `403`.
- [ ] Reply to a reply → `400`.
- [ ] Only the author edits; owner can delete any; only owner/approver resolve.
- [ ] Commenting on a signed version works; on a discarded draft → `409`.
- [ ] Counts endpoint matches the list.
- [ ] On a v2 draft, `includePreviousVersions=true` also returns v1 comments of the same node, flagged `versionLabel = "v1"`; default excludes them.
- [ ] Comment on a deleted document → `409 document-deleted`.
- [ ] A deleted document's comment list and counts → `404` for non-owner/non-admin users, `200` for owner and admin (FR-P5, via `EnsureCanView`).
