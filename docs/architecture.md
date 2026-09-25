# Architecture & key decisions

## 1. High-level view

```mermaid
flowchart LR
  UI[React SPA<br/>/web] -->|REST + JSON<br/>X-User-Id header| API[DocHub.Api<br/>ASP.NET Core .NET 10]
  API -->|EF Core 10<br/>+ SESSION_CONTEXT| DB[(Azure SQL<br/>app.* / audit.*)]
  Support[Support team<br/>SQL scripts] -->|direct T-SQL| DB
  DBProj[DocHub.Database<br/>.sqlproj → DACPAC] -.->|SqlPackage publish| DB
```

## 2. Decisions (ADR-lite)

### ADR-01 Database schema lives in an SDK-style SQL Database Project
- `database/DocHub.Database/DocHub.Database.sqlproj` using the `Microsoft.Build.Sql` SDK, target `SqlAzureV12`.
- Separate solution `database/DocHub.Database.slnx` (builds on any OS with `dotnet build`, produces a DACPAC).
- EF Core is used **database-first-by-hand**: entity configurations are written to match the schema; **no EF migrations**.
- Rationale: triggers, filtered indexes, check constraints, stored procedures and seed data are first-class and reviewable as SQL; deployments are declarative (`SqlPackage /Action:Publish`).

### ADR-02 Tree storage: adjacency list + sort order
- `DocumentNode(ParentNodeId, SortOrder)`; the full tree of a version is loaded with a single query (`WHERE DocumentVersionId = @id`) and assembled in memory (O(n)).
- Subtree operations (delete, permission checks, ancestors) use recursive CTEs or the in-memory tree.
- `hierarchyid` was rejected: harder to use with EF Core and moves require rewriting the subtree anyway.
- Sort order uses gaps (1024, 2048, …); a sibling set is renumbered when a gap is exhausted.

### ADR-03 Versioning by copy, identity by `LogicalNodeId`
- Every version owns a full copy of its nodes and contents. Creating a draft = deep copy in one transaction (set-based `INSERT … SELECT`).
- Each node carries `LogicalNodeId` (GUID), preserved across copies. Comparison, history, node-scoped permissions and comments key off it.
- Rationale: signed versions are immutable and self-contained; comparison is a simple join by `LogicalNodeId`.

### ADR-04 Change tracking in the database (triggers → `audit.ChangeLog`)
- `AFTER INSERT, UPDATE, DELETE` triggers on every tracked table write a row per changed record to `audit.ChangeLog` with old/new values as JSON (`FOR JSON`).
- **Who**: the API sets `SESSION_CONTEXT` keys `UserId` and `CorrelationId` on every opened connection (EF Core `DbConnectionInterceptor`). Triggers read them.
- **Scripts**: if `SESSION_CONTEXT('UserId')` is empty, the change is recorded with `Source = 'Script'`, `DbLogin = ORIGINAL_LOGIN()`. Support scripts should start with `EXEC audit.usp_SetSupportContext @Ticket = 'INC-123', @Reason = '…'` so the ticket is recorded too.
- Rationale: application-level auditing (EF `SaveChanges` interceptor) cannot see script changes — a hard requirement. Temporal tables were considered but do not record *who* changed a row nor the reason; triggers do both. (Temporal tables can be added later for point-in-time queries if needed.)

### ADR-05 Rich text = sanitized HTML
- The editor (TipTap) produces HTML; the API sanitizes it with an allow-list (`HtmlSanitizer`), and stores `ContentHtml`, a derived `PlainText` and a SHA-256 `ContentHash`.
- Diffs are computed server-side (`DiffPlex`) on a normalized block representation (paragraphs, headings, list items, table cells), word-level.

### ADR-06 "Authentication" = seeded users + header
- A development authentication handler reads `X-User-Id` and builds a `ClaimsPrincipal` for a seeded, active user. Missing/unknown → `401`.
- Designed so it can be swapped for Entra ID (JWT bearer) later without touching endpoints: everything uses `ICurrentUser`.

### ADR-07 API style
- Controllers (or Minimal API endpoint groups — pick one in T04 and use consistently), JSON camelCase, `ProblemDetails` for errors,
  `rowVersion` (base64) in DTOs for optimistic concurrency, built-in `Microsoft.AspNetCore.OpenApi` + Scalar UI.
- Layering is intentionally light: `Domain` (entities + rules), `Infrastructure` (EF, SQL, diff, sanitizer), `Api` (endpoints + application services). No MediatR/CQRS.

### ADR-08 Web UI: React SPA (confirmed)
- Vite + React + TypeScript, TanStack Query for server state, a tree component (e.g. `react-arborist`), TipTap with the table extension, TS API client generated from OpenAPI (`openapi-typescript` + `openapi-fetch`).
- In development the API serves CORS for the Vite dev server; in production the SPA can be hosted as static files by the API or by Azure Static Web Apps.

## 3. Standard error codes (ProblemDetails `type`)

| Code | HTTP | When |
|---|---|---|
| `validation-failed` | 400 | Invalid input |
| `unauthenticated` | 401 | Missing/unknown `X-User-Id` |
| `forbidden` | 403 | Role does not allow the operation |
| `not-found` | 404 | Entity missing (or soft-deleted, where applicable) |
| `version-not-editable` | 409 | Modifying a non-Draft version or a deleted document |
| `draft-already-exists` | 409 | Creating a second draft |
| `concurrency-conflict` | 409 | `rowVersion` mismatch |
| `invalid-move` | 409 | Moving a node/folder under itself or its descendant |
| `in-use` | 409 | Deleting a node type/folder that is referenced |
