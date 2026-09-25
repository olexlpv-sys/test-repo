# DocHub database

The SQL Database Project `DocHub.Database` (SDK `Microsoft.Build.Sql`, target Azure SQL Database) is the **single source of truth and the only deployment path** for every database object: schemas, tables, indexes, constraints, views, functions, stored procedures, triggers, roles/permissions and seed data (FR-D4). No EF Core migrations, no ad-hoc DDL.

## Layout
| Folder | Content |
|---|---|
| `Schemas/` | `app` (domain), `audit` (change log, T03) |
| `app/Tables/` | tables with their constraints and indexes (one file per table) |
| `app/Views/`, `app/Functions/`, `app/StoredProcedures/` | added by later tasks |
| `Security/` | roles `app_api`, `support_writer`, `readonly` and their grants |
| `Scripts/PostDeployment/` | idempotent seed data (users, content styles, node types, root folders) |

Seed rules: users are seed-managed (upserted, extra users are kept); built-in styles are inserted when missing and never overwritten; node types and root folders are initial data inserted only into empty tables.

## Build
```bash
dotnet build database/DocHub.Database.slnx      # → database/DocHub.Database/bin/Debug/DocHub.Database.dacpac
```
T-SQL warnings are errors. The DACPAC is also built automatically when building `DocHub.slnx` (the test infrastructure references it).

## Publish locally
```bash
dotnet tool restore                              # installs SqlPackage (dotnet-tools.json)
docker compose up -d                             # SQL Server 2022 (see ../.env.example)
dotnet sqlpackage /Action:Publish \
  /SourceFile:database/DocHub.Database/bin/Debug/DocHub.Database.dacpac \
  /TargetConnectionString:"Server=localhost,1433;Database=DocHub;User Id=sa;Password=<password>;TrustServerCertificate=True"
```

Drift check — the report must contain no `<Operation>` elements:
```bash
dotnet sqlpackage /Action:DeployReport /SourceFile:… /TargetConnectionString:… /OutputPath:deploy-report.xml
```

## Tests
`tests/DocHub.Database.Tests` deploys the DACPAC into a SQL Server container (Testcontainers) and checks deployment, seed idempotency, drift and the schema constraints. Requires Docker.
```bash
dotnet test --project tests/DocHub.Database.Tests
```
