# Web UI

> Part of the [requirements set](README.md). IDs are stable — tasks and tests reference them.

- FR-UI1 Main window: virtual-folder tree on the left; document list of the selected folder on the right with **Add** and **Delete** document buttons.
- FR-UI2 Clicking a document row opens the document form: the document is shown as one Word-like page of **sections (one per node)** with the tree on the left for navigation and (owner) structure editing. Text is edited in place per section. **Each section shows its own change history and track changes directly in the editor** (FR-H4); there is no separate history screen.
- FR-UI3 An **Admin** tab (admins only) for supporting entities: folders, node types, content styles, users (read-only), audit log.
- FR-UI4 **Test mode**: while real authentication is out of scope the system runs in `Test` auth mode, and the header shows a **"Acting as" user dropdown** to switch between seeded users (e.g. owner → approver 1 → approver 2). The dropdown and a "TEST MODE" banner are shown only when the API reports test mode.
- FR-UI5 **[A]** Version bar in the document form (sign, new draft, discard draft), version comparison view, comments panel, permissions dialog.
