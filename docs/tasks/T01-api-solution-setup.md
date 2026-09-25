# T01 — Create the .NET API solution

| | |
|---|---|
| **Depends on** | — |
| **Blocks** | T04 and all API tasks |
| **Size** | S (0.5–1 day) |
| **Requirements** | NFR-1, NFR-2 |
| **Read first** (nothing else) | [09-non-functional](../requirements/09-non-functional.md) · [architecture](../architecture.md) (only sections linked in the text) · [process](../process.md) |

## Goal
Create an empty but runnable .NET 10 API solution with project structure, shared build settings, test projects and CI build.

## Scope

1. Solution `DocHub.slnx` in the repo root (new XML solution format) with:
   | Project | Type | References |
   |---|---|---|
   | `src/DocHub.Api` | `webapi` (ASP.NET Core) | Domain, Infrastructure |
   | `src/DocHub.Domain` | `classlib` | — |
   | `src/DocHub.Infrastructure` | `classlib` | Domain |
   | `tests/DocHub.Domain.Tests` | `xunit` | Domain |
   | `tests/DocHub.Api.Tests` | `xunit` | Api (uses `Microsoft.AspNetCore.Mvc.Testing`) |
   | `tests/DocHub.Testing` | `classlib` | shared test infrastructure: SQL Server Testcontainers fixture, DACPAC deploy, builders (filled by T02/T04) |
   | `tests/DocHub.Database.Tests` | `xunit` | DocHub.Testing (DB-level tests, T02/T03) |
2. `global.json` pinning the .NET 10 SDK (`rollForward: latestFeature`).
3. `Directory.Build.props`: `net10.0`, `Nullable=enable`, `ImplicitUsings=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`.
4. `Directory.Packages.props` — **Central Package Management** for all NuGet versions.
5. `DocHub.Api`:
   - `GET /health` (ASP.NET Core health checks; DB check added in T04).
   - OpenAPI via `Microsoft.AspNetCore.OpenApi`, Scalar UI at `/scalar` in Development.
   - `appsettings.json` with `ConnectionStrings:DocHub` placeholder; local overrides via user-secrets.
   - Structured logging (built-in console JSON formatter is enough).
6. `docker-compose.yml` at repo root: SQL Server 2022 (`mcr.microsoft.com/mssql/server:2022-latest`) on port 1433 with a dev SA password from `.env` (commit `.env.example` only).
7. CI: `.github/workflows/ci.yml` — on every PR and every push: `dotnet restore`, `build`, `test` for `DocHub.slnx` (DB and web jobs are added by T02/T14).
8. Update `README.md` → "Getting started" (prerequisites, `docker compose up -d`, `dotnet run`).

## Out of scope
Database schema (T02), EF Core wiring (T04), any business endpoints.

## Acceptance criteria
- [ ] `dotnet build DocHub.slnx` and `dotnet test --solution DocHub.slnx` succeed with zero warnings (.NET 10 + Microsoft.Testing.Platform requires `--solution`).
- [ ] `dotnet run --project src/DocHub.Api` starts; `GET /health` → `200 Healthy`; `/openapi/v1.json` and `/scalar` are reachable in Development.
- [ ] One placeholder test in each test project passes (API test uses `WebApplicationFactory` to call `/health`).
- [ ] CI workflow runs green on every push and PR.
- [ ] No package versions in individual `.csproj` files.
