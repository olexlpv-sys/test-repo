# T14 — Web UI: app shell, folder tree, document list

| | |
|---|---|
| **Depends on** | T05, T06, T07 (can start against the OpenAPI contract / mocks after T04) |
| **Blocks** | T15, T16, T17 |
| **Size** | M ([sizing](../process.md#9-sizing-and-agent-effort)) |
| **Requirements** | FR-UI1, FR-UI4, FR-F5, FR-V8 |
| **Read first** (nothing else) | [08-web-ui](../requirements/08-web-ui.md) · [01-folders](../requirements/01-folders.md) · [03-versioning-and-signing](../requirements/03-versioning-and-signing.md) (FR-V8 only) · [architecture](../architecture.md) (only sections linked in the text) · [process](../process.md) |

## Goal
Scaffold the SPA and deliver the main window: folder tree on the left, documents of the selected folder on the right with Add/Delete.

## Scope

### 1. Scaffold (`/web`)
- Vite + React + TypeScript (strict), ESLint + Prettier, Vitest + Testing Library.
- UI kit: pick one and use consistently (recommended: **Mantine** or **Fluent UI v9** — both have tree, table, tabs, modal).
- Routing: React Router — `/` (main), `/documents/:id` (T15), `/admin` (T17).
- Server state: TanStack Query. API client generated from `/openapi/v1.json` with `openapi-typescript` (`npm run gen:api`), thin `openapi-fetch` wrapper that adds `X-User-Id` and maps ProblemDetails to typed errors / toasts.
- Config: `VITE_API_BASE_URL`; Vite dev proxy `/api → https://localhost:<port>` (alternative to CORS).
- CI: add a web job (`npm ci`, lint, test, build).

### 2. App shell
- Header: product name, tabs **Documents** | **Admin** (Admin visible only for `isAdmin`).
- **Test mode "Acting as" dropdown** (FR-UI4): shown only when `GET /api/system/info` returns `authMode = "Test"`, together with a visible **TEST MODE** banner. **Type-ahead** over `GET /api/users?search=` (plus the last 10 used users and the open document's owner/editors/approvers for one-click switching) showing display name + login + role hint for the open document (Owner / Editor / Approver). Selection persisted in `localStorage` (default = first user); switching invalidates all TanStack Query caches so every screen re-renders with the new user's rights. Optional hotkey `Ctrl+Shift+U` to cycle users — handy for the owner → approver 1 → approver 2 signing scenario.
- Global error toast for ProblemDetails (`title` + `detail`); `409 concurrency-conflict` shows "Changed by someone else — reload".

### 3. Main window (`/`)
```
┌──────────────────────┬──────────────────────────────────────────────────────┐
│ Folders   [+] [⋯]    │ General / Contracts                [+ Add] [🗑 Delete] │
│ ▾ General            │ ┌───────────┬────────┬─────────┬───────┬───────────┐ │
│   ▸ Contracts        │ │ Title     │ Status │ Version │ Owner │ Modified  │ │
│   ▸ Policies         │ ├───────────┼────────┼─────────┼───────┼───────────┤ │
│ ▸ Templates          │ │ NDA       │ Draft  │ v2      │ Alice │ 2026-09-… │ │
│                      │ └───────────┴────────┴─────────┴───────┴───────────┘ │
└──────────────────────┴──────────────────────────────────────────────────────┘
```
- **Folder tree** (`GET /api/folders/tree`): expand/collapse, select. Management actions (New sub-folder, Rename inline, Delete with `in-use` handling, drag-and-drop move or "Move to…") are shown **only for admins**; the same component is reused in the Admin tab (T17). Selected folder in URL (`?folder=12`) for deep links.
- **Document list** for the selected folder: columns Title, Status (badge colors: Draft = amber, Signed = green, Deleted = grey), Latest signed version, Owner, Modified; sort by column; toggle "show deleted".
- **Add**: modal with Title → `POST /api/documents` → navigate to `/documents/{id}`.
- **Delete**: enabled when one row is selected and current user is owner; confirm dialog → `DELETE /api/documents/{id}`.
- **Row click** (or double-click, decide) → `/documents/{id}`.
- **Restore**: with "show deleted" on, deleted rows show a **Restore** button (owner or admin) → `POST /api/documents/{id}/restore` (asks for a target folder when the original one is gone — `409 folder-missing`).
- **Move to…**: row action opening a folder picker → `POST /api/documents/{id}/move`; available to the owner for active documents and to admins for any document, **including deleted ones** (needed to empty a folder before deleting it, FR-F4).
- Empty states: no folders, empty folder.

## Acceptance criteria
- [ ] `npm run build` and `npm test` pass in CI; generated API client is up to date (CI check `gen:api` produces no diff).
- [ ] As admin, create/rename/delete folders; tree refreshes without full reload. As non-admin the actions are not shown.
- [ ] Add a document → lands in the editor; back navigation shows it in the list with status Draft.
- [ ] Delete button disabled for non-owners; deleting hides the row.
- [ ] Switching user in the "Acting as" dropdown changes the `X-User-Id` on subsequent requests and refreshes data; the dropdown is hidden when the API is not in test mode.
- [ ] Delete a document, toggle "show deleted", restore it.
- [ ] As admin, move a deleted document out of a folder, then delete the now-empty folder; as owner, move an active document; a non-owner non-admin sees no Move action.
- [ ] Playwright smoke test: create folder → add document → see it in the list.
