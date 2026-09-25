# T14 — Web UI: app shell, folder tree, document list

| | |
|---|---|
| **Depends on** | T06, T07 (can start against the OpenAPI contract / mocks after T04) |
| **Blocks** | T15, T16, T17 |
| **Size** | M (2–3 days) |
| **Requirements** | FR-UI1, FR-UI4 |

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
- Header: product name, tabs **Documents** | **Admin** (Admin visible only for `isAdmin`), **current-user switcher** (dropdown of `GET /api/users`, persisted in `localStorage`, default = first user).
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
- **Folder tree** (`GET /api/folders/tree`): expand/collapse, select; context menu / toolbar: New sub-folder, Rename (inline), Delete (confirm; show `in-use` error nicely), drag-and-drop move (optional; otherwise "Move to…" dialog). Selected folder in URL (`?folder=12`) for deep links.
- **Document list** for the selected folder: columns Title, Status (badge colors: Draft = amber, Signed = green, Deleted = grey), Latest signed version, Owner, Modified; sort by column; toggle "show deleted".
- **Add**: modal with Title → `POST /api/documents` → navigate to `/documents/{id}`.
- **Delete**: enabled when one row is selected and current user is owner; confirm dialog → `DELETE /api/documents/{id}`.
- **Row click** (or double-click, decide) → `/documents/{id}`.
- Empty states: no folders, empty folder.

## Acceptance criteria
- [ ] `npm run build` and `npm test` pass in CI; generated API client is up to date (CI check `gen:api` produces no diff).
- [ ] Create/rename/delete folders from the UI; tree refreshes without full reload.
- [ ] Add a document → lands in the editor; back navigation shows it in the list with status Draft.
- [ ] Delete button disabled for non-owners; deleting hides the row.
- [ ] Switching user changes the `X-User-Id` on subsequent requests and refreshes data.
- [ ] Playwright smoke test: create folder → add document → see it in the list.
