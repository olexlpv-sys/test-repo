# T21 — Tamper-evident audit: temporal + ledger tables, deploy-script guard, reconciliation

| | |
|---|---|
| **Depends on** | T03 (audit), T04 (API foundation) |
| **Blocks** | T11 (history can use `FOR SYSTEM_TIME AS OF`), T17 (Admin → Audit shows findings) |
| **Size** | L ([sizing](../process.md#9-sizing-and-agent-effort)) |
| **Requirements** | FR-H1, FR-H3, FR-H5, FR-D4, decisions log Q18 |
| **Read first** (nothing else) | [04-change-tracking](../requirements/04-change-tracking.md) · [T03](T03-database-change-tracking.md) §1–§3 · [11-data-access](../requirements/11-data-access.md) (FR-D4) · [process](../process.md) |

## Goal
The T03 triggers record *who and why*, but a **privileged principal** — a DBA, `dbo`, or the deployment pipeline running
pre/post-deployment scripts — can disable a trigger, change data and re-enable it, or insert forged audit rows. Nothing
inside the database can *prevent* that. This task makes every such change **evident**:
- the evidence can't be rewritten from inside the database;
- the anchors (digests, audit logs) live outside the reach of the pipeline and the DBA;
- a reconciliation job reports the gaps.

## Threat model
| Principal | Can | After T21 |
|---|---|---|
| `support_writer` | DML on `app` | Already fully audited (T03); can't disable triggers (no `ALTER`) or change `audit.*` |
| `app_api` | DML on `app`, EXECUTE | Audited as `App` |
| DBA / `dbo` / deployment pipeline (pre/post-deploy scripts) | anything in the database | Changes stay visible in immutable ledger history with the principal and transaction. Forged or missing audit rows are found by reconciliation. Rewriting history, restoring an old backup or re-creating the database breaks the ledger digests, which are stored outside the database. |

## Scope

### 1. System-versioned (temporal) + updatable ledger tables
- All 11 audited `app` tables (T03 §2) become **temporal and updatable ledger tables** in the DB project:
  - hidden period columns `ValidFrom` / `ValidTo` (`GENERATED ALWAYS AS ROW START/END HIDDEN`, `datetime2(7)`);
  - `PERIOD FOR SYSTEM_TIME`;
  - `WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [history].[<Table>]), LEDGER = ON)`;
  - history tables in a new schema `history`.
- **What this gives:**
  - `FOR SYSTEM_TIME AS OF` point-in-time queries (T11 "view as of" can use them);
  - the ledger view `<Table>_Ledger` shows every change with its ledger transaction;
  - `sys.database_ledger_transactions` records the principal of each transaction;
  - `SYSTEM_VERSIONING` can't be turned off and history rows can't be updated or deleted — by anyone, `sa` included (verified on SQL Server 2022).
- EF Core keeps mapping only the visible columns; the hidden period and ledger columns are not mapped. The schema-drift test (T04) ignores hidden columns (`sys.columns.is_hidden`).
- `app.VersionStamp` and `app.ContentStyleUsage` (derived data) stay regular tables.

### 2. Append-only ledger change log
- `audit.ChangeLog` becomes `LEDGER = ON (APPEND_ONLY = ON)`: nobody, `dbo` included, can update or delete audit rows.
- New column `TransactionId bigint` = `CURRENT_TRANSACTION_ID()` written by the triggers (generator change). It links every audit row to its ledger transaction.
- Verify that monthly partitioning and page compression are kept on a ledger table. If partitioning is not supported, drop it and keep compression; record the decision here.

### 3. Deploy-script guard (repository side)
A DB test (`DeployScriptGuardTests`) parses every script under `Scripts/PreDeployment/` and `Scripts/PostDeployment/` with the T-SQL parser (`Microsoft.SqlServer.TransactSql.ScriptDom`) and fails on any of:
- DML against anything except the seed tables (`app.User`, `app.NodeType`, `app.ContentStyle`, `app.Folder`);
- any statement touching `audit.*` or `history.*`;
- `DISABLE TRIGGER`, `ALTER TABLE … NOCHECK`, `SET (SYSTEM_VERSIONING …)` or `LEDGER` options;
- `sp_set_session_context`;
- dynamic SQL (`EXEC(@sql)`, `sp_executesql`).

So a pre/post-deploy script that could forge data can't be merged unnoticed. It still needs review (process §2), and the guard is part of the test suite.

### 4. Reconciliation
- **`audit.usp_ReconcileLedger @From datetime2, @To datetime2`** (DB project) finds:
  1. **Trigger bypass:** ledger transactions that changed an audited table without a matching `audit.ChangeLog` row for the same table, entity and `TransactionId`.
  2. **Forged attribution:** `ChangeLog` rows with `Source = 'App'` whose ledger transaction principal is not a member of `app_api`. Also `ChangeLog` rows whose `TransactionId` has no ledger transaction, i.e. they were inserted outside the triggers.
  3. **Ledger integrity:** the result of `sys.sp_verify_database_ledger_from_digest_storage`, where digest storage is configured (Azure). Local and test runs use `sys.sp_verify_database_ledger` with a digest taken by the test.
- Findings go to **`audit.ReconciliationFinding`**, itself an append-only ledger table: kind, table, entity, version, transaction, principal, detected-at. Every document version touched by a finding gets `VersionStamp.TamperedAt` set, so "modified after signing" (FR-H5) and the tampering view pick it up.
- **Scheduling:** an API hosted service `LedgerReconciliationService` runs nightly as the `system` user, covering the last 48 h with overlap. Admins can trigger it via `POST /api/admin/audit/reconcile` and list findings via `GET /api/admin/audit/findings` (T17 shows them in Admin → Audit).

### 5. Runbook for the product owner (Azure side, outside the code)
Write `docs/runbooks/tamper-evidence-azure.md` with exact steps:
- **Automatic digest storage** to a *separate* storage account with an immutable, time-based, **locked** retention policy. Only the security team has RBAC on it; the pipeline and DBAs don't.
- **Azure SQL Auditing** to Log Analytics and immutable storage, with these groups: `BATCH_COMPLETED_GROUP` for the deployment principal, `SCHEMA_OBJECT_CHANGE_GROUP`, `DATABASE_OBJECT_CHANGE_GROUP`, `DATABASE_PRINCIPAL_CHANGE_GROUP`, `DATABASE_ROLE_MEMBER_CHANGE_GROUP`. The pipeline can't change the audit settings.
- **Identities:**
  - API: managed identity → `app_api`;
  - support: Entra group → `support_writer`;
  - pipeline: a dedicated service principal with `db_owner`, used only during deployments, and with no access to digest storage, audit logs or Log Analytics.
- **Alerts:** reconciliation findings and ledger verification failures → security mailbox.

## Out of scope
Retention or purging of audit and history data: ledger data is kept by design. Implementing the Azure infrastructure itself (a human action, see process §10).

## Acceptance criteria (automated in `tests/DocHub.Database.Tests` unless stated)
- [ ] DACPAC deploys the temporal + ledger tables. Re-deploy shows no drift. `FOR SYSTEM_TIME AS OF` returns the earlier state of a `NodeContent` row.
- [ ] As `dbo`: `ALTER TABLE … SET (SYSTEM_VERSIONING = OFF)`, `UPDATE`/`DELETE` on a history table and `UPDATE`/`DELETE` on `audit.ChangeLog` all fail.
- [ ] **Trigger bypass is detected:** as `dbo`, disable `TR_NodeContent_Audit`, update a signed version's content and re-enable the trigger. Reconciliation reports a *trigger bypass* for that node and transaction, and the version gets `TamperedAt`.
- [ ] **Forged audit row is detected:** as `dbo`, `INSERT` a `ChangeLog` row with `Source = 'App'`, `UserId = 2`. Reconciliation reports *forged attribution*.
- [ ] Normal API and support-script changes produce **no** findings.
- [ ] Ledger verification with a digest taken before a manual page-level change is out of scope for tests. The test verifies that `sp_verify_database_ledger` passes on an untouched database with the stored digest.
- [ ] Deploy-script guard: fixtures with a forbidden statement fail the guard, and the real seed scripts pass.
- [ ] API (in `tests/DocHub.Api.Tests`): reconcile and findings endpoints are admin-only (authorization matrix). A finding created in the DB is listed.
- [ ] The runbook exists and names every Azure setting above with the exact portal/CLI steps.
