# Requirements

Consolidated from the product owner's brief. Items marked **[A]** are assumptions made while writing the tasks —
confirm or correct them (see [Open questions](#open-questions)).

## 1. Glossary

| Term | Meaning |
|---|---|
| **Folder** | Virtual folder. Folders form a tree of arbitrary depth. Purely logical (not a file system). |
| **Document** | Logical document. Belongs to exactly one folder. Has an owner and a sequence of versions. |
| **Document version** | A snapshot of the document tree. Status `Draft`, `Signed` or `Deleted`. Signed versions are numbered v1, v2, v3… |
| **Node** | An element of the document tree (e.g. "Chapter 1", "Section 2"). Has a type, a free-text title, an order among siblings, and rich-text content. |
| **Logical node id** | A stable GUID identifying "the same node" across versions. Used for history, comparison, node-scoped permissions and comments. |
| **Node type** | Entry of an editable dictionary (Chapter, Section, Subsection, …). |
| **Change log** | Audit trail of every data change, written by the database itself (so it also captures support scripts). |

## 2. Functional requirements

### 2.1 Virtual folders
- FR-F1 Folders form a hierarchy of arbitrary depth with arbitrary names.
- FR-F2 CRUD for folders: create, rename, move (re-parent, with cycle protection), delete.
- FR-F3 Documents are attached to a folder; a document can be moved to another folder.
- FR-F4 **[A]** Folder names are unique among siblings. A folder can be deleted only if it has no sub-folders and no non-deleted documents.

### 2.2 Document tree
- FR-T1 A document version contains a tree of nodes of **arbitrary depth**, with arbitrary titles, e.g.
  ```
  Chapter 1
    Section 1
    Section 2
      Subsection 1
      Subsection 2
  Chapter 2
    Section 1
      Subsection 1
      Subsection 2
  ```
- FR-T2 API supports building the hierarchy: add node (as child / at position), rename, change type, move (re-parent / reorder), delete (with subtree).
- FR-T3 Each node has a **type** from the editable node-type dictionary.
- FR-T4 Each node can hold **rich text including tables** (headings, bold/italic/underline, lists, links, tables with merged cells).
- FR-T5 **[A]** Numbering ("1.2.1") is computed for display only, from the position in the tree; it is not stored.

### 2.3 Node types dictionary
- FR-N1 CRUD for node types (code, name, description, sort order, active flag).
- FR-N2 **[A]** A node type used by any node cannot be deleted, only deactivated. Deactivated types cannot be used for new nodes.

### 2.4 Document lifecycle & versioning
- FR-V1 Statuses: `Draft`, `Signed`, `Deleted`.
- FR-V2 Signing a draft approves it as the next version number: v1, v2, v3…
- FR-V3 A new `Draft` version can be created from a signed version (deep copy of tree and content). **[A]** At most one draft per document at any time.
- FR-V4 **Only Draft versions are editable.** Any modification of a Signed/Deleted version or of a deleted document via the API is rejected.
- FR-V5 **[A]** `Deleted` applies to (a) a whole document (soft delete) and (b) a discarded draft version. Signed versions are never deleted through the API.
- FR-V6 **[A]** Only the document owner can sign, create a new draft, discard a draft, and delete the document.

### 2.5 Change tracking
- FR-H1 Every change to folders, documents, versions, nodes, node content, permissions and comments is recorded: who, when, what (old and new values).
- FR-H2 A `User` entity is introduced; for now users are a **hardcoded seed** in the database (no real authentication).
- FR-H3 Data may be changed directly in the database by the **support team via SQL scripts**. Such changes **must also be captured** in the change log, marked as script changes, with the DB login and an optional ticket/reason.
- FR-H4 API to view the change history of node text (per node, across versions) including a readable diff between consecutive states.
- FR-H5 **[A]** If a Signed version is modified by a script after signing, the system detects it (content hash) and flags the version as "modified after signing".

### 2.6 Version comparison
- FR-C1 Compare any two versions of a document (e.g. v1 vs v3), and any signed version vs the current draft.
- FR-C2 Result shows per node: added, removed, moved, renamed, type changed, content changed (with text diff).

### 2.7 Permissions
- FR-P1 **Owner** — the user who created the document. Full rights.
- FR-P2 **Editor** — can edit the whole document, **or only a specific node** (and **[A]** its subtree).
- FR-P3 **Approver** — can leave comments on nodes and on the document as a whole.
- FR-P4 **[A]** The owner grants/revokes Editor/Approver roles. Permissions are defined per *document* (not per version), so they carry over to new drafts.
- FR-P5 **[A]** Any seeded user can view any (non-deleted) document; roles restrict modifications and commenting only.

### 2.8 Comments
- FR-CM1 API for comments on a node or on the whole document; replies (one level of threading **[A]**); edit/delete own comment; resolve/reopen.
- FR-CM2 **[A]** Owner, Editors and Approvers can comment. Comments allowed on Draft and Signed versions, not on Deleted.

### 2.9 Web UI
- FR-UI1 Main window: virtual-folder tree on the left; document list of the selected folder on the right with **Add** and **Delete** document buttons.
- FR-UI2 Clicking a document row opens the document form: edit the node tree, edit node content, see the change history of each node.
- FR-UI3 An **Admin** tab for supporting entities (node types, users (read-only), folders management) **[A]**.
- FR-UI4 **[A]** A "current user" switcher in the header (since authentication is out of scope, users are seeded).
- FR-UI5 **[A]** Version bar in the document form (sign, new draft, discard draft), version comparison view, comments panel, permissions dialog.

## 3. Non-functional requirements

- NFR-1 .NET 10 LTS; Azure SQL Database (compatibility level 160).
- NFR-2 OpenAPI description of the API with an interactive UI (Scalar) in Development.
- NFR-3 Errors returned as RFC 9457 `ProblemDetails` with stable `type` codes.
- NFR-4 Optimistic concurrency on all editable entities (`rowversion`); conflicting updates return `409`.
- NFR-5 Rich text is **sanitized server-side** (allow-list) to prevent stored XSS.
- NFR-6 Tree operations must handle documents with ≥ 2 000 nodes and depth ≥ 15 with tree load < 500 ms (local DB).
- NFR-7 Integration tests run against a real SQL Server (Testcontainers) with the DACPAC deployed.

## 4. Out of scope (for these first tasks)

Real authentication (Entra ID), Word import/export, full-text search, attachments/images upload,
notifications, approval workflow (multi-step), real-time collaborative editing, Azure infrastructure-as-code.

## Open questions

| # | Question | Current assumption |
|---|---|---|
| Q1 | Web UI technology: React (recommended for rich-text/tree components) or Blazor? | React + TS + Vite + TipTap |
| Q2 | Can a new draft be created from *any* signed version or only the latest? | Any signed version; latest by default |
| Q3 | Who can sign? Only owner, or does signing require approver approval? | Owner only, no approval step |
| Q4 | Node-scoped Editor: may they add/delete/move child nodes under their node, or only edit text? | Full edit of the node's subtree, but cannot move/delete the granted node itself |
| Q5 | Is reading documents open to everyone? | Yes, all users can read |
| Q6 | Should comments carry over into a new draft? | No; comments are bound to a version, UI can show previous versions' comments |
| Q7 | Who manages folders and node types — any user or only admins? | Folders: any user; node types: admins (`User.IsAdmin`) |
| Q8 | Rich-text storage format: sanitized HTML or editor JSON (ProseMirror)? | Sanitized HTML (+ derived plain text) |
| Q9 | Is there a need to restore deleted documents via the API? | No, support script only |
