# T17 — Web UI: Admin tab (supporting entities)

| | |
|---|---|
| **Depends on** | T05, T14 (T11 for the optional audit viewer) |
| **Size** | S–M (1.5 days) |
| **Requirements** | FR-UI3, FR-N1, FR-N2 |

## Goal
Manage supporting (dictionary) entities on the **Admin** tab (`/admin`, visible to `isAdmin` users).

## Scope
Sub-tabs:

1. **Node types** — grid: Code, Name, Description, Sort order, Active, Usage count. Add / Edit (modal), Activate/Deactivate toggle, Delete (disabled with tooltip when `usageCount > 0`). Validation errors shown on fields.
2. **Users** — read-only grid of seeded users (Login, Display name, Email, Admin, Active) with a note "Users are managed by seed data".
3. **Folders** — the same folder tree component as the main window but with full management actions and folder details (path, document count). *(Reuses T14 component; no new API.)*
4. **Audit log** *(optional, if time allows; needs a small endpoint `GET /api/admin/audit?table&source&from&to&page` in T11 scope)* — raw `audit.ChangeLog` viewer with filters, highlighting `Source = Script`.

## Acceptance criteria
- [ ] Non-admin user does not see the tab; direct navigation to `/admin` shows "Access denied".
- [ ] Create, edit, deactivate a node type; deactivated type is not offered when adding a node in the editor.
- [ ] Delete of a used type is disabled; delete of an unused type works.
- [ ] Users list reflects the seed.
