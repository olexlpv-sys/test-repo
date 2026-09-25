# T17 — Web UI: Admin tab (supporting entities)

| | |
|---|---|
| **Depends on** | T05, T11, T14 |
| **Size** | M (2–2.5 days) |
| **Requirements** | FR-UI3, FR-N1, FR-N2, FR-T6, FR-F5 |
| **Read first** (nothing else) | [08-web-ui](../requirements/08-web-ui.md) · [02-document-tree](../requirements/02-document-tree.md) · [01-folders](../requirements/01-folders.md) · [content-format](../content-format.md) · [architecture](../architecture.md) (only sections linked in the text) · [process](../process.md) |

## Goal
Manage supporting (dictionary) entities on the **Admin** tab (`/admin`, visible to `isAdmin` users).

## Scope
Sub-tabs:

1. **Node types** — grid: Code, Name, Description, Sort order, Active, Usage count. Add / Edit (modal), Activate/Deactivate toggle, Delete (disabled with tooltip when `usageCount > 0`). Validation errors shown on fields.
2. **Content styles** — grid by kind (Paragraph / Character / Table) with a live preview of each style; edit form for font family/size/color, bold/italic, alignment, spacing before/after, line spacing, indents, based-on style, table borders/shading. Built-in styles: editable, not deletable. Changes are immediately visible in the editor (stylesheet refetch).
3. **Users** — read-only grid of seeded users (Login, Display name, Email, Admin, Active) with a note "Users are managed by seed data".
4. **Folders** — the same folder tree component as the main window but with full management actions and folder details (path, document count incl. deleted), plus the folder's document list with **Move to…** for every document incl. deleted ones, so a folder can be emptied and deleted from here. *(Reuses T14 components; no new API.)*
5. **Audit log** (mandatory, FR-UI3) — `GET /api/admin/audit` (T11): grid with filters (table, operation, source, user, DB login, ticket, date range), JSON old/new viewer, `Source = Script` rows highlighted.

## Acceptance criteria
- [ ] Non-admin user does not see the tab; direct navigation to `/admin` shows "Access denied".
- [ ] Create, edit, deactivate a node type; deactivated type is not offered when adding a node in the editor.
- [ ] Delete of a used type is disabled; delete of an unused type works.
- [ ] Users list reflects the seed.
- [ ] Change `Heading1` font size → an open editor shows the new size after refresh.
- [ ] Folder management works here for admins.
- [ ] Audit log shows a support-script change with login and ticket; filtering by `source=Script` works.
