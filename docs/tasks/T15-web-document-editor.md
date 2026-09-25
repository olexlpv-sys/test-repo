# T15 — Web UI: document form (section editor with inline per-section history, tree, signing)

| | |
|---|---|
| **Depends on** | T08, T09, T10, T11, T14 |
| **Blocks** | T16 |
| **Size** | XL (7–8 days) |
| **Requirements** | FR-UI2, FR-UI5, FR-T*, FR-V*, FR-H4, NFR-L6 |
| **Read first** (nothing else) | [08-web-ui](../requirements/08-web-ui.md) · [02-document-tree](../requirements/02-document-tree.md) · [03-versioning-and-signing](../requirements/03-versioning-and-signing.md) · [04-change-tracking](../requirements/04-change-tracking.md) · [content-format](../content-format.md) · [architecture](../architecture.md) (only sections linked in the text) · [process](../process.md) |

## Goal
The document form opened from the list: the whole document is shown as **one Word-like page made of sections** (one section per node). Users edit text section by section, and **each section carries its own change history and track-changes view right inside the editor**. The tree on the left is used for navigation and (for the owner) for editing the structure. The form also drives the version lifecycle.

## Layout (`/documents/:id?version=…&node=…`)
```
┌───────────────────────────────────────────────────────────────────────────────────┐
│ ← Back  NDA with Contoso  [Draft (based on v2) ▾]  Signatures 1/2  [Compare…]     │
│ [Styles ▾] [Font ▾ 11] B I U  ≡ ≡  • 1.  ⊞ table …   [Show changes since: v2 ▾ ☐] │
├──────────────────────┬────────────────────────────────────────────────────────────┤
│ Structure  [+ Node]  │ ┌─ page ───────────────────────────────────────────────┐   │
│ ▾ 1 Chapter 1        │ │ 1 Chapter 1                              🕘 3   💬 1  │   │
│    1.1 Section 1  ●3 │ │   Text of chapter 1 …                                 │   │
│  ▸ 1.2 Section 2     │ │ 1.1 Section 1                            🕘 3 ▲       │   │
│ ▾ 2 Chapter 2        │ │ ┌ history of 1.1 ───────────────────────────────────┐ │   │
│    2.1 Section 1     │ │ │ ● 10:15 Alice  content +12/−3      [view] [compare]│ │   │
│                      │ │ │ ● 09:02 Script · jdoe · INC-1234 ⚠ after signing    │ │   │
│                      │ │ │ ● v2 signed 2026-09-01 (carol, dave)               │ │   │
│                      │ │ └────────────────────────────────────────────────────┘ │   │
│                      │ │   The ~~old~~ <new> scope of the agreement …           │   │
│                      │ │   (track changes: hover → "Alice, 10:15")             │   │
│                      │ │ 1.2 Section 2                                   🕘 0  │   │
│                      │ └───────────────────────────────────────────────────────┘   │
└──────────────────────┴────────────────────────────────────────────────────────────┘
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
- **Section-based page:** every node is a section: a numbered heading (title rendered with the node type's heading style) followed by its content, in tree order. Each section is a separate TipTap instance bound to one `NodeContent`. There is one shared ribbon, which acts on the focused section. Sections the user may not edit are read-only (muted heading marker). Sections are **virtualized**: only sections near the viewport are mounted, so documents with 2 000 nodes stay fluid (NFR-L6). Clicking a node in the tree scrolls to its section and vice versa (URL `node=`).
- **TipTap** with the extensions listed in content-format.md §2 incl. the custom ones (FontSize, LineSpacing, ParagraphSpacing, Indent, ParagraphStyle, TableCellShading/Borders, PageBreak).
- **Ribbon-like toolbar** grouped like Word: *Styles* dropdown (from `/api/content-styles`, rendered in its own style) · *Font* (family, size, bold/italic/underline/strike, sub/superscript, color, highlight, clear formatting) · *Paragraph* (align, bullets/numbering, indent/outdent, line & paragraph spacing) · *Insert* (table with size picker, page break, link) · *Table* contextual group (insert/delete row/column, merge/split, cell shading, borders, column width).
- Editor surface styled with `/api/content-styles/stylesheet.css` and a page-like layout (white page, margins) so it looks like the Word output.
- Paste from Word: keep supported formatting (map `mso-*`/inline styles to schema attributes via TipTap paste rules), drop the rest.
- Load `GET /api/nodes/{id}/content` (JSON); **debounced autosave** (1.5 s after last change + on blur/node switch) via `PUT` with `rowVersion`; status "Saving… / Saved · time / Conflict / Validation error". After save, replace editor content with the canonical JSON only if it differs (avoid caret jumps).
- On `409 concurrency-conflict`: dialog "Reload theirs" / "Overwrite". On `400` show the offending element.
- Read-only mode for non-draft versions or missing content permission (`editableLogicalNodeIds`).

### 4. Change history inside the editor — per section (FR-H4, FR-UI2)
- **Section header indicators:** 🕘 *n* = number of changes to this node since the chosen baseline (default: the latest signed version); a coloured side bar marks sections changed since the baseline; a ⚠ icon marks script changes and changes after signing. Data comes from `GET /api/versions/{id}/change-summary?since=…` (T11) — one call for all sections.
- **Section history (inline):** clicking 🕘 expands a timeline **inside the section**, between the heading and the text. It shows every change of this node across versions, newest first: time, user or "Script · login · ticket", kind (content, rename, type change, move, created, copied to draft), `+/−` word stats, and version markers ("v2 signed · carol, dave"). Data: `GET /api/documents/{id}/nodes/{logicalNodeId}/history` (T11), paged.
  - **View:** the section text switches to a read-only rendering of the node as it was at that entry.
  - **Compare:** the section text switches to **track changes** between that entry and the current state.
  - **Restore this text** (owner/editor with rights, Draft only): puts the historical content into the section editor as a normal edit. It is saved and audited like any change.
- **Track changes in the text (per section and document-wide):**
  - The *Show changes since* selector in the toolbar offers: latest signed version, any version, or a date. When it is on, every changed section shows its text in track-changes mode: insertions underlined in the author's colour, deletions struck through, formatting changes dotted-underlined.
  - Hovering or clicking a change shows its author, time and source (App / Script + ticket).
  - Data: `GET /api/documents/{id}/nodes/{logicalNodeId}/changes?since=…` (T11 attributed diff), loaded lazily per visible section.
  - Track-changes marks are **display-only decorations** (ProseMirror decorations), never stored in `ContentJson`.
  - While track changes are shown the section is read-only. The user turns it off to continue editing (**assumption A-5**).
- Structural changes appear in the section header, e.g. "moved from 2.1", "renamed from 'Scope'", "type Section → Subsection", "new since v2". Sections deleted since the baseline appear as collapsed struck-through placeholders at their former position, and can be expanded to show their last content.
- The **document activity feed** (`GET /api/documents/{id}/history`) is available as a secondary view from the version bar; it is not the primary place to see history.

## Acceptance criteria
- [ ] Build the FR-T1 example tree entirely from the UI, including drag-and-drop move; reload shows the same tree.
- [ ] Apply Heading 2 style, a custom font/size/color, 1.5 line spacing, a numbered list, insert a 3×3 table, merge two cells, shade a cell → autosaved; reload shows identical content.
- [ ] Paste a fragment from Word with headings, bold and a table — formatting is preserved within the schema.
- [ ] Signed version opens read-only; Sign/New draft/Discard work and the selector updates.
- [ ] Each section shows its change count since the latest signed version; expanding 🕘 shows that node's timeline **inside the section**, with the correct users, and a support-script change appears as "Script · login · ticket" with ⚠.
- [ ] "Compare" on a timeline entry shows track changes inside the section (insert/delete/format), and hovering a change names the author. With two edits by different users since v1, each change is attributed to the right user.
- [ ] *Show changes since v1* marks all changed sections, and shows moved, renamed and deleted sections in their headers or as placeholders.
- [ ] "Restore this text" from an older entry creates a new audited change; restore is not offered on Signed versions or without edit rights.
- [ ] A 2 000-section document scrolls smoothly and loads its first screen in ≤ 3 s (NFR-L6).
- [ ] Node-scoped editor (switch user) can edit only the text of their node(s) and descendants; no structure actions are shown.
- [ ] Using the "Acting as" dropdown: carol signs (1/2), owner edits → carol shown as outdated, carol and dave sign → version becomes v{n}. Owner and editors never see **Sign**.
- [ ] Two browser windows editing the same node → second save shows the conflict dialog.
- [ ] Playwright smoke: owner adds node → types content → grants two approvers → both sign → owner creates new draft → edits a section → that section's inline history shows the edit and track changes vs v1 highlight it.
