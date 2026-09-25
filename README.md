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
| Tests | xUnit, Testcontainers (MsSql), Playwright (UI smoke) |

## Repository layout (target)

```
/DocHub.slnx                  API solution
/src/DocHub.Api               ASP.NET Core Web API (host, endpoints)
/src/DocHub.Domain            Entities, enums, domain rules (no infrastructure deps)
/src/DocHub.Infrastructure    EF Core DbContext, SQL session context, diff, sanitizer
/tests/DocHub.Domain.Tests
/tests/DocHub.Api.Tests       Integration tests against a real SQL Server container
/database/DocHub.Database.slnx
/database/DocHub.Database     SQL Database Project (tables, triggers, seed)
/web                          React SPA
/docs                         Requirements, architecture, development tasks
```

## Documentation

- [Requirements](docs/requirements.md) — consolidated functional requirements, assumptions, open questions
- [Architecture & key decisions](docs/architecture.md)
- [Execution plan & task index](docs/tasks/00-execution-plan.md)
