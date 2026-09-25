# T04 — API foundation: EF Core, current user, errors, test infrastructure

| | |
|---|---|
| **Depends on** | T01, T02, T03 |
| **Blocks** | T05–T13 |
| **Size** | M (2–3 days) |
| **Requirements** | FR-H2, NFR-2, NFR-3, NFR-4, NFR-7 |
| **Read first** (nothing else) | [04-change-tracking](../requirements/04-change-tracking.md) · [09-non-functional](../requirements/09-non-functional.md) · [architecture](../architecture.md) (only sections linked in the text) · [process](../process.md) |

## Goal
Cross-cutting plumbing every feature task relies on, so feature tasks only add endpoints and rules.

## Scope

### 1. Domain & EF Core mapping
- Entities in `DocHub.Domain` for all `app.*` tables from T02; enums `VersionStatus { Draft=1, Signed=2, Deleted=3 }`, `DocumentRole { Editor=1, Approver=2 }`.
- `DocHubDbContext` in `DocHub.Infrastructure` (`Microsoft.EntityFrameworkCore.SqlServer`, `UseAzureSql` / `UseSqlServer`), one `IEntityTypeConfiguration<>` per entity, exact table/column/schema names, `RowVersion` as concurrency token (`IsRowVersion()`).
- Tables with triggers: configure `ToTable(t => t.HasTrigger("…"))` (required by EF Core so it does not use `OUTPUT` without `INTO`).
- **No migrations.** A test asserts the model matches the DACPAC (see §5).
- `EnableRetryOnFailure` for Azure SQL transient errors.

### 2. Current user — **Test mode** ([ADR-06](../architecture.md))
- `TestModeAuthenticationHandler` (active when `Auth:Mode = Test`): reads `X-User-Id`, loads the user (cached), rejects missing/unknown/inactive users with `401`.
- `GET /api/system/info` (anonymous) → `{ authMode, environment, version }` — the SPA uses it to show the "Acting as" dropdown.
- Guard: startup fails for `Test` mode in `Production` unless `Auth:AllowTestModeInProduction = true`.
- `ICurrentUser { int UserId; bool IsAdmin; }` — the only way endpoints learn who is calling.
- `GET /api/me` → current user.
- OpenAPI: declare the `X-User-Id` security scheme so Scalar can send it.

### 3. SQL session context
- `SessionContextConnectionInterceptor : DbConnectionInterceptor` — on `ConnectionOpened(Async)` execute
  `EXEC sp_set_session_context N'UserId', @uid; EXEC sp_set_session_context N'CorrelationId', @cid;`
  using `ICurrentUser` and `HttpContext.TraceIdentifier`. (Pooled connections are reset by `sp_reset_connection`, which clears session context.)
- Integration test proves an API change lands in `audit.ChangeLog` with the right `UserId` and `CorrelationId`.

### 4. Errors, validation, conventions
- `ProblemDetails` everywhere (`AddProblemDetails`, `IExceptionHandler`); domain exceptions map to the codes in [architecture §3](../architecture.md#3-standard-error-codes-problemdetails-type).
- `DbUpdateConcurrencyException` → `409 concurrency-conflict`. Unique-index violations (SQL 2601/2627) → `409` with a meaningful `type`.
- Validation: FluentValidation **or** DataAnnotations + endpoint filter (pick one), → `400 validation-failed` with field errors.
- JSON: camelCase, enums as strings, `rowVersion` serialized as base64 string.
- Pick **Controllers or Minimal API endpoint groups** and document the choice in `architecture.md` (ADR-07).
- Paging convention for list endpoints: `?page=1&pageSize=50` → `{ items, page, pageSize, totalCount }`.
- CORS policy for the Vite dev server (`http://localhost:5173`), configurable.
- DB health check added to `/health`.

### 5. Test infrastructure (`DocHub.Api.Tests`)
- `DocHubApiFactory : WebApplicationFactory<Program>` reusing the SQL container fixture from `tests/DocHub.Testing` (T02).
- Isolation: **Respawn** reset of `app` tables (except seed) between tests, or one database per test class.
- Helpers: `client.AsUser(2)` sets `X-User-Id`; builders for folders/documents/nodes.
- Test: EF model ↔ DB schema check (compare `IModel` tables/columns with `INFORMATION_SCHEMA.COLUMNS`).
- CI runs these tests (Docker available on `ubuntu-latest`).
- Authorization-matrix test harness (endpoint × role → expected status) that later tasks extend — see [testing strategy](../testing-strategy.md).
- OpenAPI snapshot test (Verify) + committed `src/DocHub.Api/openapi.v1.json`.

## Acceptance criteria
- [ ] `GET /api/me` with `X-User-Id: 2` → alice; without header → `401` ProblemDetails.
- [ ] An integration test updates a row through `DbContext` inside a request and finds a matching `audit.ChangeLog` row with `Source='App'`, `UserId`, `CorrelationId`.
- [ ] Concurrency conflict returns `409 concurrency-conflict`.
- [ ] Schema-drift test passes and fails when a column is renamed in the DB project.
- [ ] Integration tests run green in CI.
