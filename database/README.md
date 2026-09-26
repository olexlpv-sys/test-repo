# DocHub database

The SQL Database Project `DocHub.Database` (SDK `Microsoft.Build.Sql`, target Azure SQL Database) is the **single source of truth and the only deployment path** for every database object: schemas, tables, indexes, constraints, views, functions, stored procedures, triggers, roles/permissions and seed data (FR-D4). No EF Core migrations, no ad-hoc DDL.

## Layout
| Folder | Content |
|---|---|
| `Schemas/` | `app` (domain), `audit` (change log, T03; tamper evidence, T21), `history` (history tables of the temporal + ledger tables) |
| `app/Triggers/` | **generated** audit triggers — edit `database/tools/GenerateAuditTriggers.cs`, then run `dotnet run database/tools/GenerateAuditTriggers.cs` |
| `audit/` | `ChangeLog` (append-only ledger, partitioned, generated partition function in `audit/Storage/`), `fn_ChangeContext`, `usp_SetSupportContext`, `vSignedVersionTampering`, `vModifiedAfterSigning`; T21: `ReconciliationFinding`/`ReconciliationBaseline` (append-only ledger), **generated** `usp_ReconcileLedger`, `usp_RecordModuleIntegrityFinding` |
| `app/Tables/` | tables with their constraints and indexes (one file per table) |
| `app/Views/`, `app/Functions/`, `app/StoredProcedures/` | added by later tasks |
| `Security/` | roles `app_api`, `support_writer`, `readonly`, `ledger_reader` and their grants |
| `Scripts/PostDeployment/` | idempotent seed data (users, content styles, node types, root folders) and role-in-role membership (`app_api` ∈ `ledger_reader`) |

The generator also writes the ledger **allow-list** (every ledger table and column of the table scripts, embedded in `usp_ReconcileLedger`) and the **module hashes** the API checks (`src/DocHub.Infrastructure/Audit/AuditModuleHashes.g.cs`). Re-run it after any change to a ledger table or an audit module and commit the output with the change (CI and the tests fail otherwise).

Seed rules: users are seed-managed (upserted, extra users are kept); built-in styles are inserted when missing and never overwritten; node types and root folders are initial data inserted only into empty tables.

## Tamper evidence (T21)
The 11 audited `app` tables are **temporal + updatable ledger** tables (history in schema `history`), `app.VersionStamp` is an updatable ledger table, and `audit.ChangeLog`, `audit.ReconciliationFinding` and `audit.ReconciliationBaseline` are **append-only ledger** tables. Nobody — `dbo` and the deployment pipeline included — can switch versioning off or change history and audit rows; `audit.usp_ReconcileLedger` (run nightly by the API, or `POST /api/admin/audit/reconcile`) reports trigger bypasses, forged audit rows, stamp tampering, ledger verification failures and ledger schema changes; the API also checks the audit modules' definitions. Azure-side settings (digest storage, auditing, identities, alerts): [runbook](../docs/runbooks/tamper-evidence-azure.md).

**Upgrade path — recreate only.** An existing regular table can't be switched to ledger, and SqlPackage can't publish the ledger model onto a database whose tables are still regular: databases created before T21 (local compose, test) are **dropped and republished**. There is no production data before go-live.

**Changes of ledger tables later on:**
- Allowed through a normal publish: **additive, nullable (or defaulted) columns**. Every publish path uses `IgnoreColumnOrder=True` (otherwise SqlPackage wants to rebuild the table to keep the column order, which ledger tables forbid): `DacpacDeployer` (tests), the commands below, CI, the runbook. Re-run the generator so the allow-list contains the new column.
- Anything else (type change, drop, rename) = a new table + data copy with `sys.sp_copy_data_in_batches` in a reviewed one-off release step; SQL Server keeps dropped ledger tables/columns (renamed `MSSQL_Dropped…`), and reconciliation reports every drop/rename as *schema tampering*.

**Go-live baseline.** After the initial data load (or the T19 generator's bulk load), insert the baseline once — reconciliation ignores ledger transactions before the first row:
```sql
INSERT INTO audit.ReconciliationBaseline (CreatedAt, Reason) VALUES (SYSUTCDATETIME(), N'go-live');
```

**Local development.** The API must connect as a member of `app_api` for its writes to count as the API's; a local API connecting as `sa` writes `Source = 'App'` rows from a non-`app_api` principal, which reconciliation reports as *forged audit rows* (by design — a `db_owner` posing as the API). Create a local login for the API if you run reconciliation locally:
```sql
CREATE LOGIN dochub_api WITH PASSWORD = N'<password>'; -- in master
CREATE USER dochub_api FOR LOGIN dochub_api; ALTER ROLE app_api ADD MEMBER dochub_api; -- in DocHub
```

## Support scripts
Start from `database/support/_TEMPLATE.sql`: it sets the ticket and reason (`audit.usp_SetSupportContext`) and edits only `ContentJson`. Every change is audited by triggers whether or not the context is set (`Source = 'Script'`, database login).
The template sets `QUOTED_IDENTIFIER ON` and `ANSI_NULLS ON` in its own first batch (before `GO`): `sqlcmd` connects with `QUOTED_IDENTIFIER OFF`, and updates of tables with filtered or computed-column indexes (e.g. `app.NodeContent`) then fail with Msg 1934. Keep that first batch in every support script (or run `sqlcmd -I`).

## Build
```bash
dotnet build database/DocHub.Database.slnx      # → database/DocHub.Database/bin/Debug/DocHub.Database.dacpac
```
T-SQL warnings are errors. The DACPAC is also built automatically when building `DocHub.slnx` (the test infrastructure references it).

## Publish locally
```bash
dotnet tool restore                              # installs SqlPackage (dotnet-tools.json)
docker compose up -d                             # SQL Server 2022 (see ../.env.example)
dotnet sqlpackage /Action:Publish /p:IgnoreColumnOrder=True \
  /SourceFile:database/DocHub.Database/bin/Debug/DocHub.Database.dacpac \
  /TargetConnectionString:"Server=localhost,1433;Database=DocHub;User Id=sa;Password=<password>;TrustServerCertificate=True"
```

Drift check — a redeploy script must contain no DDL (the deploy *report* can't be used: DacFx lists every ledger table as an empty "Alter" on each redeploy):
```bash
dotnet sqlpackage /Action:Script /p:IgnoreColumnOrder=True /SourceFile:… /TargetConnectionString:… /OutputPath:redeploy.sql
sed '/^-- <post-deployment>/,$d' redeploy.sql | grep -Ei '^\s*(CREATE|ALTER|DROP)\s|sp_rename'   # must print nothing (the post-deployment part runs on every deploy)
```

## Tests
`tests/DocHub.Database.Tests` deploys the DACPAC into a SQL Server container (Testcontainers) and checks deployment, seed idempotency, drift, the schema constraints, the audit triggers, ledger reconciliation (with real logins and committed data) and the deploy-script guard (pre/post-deployment scripts may only change the seed tables — T21 §3). Requires Docker.
```bash
dotnet test --project tests/DocHub.Database.Tests
```
