# T05 — Users (read-only) and Node Types dictionary API

| | |
|---|---|
| **Depends on** | T04 |
| **Blocks** | T08, T17 |
| **Size** | S (1 day) |
| **Requirements** | FR-H2, FR-N1, FR-N2 |

## Goal
Expose the seeded users and a fully editable node-type dictionary.

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

## Rules
- `code`: required, `^[A-Z][A-Z0-9_]{1,49}$`, unique (`409` on duplicate). `name`: required, ≤ 100 chars.
- Non-admin writes → `403 forbidden`.
- Response of `GET /{id}` includes `usageCount` (number of nodes using the type) so the UI can explain why delete is disabled.

## Acceptance criteria
- [ ] CRUD happy paths covered by integration tests.
- [ ] Duplicate `code` → `409`; invalid `code` → `400` with field error.
- [ ] Deleting a used type → `409 in-use`; deactivating it succeeds and it disappears from the default list.
- [ ] Non-admin `POST` → `403`.
- [ ] Every write produces an `audit.ChangeLog` row (smoke assertion).
