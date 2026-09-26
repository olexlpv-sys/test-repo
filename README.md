# DocHub

> Working name. Rename freely — the solution/project names in the tasks use `DocHub` as a placeholder.

A system for managing **structured, versioned documents**: a Word-like document is stored as a tree of
nodes of arbitrary depth (chapters → sections → subsections → …), each node holding rich text (including tables).
Documents live in a hierarchy of **virtual folders**, go through a **Draft → Signed** lifecycle with numbered
versions (v1, v2, …), keep a **full change history** (including direct DB changes made by support scripts),
support **version comparison**, **per-document / per-node permissions** and **comments**.

## Tech stack

| Area | Choice |
|---|---|
| Backend | .NET 10 (LTS), ASP.NET Core Web API, EF Core 10 |
| Database | Azure SQL Database (local dev: SQL Server 2022 in Docker) |
| DB schema source of truth | SDK-style SQL Database Project (`Microsoft.Build.Sql`) → DACPAC |
| Web UI | React + TypeScript + Vite, TipTap rich-text editor |
| Tests | xUnit, Testcontainers (MsSql), Respawn, Vitest, Playwright — see [testing strategy](docs/testing-strategy.md) |

## Repository layout (target)

```
/DocHub.slnx                  API solution
/src/DocHub.Api               ASP.NET Core Web API (host, endpoints)
/src/DocHub.Domain            Entities, enums, domain rules (no infrastructure deps)
/src/DocHub.Infrastructure    EF Core DbContext, SQL session context, diff, content renderer
/tests/DocHub.Domain.Tests
/tests/DocHub.Api.Tests       Integration tests against a real SQL Server container
/database/DocHub.Database.slnx
/database/DocHub.Database     SQL Database Project (tables, triggers, seed)
/web                          React SPA
/docs                         Requirements, architecture, development tasks
```

## Getting started

Prerequisites: .NET SDK 10.0.100+ (see `global.json`), Docker (for SQL Server and the integration tests).

```bash
cp .env.example .env               # then set a strong MSSQL_SA_PASSWORD
docker compose up -d               # SQL Server 2022 on localhost:1433, Azurite (blob storage for PDF exports) on :10000
dotnet build DocHub.slnx
dotnet test --solution DocHub.slnx
dotnet run --project src/DocHub.Api   # http://localhost:5080  (/health, /openapi/v1.json, /scalar)
```

Local connection string override (never commit secrets):
```bash
dotnet user-secrets --project src/DocHub.Api set "ConnectionStrings:DocHub" "Server=localhost,1433;Database=DocHub;User Id=sa;Password=<your password>;TrustServerCertificate=True"
```

PDF export (T20) renders with headless Chromium through Playwright and stores files in Azure Blob Storage
(`Export:Storage:ConnectionString`, default `UseDevelopmentStorage=true` = the Azurite container). Chromium comes from
`pwsh src/DocHub.Api/bin/Debug/net10.0/playwright.ps1 install chromium`, or point `Export:Renderer:ChromiumExecutablePath` at an
installed one. Where Chromium can't create its sandbox (a container running as root), set `Export:Renderer:Sandbox=false`.
The export tests use `PLAYWRIGHT_CHROMIUM` (default `/opt/pw-browsers/chromium` when present) and start their own Azurite container.

## Documentation

- [Requirements](docs/requirements/README.md) — one file per area, plus the decisions log
- [Rich-text content format](docs/content-format.md)
- [Development process: reviews, defect rules, testing](docs/process.md) · [Testing strategy](docs/testing-strategy.md)
- [Work queue](docs/work-queue.md) — the single place to pick the next item
- [Architecture & key decisions](docs/architecture.md)
- [Execution plan & task index](docs/tasks/00-execution-plan.md)
