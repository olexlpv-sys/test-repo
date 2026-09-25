# T21 — Tamper-evident audit: temporal + ledger tables, deploy-script guard, reconciliation

| | |
|---|---|
| **Depends on** | T03 (audit), T04 (API foundation) |
| **Blocks** | T11 (history can use `FOR SYSTEM_TIME AS OF`), T17 (Admin → Audit shows findings) |
| **Size** | L ([sizing](../process.md#9-sizing-and-agent-effort)) |
| **Requirements** | FR-H6, FR-H1, FR-H3, FR-H5, FR-D4, decisions log Q18 |
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
- All 11 audited `app` tables (T03 §2) become **temporal and updatable ledger tables**: hidden period columns `ValidFrom` / `ValidTo` (`GENERATED ALWAYS AS ROW START/END HIDDEN`, `datetime2(7)`), `PERIOD FOR SYSTEM_TIME`, `WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [history].[<Table>]), LEDGER = ON)`; history tables in a new schema `history`.
- `app.VersionStamp` becomes an **updatable ledger** table (not temporal): nobody can silently clear `TamperedAt`; changes to it outside the triggers are found by reconciliation (§4, rule 3). `app.ContentStyleUsage` (derived, API-maintained) stays a regular table.
- Gives: `FOR SYSTEM_TIME AS OF` (T11 "view as of" may use it), the ledger views `<Table>_Ledger` (every change with its ledger transaction), `sys.database_ledger_transactions` (principal of every transaction); `SYSTEM_VERSIONING` can't be switched off and history rows can't be updated/deleted — by anyone, `sa` included (verified on SQL Server 2022, together with triggers, `IDENTITY`, `ON DELETE CASCADE` into ledger tables, partitioning + page compression).
- EF Core maps only the visible columns; hidden period/ledger columns are never mapped (the T04 drift test ignores hidden columns).
- **Upgrade path (verified limitation):** an existing regular table can't be switched to ledger (`ALTER TABLE … SET (LEDGER = ON)` doesn't exist) and SqlPackage fails when publishing the ledger model onto a database whose tables are still regular. T21 is applied **before any production data exists**: every existing database (local compose, test) is **recreated** (drop + publish). `database/README.md` documents this, and the rules for later changes of ledger tables: only additive, nullable (or defaulted) columns via a normal publish; anything else (type change, drop, rename) = a new table + data copy with `sys.sp_copy_data_in_batches` in a reviewed one-off release step (dropped ledger tables/columns are kept by SQL Server, renamed).

### 2. Append-only ledger change log
- `audit.ChangeLog` becomes `LEDGER = ON (APPEND_ONLY = ON)` (keeps monthly partitioning and page compression — verified). Nobody, `dbo` included, can update or delete audit rows.
- No application-written transaction id: `CURRENT_TRANSACTION_ID()` is **not** the ledger transaction id, and any column the triggers write is also writable by `dbo`. Correlation uses the ledger's own hidden `ledger_start_transaction_id` of the `ChangeLog` row (written by SQL Server, immutable) against the audited table's `ledger_transaction_id` in its `<Table>_Ledger` view.
- New append-only ledger tables: `audit.ReconciliationFinding` (findings, §4) and `audit.ReconciliationBaseline` (`Id`, `CreatedAt`, `Reason`): reconciliation ignores ledger transactions older than the **first** baseline row (append-only, so the first baseline can't be moved; a later insert changes nothing). Written once at go-live (runbook) or by the T19 data generator after its bulk load. Append-only does **not** stop `dbo` from dropping and re-creating the table, so the baseline and findings tables are resolved **by object identity, not by name**: every object that `sys.ledger_table_history` shows as ever having been `audit.ReconciliationBaseline` (resp. `audit.ReconciliationFinding`) — current, renamed, schema-transferred or dropped (`MSSQL_DroppedLedgerTable_*`) — is read; the effective baseline is the earliest row across all of them. Any such drop/rename/transfer is itself a finding (rule 5).
- The database option **`ALLOW_SNAPSHOT_ISOLATION = ON`** is set in the DB project (`<AllowSnapshotIsolation>True</AllowSnapshotIsolation>`) — required by `sp_verify_database_ledger` (Msg 37498 otherwise) and covered by the drift check.

### 3. Deploy-script guard (repository side)
A DB test (`DeployScriptGuardTests`) parses every script under `Scripts/PreDeployment/` and `Scripts/PostDeployment/` with `Microsoft.SqlServer.TransactSql.ScriptDom` and fails on: DML against anything except the seed tables (`app.User`, `app.NodeType`, `app.ContentStyle`, `app.Folder`); any statement touching `audit.*` or `history.*` (except the documented baseline insert in a go-live script, if ever added); `DISABLE TRIGGER`, `ALTER TABLE … NOCHECK`, `SYSTEM_VERSIONING`/`LEDGER` options; `sp_set_session_context`; dynamic SQL (`EXEC(@sql)`, `sp_executesql`). Pre/post-deploy scripts that could forge data therefore can't be merged unnoticed (they still need review, process §2).

### 4. Reconciliation
**`audit.usp_ReconcileLedger @From datetime2, @To datetime2`** (DB project, executed with the grants below) compares, per audited table, the ledger history with `audit.ChangeLog`, **using the same change rules as the triggers** (T03): the before/after images of a ledger transaction (the `DELETE`/`INSERT` pair of `<Table>_Ledger` for one key) are compared on the audited columns only, strings as `VARBINARY`; derived-only `NodeContent` changes count only when the transaction principal is **not** an `app_api` member.
1. **Trigger bypass** — a ledger change that the trigger rules would have audited (insert, delete, or an audited column changed) with no `ChangeLog` row for the same table + entity whose `ledger_start_transaction_id` equals the change's ledger transaction. No-op updates, API derived-only rebuilds (refresher) and anything before the baseline are **not** findings.
2. **Forged audit row** — a `ChangeLog` row whose ledger transaction didn't change the referenced table + entity (inserted outside the triggers), or a `Source = 'App'` row whose transaction principal is not an `app_api` member, or a `Source = 'Script'` row whose `DbLogin` is not the transaction principal. **Exception:** reconciliation's own rows (`TableName = 'audit.ReconciliationFinding'`, `Operation = 'I'`, `EntityId` = a finding inserted in the same ledger transaction) are not forged.
3. **Stamp tampering** — a `VersionStamp` ledger change in a transaction without an audited change of that version **and** without a finding for that version inserted in the same transaction (reconciliation's own stamp updates are not tampering).
4. **Ledger integrity** — `sys.sp_verify_database_ledger_from_digest_storage` where automatic digest storage is configured (Azure); locally/tests `sys.sp_verify_database_ledger` with a digest taken by the test.
5. **Ledger schema tampering** — evaluated over **all time** (the history is small) on `sys.ledger_table_history` and `sys.ledger_column_history` for audited tables, `app.VersionStamp` and all `audit.*` ledger tables, **by object id** (an object keeps its id through renames/transfers):
   - `DROP`, `RENAME`, `SCHEMA_TRANSFER` of a table and `DROP`/`RENAME` of a column are **always** findings (no legitimate deployment does them — see the §1 change rules);
   - `CREATE` of a table and `ADD` of a column are findings **unless** the table/column is in the **ledger allow-list** — the list of ledger tables and columns of the DACPAC model, generated at build time from the DB project (`dotnet run database/tools/GenerateAuditTriggers.cs` also emits it) and embedded as a `VALUES` list in `usp_ReconcileLedger`; a legitimate additive schema change updates the allow-list in the same reviewed commit.
   - Findings of every object that ever was `audit.ReconciliationFinding` (see §2) are re-imported into the current table (dynamic SQL inside the procedure; the deploy-script guard applies to deploy scripts only), so findings can't be made to vanish.
6. **Module integrity** — the API compares the SHA-256 of the definitions (`sys.sql_modules`) of `usp_ReconcileLedger`, `fn_ChangeContext` and the audit triggers with hashes generated at build time from the DACPAC sources (embedded in the API assembly). A mismatch — e.g. a `dbo` rewrote the reconciliation procedure or a trigger to hide changes — is a finding raised by the API itself (stored the same way). Changes to the procedure by a privileged principal are also recorded outside the database by Azure SQL Auditing (§5).

**Principal resolution:** `sys.database_ledger_transactions.principal_name` holds the **login / external principal name**, not the database user (and `EXECUTE AS USER` still records the original login). Membership is resolved by SID: `SUSER_SID(principal_name)` → `sys.database_principals.sid` → `sys.database_role_members`; `DbLogin` vs principal is compared the same way (SID, case-insensitive name as fallback). On Azure the users are created `FROM EXTERNAL PROVIDER` (Entra) and the runbook verifies the mapping with a test write. **Tests must connect with real logins** (not `EXECUTE AS USER`) for everything reconciliation evaluates.

Effects of a finding (in the same transaction; a finding is unique by kind + table + entity + ledger transaction, so a re-run adds nothing): insert into `audit.ReconciliationFinding` (kind, table, entity, document, version, ledger transaction, principal, detected-at); for every affected document version: if the version is **Signed and the finding's ledger transaction committed after `SignedAt`**, set `VersionStamp.TamperedAt` (if null); in any case **advance `VersionStamp.LastChangeLogId`** (so cached trees/contents/ETags are invalidated, NFR-L9) to the id of a `ChangeLog` row the procedure writes: `TableName = 'audit.ReconciliationFinding'`, `Operation = 'I'`, `EntityId` = finding id, `DocumentVersionId` = the version, `Source = 'App'`, `OperationContext = 'Reconciliation'`; for `NodeContent` bypasses set `DerivedStale = 1` (forged `ContentHtml`/`PlainText` get re-rendered, T09 rule 8). The FR-H5 flag "modified after signing" of a **Signed** version = `TamperedAt IS NOT NULL` **or** a finding exists for the version **whose ledger transaction committed after `SignedAt`** (index `ReconciliationFinding (DocumentVersionId)`; the finding stores the transaction commit time), so even a cleared stamp keeps the flag; findings on Drafts (and pre-signing findings of a later signed version) are shown in Admin → Audit only.

**Grants (`Security/`):** reading `sys.database_ledger_transactions` and verifying the ledger require database permissions that ownership chaining doesn't cover. Role **`ledger_reader`** gets `VIEW LEDGER CONTENT` and `VIEW DATABASE STATE`; `app_api` is a member (read-only permissions; the API runs reconciliation). The DB tests run reconciliation as an `app_api` user.

**Scheduling:** API hosted service `LedgerReconciliationService`, nightly, as the `system` user, window = last 48 h (overlap; idempotent), plus `POST /api/admin/audit/reconcile` (admin) and `GET /api/admin/audit/findings` (admin, paged) — shown in Admin → Audit (T17).

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
Retention/purging of audit and history data (ledger data is kept by design). A data-preserving migration of existing non-empty databases (none exist before go-live; see §1 upgrade path). Implementing the Azure infrastructure itself (human action, process §10).

## Acceptance criteria (automated in `tests/DocHub.Database.Tests` unless stated)
- [ ] A fresh DACPAC publish creates the temporal + ledger tables; re-deploy shows no drift; `FOR SYSTEM_TIME AS OF` returns the earlier state of a `NodeContent` row. `database/README.md` documents the recreate-only upgrade path and the allowed changes of ledger tables.
- [ ] As `dbo`: `SET (SYSTEM_VERSIONING = OFF)`, `UPDATE`/`DELETE` on a history table and on `audit.ChangeLog`/`ReconciliationFinding`/`ReconciliationBaseline` all fail.
- [ ] **Trigger bypass is detected:** as `dbo`, disable `TR_NodeContent_Audit`, update a signed version's `ContentJson`, re-enable → reconciliation (run as an `app_api` user) reports a trigger bypass for that node + transaction; the version gets `TamperedAt`, its `LastChangeLogId` advances and the node is `DerivedStale`.
- [ ] **Forged audit row is detected:** as `dbo`, `INSERT` a `ChangeLog` row with `Source = 'App'`, `UserId = 2` → *forged audit row*.
- [ ] **Stamp tampering is detected:** as `dbo`, clear `TamperedAt` of a tampered version → *stamp tampering*; the version is still flagged "modified after signing" (finding-based).
- [ ] **No false positives:** normal API writes (as `app_api`, incl. an API content save and a derived-only refresher update), support-script changes (`support_writer`, with and without context), no-op updates, and bulk changes before the baseline produce **no** findings.
- [ ] Reconciliation is idempotent: run, then run again over the same window → 0 new findings and no `VersionStamp` change in the second run (its own ChangeLog/stamp writes are not findings).
- [ ] **Schema tampering is detected:** as `dbo`, drop and re-create `audit.ReconciliationBaseline` with a later "first" row → a *ledger schema tampering* finding, and the effective baseline is still the original one; drop and re-create `audit.ReconciliationFinding` → finding, and earlier findings are re-imported.
- [ ] An API write through a SQL login whose name differs from its database user (member of `app_api`) produces **no** finding; the same write through a login that is not an `app_api` member but claims `Source = 'App'` via a forged row is found.
- [ ] Reconciliation including `sp_verify_database_ledger` succeeds on a fresh publish (snapshot isolation enabled by the DACPAC).
- [ ] **Rename/transfer is detected:** as `dbo`, `sp_rename` `audit.ReconciliationBaseline`, transfer it to another schema and create a new `audit.ReconciliationBaseline` with a later "first" row → *ledger schema tampering* finding; the effective baseline is still the original row. The same for `audit.ReconciliationFinding` → earlier findings are re-imported.
- [ ] **Allow-list:** publishing a DACPAC that adds a nullable column (allow-list updated in the same change) → no finding; a manual `ALTER TABLE … ADD` of a column not in the allow-list → finding; a manual column `DROP` → finding.
- [ ] **Module integrity:** as `dbo`, `ALTER PROCEDURE audit.usp_ReconcileLedger` (or a trigger) → the API's next reconciliation run reports a *module integrity* finding.
- [ ] **Draft findings don't flag signing:** a finding on a Draft, then the draft is signed → `modifiedAfterSigning = false`; a later tampering finding → `true`.
- [ ] `sp_verify_database_ledger` passes on an untouched database with the stored digest (as an `app_api` login via `ledger_reader`).
- [ ] Deploy-script guard: fixtures with each forbidden statement fail; the real seed scripts pass.
- [ ] API (`tests/DocHub.Api.Tests`, API connecting as `app_api`): `POST /api/admin/audit/reconcile` succeeds and `GET /api/admin/audit/findings` lists a finding created in the DB; both are admin-only in the authorization matrix.
- [ ] The runbook exists and names every Azure setting of §5 with the exact portal/CLI steps.
