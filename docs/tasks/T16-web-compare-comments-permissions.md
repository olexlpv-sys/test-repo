# T16 — Web UI: version comparison, comments, permissions dialog

| | |
|---|---|
| **Depends on** | T10, T12, T13, T15 |
| **Size** | M–L (3–4 days) |
| **Requirements** | FR-C1, FR-C2, FR-CM1, FR-P1 … FR-P4, FR-UI5 |
| **Read first** (nothing else) | [05-version-comparison](../requirements/05-version-comparison.md) · [07-comments](../requirements/07-comments.md) · [06-permissions](../requirements/06-permissions.md) · [08-web-ui](../requirements/08-web-ui.md) · [architecture](../architecture.md) (only sections linked in the text) · [process](../process.md) |

## Goal
Complete the document form with comparison, comments and role management.

## Scope

### 1. Compare view (`/documents/:id/compare?base=…&target=…`)
- Opened from **Compare…** in the version bar; two selectors (Base / Target, target may be "Current draft"), swap button.
- Summary chips: `+3 added, −1 removed, 2 moved, 1 renamed, 5 content changed`; filter "changed only".
- Merged tree (from `GET /compare`) with status colors: Added green, Removed red strike-through, Modified amber, Moved ↪ icon with tooltip "from 1.2 → 2.1".
- Selecting a node shows its content diff (`/compare/nodes/{logicalNodeId}`) — inline `<ins>/<del>` view; optional side-by-side toggle.

### 2. Comments
- Tree badges with counts (`/comments/counts`).
- Right panel tab **💬 Comments**: document-level thread list + threads for the selected node; add comment, reply, edit/delete own, resolve/reopen (per permissions); "show resolved" toggle; "show comments from previous versions" toggle on drafts.
- Comment input disabled with a tooltip when the user cannot comment.

### 3. Permissions dialog
- Opened from the version bar (**Share / Permissions**), visible to everyone, editable by owner only.
- List: owner (read-only), grants with role, scope ("Whole document" or node number + title, "node not in current draft" warning), granted by/at, remove button.
- Add grant: **type-ahead** user picker (`/api/users?search=`), role, scope: whole document / pick node from a tree picker (Editor only).
- After changes refresh `my-permissions` so the editor reflects new rights immediately.

## Acceptance criteria
- [ ] Compare v1 vs draft shows the same summary as the API; clicking a changed node shows its diff.
- [ ] As approver (user switcher), add a node comment and a document comment; owner replies and resolves; badges update.
- [ ] Owner grants node-scoped Editor to another user; switching to that user shows only that subtree's text editable and no structure actions.
- [ ] Granting Approver to the owner is not offered in the user picker.
- [ ] Non-owner sees the permissions dialog read-only.
