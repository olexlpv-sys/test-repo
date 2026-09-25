# T15 — Web UI: document form (tree editor, rich text, node history, versions)

| | |
|---|---|
| **Depends on** | T08, T09, T10, T11, T14 |
| **Blocks** | T16 |
| **Size** | XL (6–7 days) |
| **Requirements** | FR-UI2, FR-UI5, FR-T*, FR-V*, FR-H4 |
| **Read first** (nothing else) | [08-web-ui](../requirements/08-web-ui.md) · [02-document-tree](../requirements/02-document-tree.md) · [03-versioning-and-signing](../requirements/03-versioning-and-signing.md) · [04-change-tracking](../requirements/04-change-tracking.md) · [content-format](../content-format.md) · [architecture](../architecture.md) (only sections linked in the text) · [process](../process.md) |

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
- **Signing panel** (Draft): progress `Signatures 1 / 2`, list of approvers with state ✔ signed · ⚠ outdated (draft changed after signing) · ⏳ pending (`GET /api/versions/{id}/signatures`).
  - Approver: **Sign** (optional note; confirm "Your signature confirms the current content") and **Withdraw signature**. When the last signature completes the version, show "Signed as v{n}" and switch the view to it.
  - Owner/editor: read-only progress; hint "Add an approver to sign" when there are none; warning before editing a draft that already has signatures ("Editing will outdate 1 signature").
- Other buttons (visible per `my-permissions`): **New draft** (always from the latest signed version), **Discard draft** (confirm), **Compare…** (T16).

### 2. Tree panel (`GET /api/versions/{id}/tree`)
- Shows numbering + title + node-type badge; expand/collapse; keyboard navigation.
- Structure editing is **owner-only** (Draft): add child / add sibling (dialog: type + title), inline rename, change type, delete (confirm, shows descendant count), move via **drag-and-drop** (`react-arborist` or equivalent) with drop validation (no drop into own subtree) — fallback "Move up/down/indent/outdent" buttons are acceptable for the first iteration.
- For editors the tree is read-only; nodes whose **text** they cannot edit are visually muted (uses `editableLogicalNodeIds`).
- Comment count badges (after T13/T16 — leave a slot).

### 3. Content editor (Word-like — spec: [content-format.md](../content-format.md))
- **TipTap** with the extensions listed in content-format.md §2 incl. the custom ones (FontSize, LineSpacing, ParagraphSpacing, Indent, ParagraphStyle, TableCellShading/Borders, PageBreak).
- **Ribbon-like toolbar** grouped like Word: *Styles* dropdown (from `/api/content-styles`, rendered in its own style) · *Font* (family, size, bold/italic/underline/strike, sub/superscript, color, highlight, clear formatting) · *Paragraph* (align, bullets/numbering, indent/outdent, line & paragraph spacing) · *Insert* (table with size picker, page break, link) · *Table* contextual group (insert/delete row/column, merge/split, cell shading, borders, column width).
- Editor surface styled with `/api/content-styles/stylesheet.css` and a page-like layout (white page, margins) so it looks like the Word output.
- Paste from Word: keep supported formatting (map `mso-*`/inline styles to schema attributes via TipTap paste rules), drop the rest.
- Load `GET /api/nodes/{id}/content` (JSON); **debounced autosave** (1.5 s after last change + on blur/node switch) via `PUT` with `rowVersion`; status "Saving… / Saved · time / Conflict / Validation error". After save, replace editor content with the canonical JSON only if it differs (avoid caret jumps).
- On `409 concurrency-conflict`: dialog "Reload theirs" / "Overwrite". On `400` show the offending element.
- Read-only mode for non-draft versions or missing content permission (`editableLogicalNodeIds`).

### 4. History panel (per selected node)
- `GET /api/documents/{id}/nodes/{logicalNodeId}/history` — infinite list: time, user (or "Script · login · ticket"), kind, summary, version label.
- Script entries and `afterSigning` entries highlighted.
- Clicking a `ContentChanged` entry opens a **diff view** (`/api/history/entries/{id}/diff` → render `html` with `<ins>` green / `<del>` red strike-through, formatting-only changes underlined dotted with a tooltip "bold added").
- Optional toggle: "Document activity" (document-wide feed).

## Acceptance criteria
- [ ] Build the FR-T1 example tree entirely from the UI, including drag-and-drop move; reload shows the same tree.
- [ ] Apply Heading 2 style, a custom font/size/color, 1.5 line spacing, a numbered list, insert a 3×3 table, merge two cells, shade a cell → autosaved; reload shows identical content.
- [ ] Paste a fragment from Word with headings, bold and a table — formatting is preserved within the schema.
- [ ] Signed version opens read-only; Sign/New draft/Discard work and the selector updates.
- [ ] History panel shows each save with the correct user; diff view highlights changes; a support-script change is shown as such.
- [ ] Node-scoped editor (switch user) can edit only the text of their node(s) and descendants; no structure actions are shown.
- [ ] Using the "Acting as" dropdown: carol signs (1/2), owner edits → carol shown as outdated, carol and dave sign → version becomes v{n}. Owner and editors never see **Sign**.
- [ ] Two browser windows editing the same node → second save shows the conflict dialog.
- [ ] Playwright smoke: owner adds node → types content → grants two approvers → both sign → owner creates new draft → edits → history shows 2 entries.
