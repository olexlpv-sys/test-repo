# Permissions

> Part of the [requirements set](README.md). IDs are stable — tasks and tests reference them.

- FR-P1 **Owner** — the user who created the document. **Only the owner defines the structure**: adds, deletes, moves, renames nodes, changes node types, renames the document. The owner can also edit text of any node and manages roles.
- FR-P2 **Editor** — edits **text (node content) only**, never the structure: either of all nodes (document-level grant) or of a specific node (node-level grant, including its descendants).
- FR-P3 **Approver** — leaves comments on nodes and on the document as a whole, resolves comments and **signs** the draft (FR-V6).
- FR-P4 **[A]** The owner grants/revokes Editor/Approver roles. Permissions are defined per *document* (not per version), so they carry over to new drafts.
- FR-P5 Any user can view any **non-deleted** document; roles restrict modifications and commenting only. Deleted documents are visible (read-only) only to their owner and admins.
- FR-P6 **Admin** (`User.IsAdmin`) — manages folders, node types and content styles; can restore deleted documents. No other document rights by default.
