# T05 — Users (read-only), Node Types and Content Styles dictionaries API

| | |
|---|---|
| **Depends on** | T04 |
| **Blocks** | T08, T09, T17 |
| **Size** | M (1.5–2 days) |
| **Requirements** | FR-H2, FR-N1, FR-N2, FR-T6 |
| **Read first** (nothing else) | [04-change-tracking](../requirements/04-change-tracking.md) · [02-document-tree](../requirements/02-document-tree.md) · [content-format](../content-format.md) · [architecture](../architecture.md) (only sections linked in the text) · [process](../process.md) |

## Goal
Expose the seeded users and the admin-editable dictionaries: node types and the Word-compatible content style catalog.

## API

### Users (read-only — users are seeded)
| Method | Route | Notes |
|---|---|---|
| GET | `/api/users` | all active users `{ id, login, displayName, email, isAdmin }` — used by user switcher and permission dialogs |
| GET | `/api/users/{id}` | |

### Node types
| Method | Route | Who | Notes |
|---|---|---|---|
| GET | `/api/node-types?includeInactive=false` | any | ordered by `sortOrder, name` |
| GET | `/api/node-types/{id}` | any | |
| POST | `/api/node-types` | admin | `{ code, name, description?, sortOrder }` → `201` + `Location` |
| PUT | `/api/node-types/{id}` | admin | `{ code, name, description?, sortOrder, isActive, rowVersion }` |
| DELETE | `/api/node-types/{id}` | admin | `409 in-use` if any node (any version) references it — client should deactivate instead |

### Content styles (Word-compatible style catalog — [content-format.md](../content-format.md) §2)
| Method | Route | Who | Notes |
|---|---|---|---|
| GET | `/api/content-styles?kind=&includeInactive=false` | any | used by the editor's style dropdown |
| GET | `/api/content-styles/stylesheet.css` | anonymous | CSS generated from the catalog (`.ds-style-Heading1 { … }`), cached with ETag |
| POST / PUT / DELETE | `/api/content-styles[/{id}]` | admin | `PropertiesJson` validated against the style property schema; built-in styles cannot be deleted; a style used in any content cannot be deleted (`409 in-use`, check via `ContentJson` `LIKE '%"styleId":"X"%'` or a usage table) — deactivate instead |

## Rules
- `code`: required, `^[A-Z][A-Z0-9_]{1,49}$`, unique (`409` on duplicate). `name`: required, ≤ 100 chars.
- Non-admin writes → `403 forbidden`.
- Response of `GET /{id}` includes `usageCount` (number of nodes using the type) so the UI can explain why delete is disabled.

## Acceptance criteria
- [ ] CRUD happy paths covered by integration tests.
- [ ] Duplicate `code` → `409`; invalid `code` → `400` with field error.
- [ ] Deleting a used type → `409 in-use`; deactivating it succeeds and it disappears from the default list.
- [ ] Non-admin `POST` → `403`.
- [ ] Content styles: CRUD by admin; built-in delete → `409`; `stylesheet.css` reflects a changed `Heading1` font size.
- [ ] Every write produces an `audit.ChangeLog` row (smoke assertion).
