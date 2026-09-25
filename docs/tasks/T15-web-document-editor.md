# T15 — Web UI: document form (tree editor, rich text, node history, versions)

| | |
|---|---|
| **Depends on** | T08, T09, T11, T14 |
| **Blocks** | T16 |
| **Size** | L (4–5 days) |
| **Requirements** | FR-UI2, FR-UI5, FR-T*, FR-V*, FR-H4 |

## Goal
The document form opened from the list: edit the node tree, edit node content with tables, see the change history of each node, drive the version lifecycle.

## Layout (`/documents/:id?version=…&node=…`)
```
┌──────────────────────────────────────────────────────────────────────────────┐
│ ← Back  NDA with Contoso  [Draft (based on v2) ▾]   [Sign] [Discard draft]   │
│                                                     [New draft] [Compare…]   │
├───────────────────────┬──────────────────────────────────────┬───────────────┤
│ Structure  [+ Node]   │ 1.2 Section — "Scope"      [type ▾]  │ History │ 💬  │
│ ▾ 1 Chapter 1         │ ┌──────────────────────────────────┐ │ 10:15 Alice   │
│    1.1 Section 1      │ │ B I U • 1. ⊞ table …             │ │  content +12  │
│  ▸ 1.2 Section 2   ●  │ │                                  │ │ 09:02 script  │
│ ▾ 2 Chapter 2         │ │  rich text editor                │ │  INC-1234 ⚠   │
│    2.1 Section 1      │ └──────────────────────────────────┘ │               │
│                       │ Saved · 10:15                        │ [diff view]   │
└───────────────────────┴──────────────────────────────────────┴───────────────┘
```

## Scope

### 1. Version bar
- Version selector: all versions (`GET /api/documents/{id}`) with labels; default = draft if exists, else latest signed. Selected version in URL.
- Read-only banner for Signed/Deleted versions; ⚠ badge when `modifiedAfterSigning`.
- Buttons (visible per `my-permissions`): **Sign** (confirm: "This will become v{n}"), **New draft** (from selected signed version), **Discard draft** (confirm), **Compare…** (T16).

### 2. Tree panel (`GET /api/versions/{id}/tree`)
- Shows numbering + title + node-type badge; expand/collapse; keyboard navigation.
- Draft + permission: add child / add sibling (dialog: type + title), inline rename, change type, delete (confirm, shows descendant count), move via **drag-and-drop** (`react-arborist` or equivalent) with drop validation (no drop into own subtree) — fallback "Move up/down/indent/outdent" buttons are acceptable for the first iteration.
- Nodes the current user cannot edit are visually muted (uses `editableLogicalNodeIds`).
- Comment count badges (after T13/T16 — leave a slot).

### 3. Content editor
- **TipTap** with StarterKit + Underline + Link + Table (TableRow/Header/Cell with `resizable`), toolbar incl. insert table, add/remove row/column, merge/split cells.
- Load `GET /api/nodes/{id}/content`; **debounced autosave** (e.g. 1.5 s after last keystroke + on blur/node switch) via `PUT` with `rowVersion`; status indicator "Saving… / Saved · time / Conflict".
- On `409 concurrency-conflict`: show a dialog with options "Reload theirs" / "Overwrite" (overwrite = re-fetch rowVersion and save).
- After save, replace editor content with the sanitized HTML only if it differs (avoid caret jumps).
- Read-only mode for non-draft versions or missing permission.

### 4. History panel (per selected node)
- `GET /api/documents/{id}/nodes/{logicalNodeId}/history` — infinite list: time, user (or "Script · login · ticket"), kind, summary, version label.
- Script entries and `afterSigning` entries highlighted.
- Clicking a `ContentChanged` entry opens a **diff view** (`/api/history/entries/{id}/diff` → render `html` with `<ins>` green / `<del>` red strike-through).
- Optional toggle: "Document activity" (document-wide feed).

## Acceptance criteria
- [ ] Build the FR-T1 example tree entirely from the UI, including drag-and-drop move; reload shows the same tree.
- [ ] Type text, insert a 3×3 table, merge two cells → autosaved; reload shows identical content.
- [ ] Signed version opens read-only; Sign/New draft/Discard work and the selector updates.
- [ ] History panel shows each save with the correct user; diff view highlights changes; a support-script change is shown as such.
- [ ] Node-scoped editor (switch user) can edit only their subtree.
- [ ] Two browser windows editing the same node → second save shows the conflict dialog.
- [ ] Playwright smoke: add node → type content → sign → new draft → edit → history shows 2 entries.
