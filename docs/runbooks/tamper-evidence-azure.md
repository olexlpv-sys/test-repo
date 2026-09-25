# Runbook — tamper evidence on Azure (T21 §5)

Audience: the product owner / security team (process §10 — human action, outside the code). The database side (ledger
tables, reconciliation, module check) ships with the DACPAC and the API; this runbook sets up what must live **outside the
reach of the deployment pipeline and the DBAs**: the ledger digests, the audit logs, the identities and the alerts.

Placeholders: `<rg>` resource group of the SQL server, `<rg-sec>` resource group owned by the security team, `<server>`
SQL logical server, `DocHub` database, `<digest-sa>` / `<audit-sa>` storage accounts, `<law>` Log Analytics workspace,
`<pipeline-sp>` deployment service principal, `<api-mi>` the API's managed identity, `<support-group>` Entra group of the
support team, `security@<company>` the security mailbox. Commands: Azure CLI ≥ 2.60, signed in as a security-team member.

## 0. Ownership and RBAC (do first)
1. Create `<rg-sec>` for `<digest-sa>`, `<audit-sa>` and `<law>`. Only the security team gets roles on it
   (Portal: *Resource group → Access control (IAM) → Add role assignment*; `Owner` for the security group, nothing for
   the pipeline or DBAs).
2. The pipeline and DBAs get **no Azure RBAC role on `<server>`** (not *Contributor*, not *SQL Security Manager*, not
   *SQL Server Contributor*) — they reach the database only as database users. Changing auditing, digest storage or the
   server identity requires such a role, so they can't switch the evidence off.
   Check: `az role assignment list --scope $(az sql server show -g <rg> -n <server> --query id -o tsv) -o table`.
3. Put a delete lock on `<rg-sec>`: `az lock create -g <rg-sec> -n no-delete --lock-type CanNotDelete`.

## 1. Automatic digest storage (ledger digests outside the database)
```bash
# Storage account for digests (security team only).
az storage account create -g <rg-sec> -n <digest-sa> --sku Standard_ZRS --kind StorageV2 \
  --min-tls-version TLS1_2 --allow-blob-public-access false --allow-shared-key-access false
az storage container create --account-name <digest-sa> -n sqldbledgerdigests --auth-mode login

# Time-based immutability, append writes allowed (digests are appended), then LOCK it (irreversible).
az storage container immutability-policy create --account-name <digest-sa> -c sqldbledgerdigests \
  --period 3650 --allow-protected-append-writes true
ETAG=$(az storage container immutability-policy show --account-name <digest-sa> -c sqldbledgerdigests --query etag -o tsv)
az storage container immutability-policy lock --account-name <digest-sa> -c sqldbledgerdigests --if-match "$ETAG"

# The SQL server writes digests with its managed identity.
az sql server update -g <rg> -n <server> --assign-identity
SERVER_MI=$(az sql server show -g <rg> -n <server> --query identity.principalId -o tsv)
az role assignment create --assignee-object-id "$SERVER_MI" --assignee-principal-type ServicePrincipal \
  --role "Storage Blob Data Contributor" --scope $(az storage account show -g <rg-sec> -n <digest-sa> --query id -o tsv)

# Enable automatic digest storage for the database.
az sql db ledger-digest-uploads enable -g <rg> -s <server> -n DocHub --endpoint https://<digest-sa>.blob.core.windows.net
```
Portal equivalent: *SQL database → Security → Ledger → Enable automatic digest storage → Storage account `<digest-sa>`*.

Verify (as any `ledger_reader`, e.g. the API identity):
```sql
SELECT * FROM sys.database_ledger_digest_locations;   -- one row with the digest-sa path
```
From then on `audit.usp_ReconcileLedger` calls `sys.sp_verify_database_ledger_from_digest_storage` on every run; a
failure is a *LedgerIntegrity* finding (§4 alerts).

## 2. Azure SQL Auditing (who changed the procedure, the triggers, the roles)
```bash
# Storage for audit logs (security team only), immutable with append writes, then locked.
az storage account create -g <rg-sec> -n <audit-sa> --sku Standard_ZRS --kind StorageV2 \
  --min-tls-version TLS1_2 --allow-blob-public-access false
az storage container create --account-name <audit-sa> -n sqldbauditlogs --auth-mode login
az storage container immutability-policy create --account-name <audit-sa> -c sqldbauditlogs \
  --period 3650 --allow-protected-append-writes true
ETAG=$(az storage container immutability-policy show --account-name <audit-sa> -c sqldbauditlogs --query etag -o tsv)
az storage container immutability-policy lock --account-name <audit-sa> -c sqldbauditlogs --if-match "$ETAG"

# Server-level auditing to storage and Log Analytics with the T21 action groups. BATCH_COMPLETED_GROUP is kept only for
# the deployment principal (predicate), so every statement of a deployment — pre/post-deployment scripts included — is logged.
az sql server audit-policy update -g <rg> -n <server> --state Enabled \
  --blob-storage-target-state Enabled --storage-account <audit-sa> --retention-days 0 \
  --log-analytics-target-state Enabled --log-analytics-workspace-resource-id $(az monitor log-analytics workspace show -g <rg-sec> -n <law> --query id -o tsv) \
  --actions SCHEMA_OBJECT_CHANGE_GROUP DATABASE_OBJECT_CHANGE_GROUP DATABASE_PRINCIPAL_CHANGE_GROUP DATABASE_ROLE_MEMBER_CHANGE_GROUP BATCH_COMPLETED_GROUP \
  --predicate-expression "action_id <> 'BCM' OR server_principal_name = '<pipeline-sp>'"
```
Portal: *SQL server → Security → Auditing → Enable; Storage `<audit-sa>`; Log Analytics `<law>`* (action groups and the
predicate are CLI/PowerShell only).

Verify: `az sql server audit-policy show -g <rg> -n <server>` lists the five groups and the predicate; run a deployment
and check that its batches arrive:
```kusto
SQLSecurityAuditEvents | where TimeGenerated > ago(1h) and ServerPrincipalName == "<pipeline-sp>" | take 20
```

## 3. Identities
1. Enable Entra-only authentication on `<server>` (*SQL server → Settings → Microsoft Entra ID → Support only Microsoft
   Entra authentication*). The Entra admin of the server is the security team's group, not the pipeline.
2. As the Entra admin, in database `DocHub`:
   ```sql
   CREATE USER [<api-mi>] FROM EXTERNAL PROVIDER;        ALTER ROLE app_api ADD MEMBER [<api-mi>];
   CREATE USER [<support-group>] FROM EXTERNAL PROVIDER; ALTER ROLE support_writer ADD MEMBER [<support-group>];
   CREATE USER [<pipeline-sp>] FROM EXTERNAL PROVIDER;   ALTER ROLE db_owner ADD MEMBER [<pipeline-sp>];
   ```
   The API's connection string uses `Authentication=Active Directory Managed Identity`. The pipeline identity is used only
   by the deployment job and has **no** access to `<digest-sa>`, `<audit-sa>` or `<law>` (check with
   `az role assignment list --assignee <pipeline-sp-app-id> --all -o table`).
3. **Verify the principal mapping** (reconciliation resolves `sys.database_ledger_transactions.principal_name` to a database
   user by SID, by user name as fallback): make one write through the API (e.g. rename a folder), then as `ledger_reader`:
   ```sql
   SELECT TOP (5) t.transaction_id, t.principal_name, SUSER_SID(t.principal_name) AS sid,
          (SELECT p.name FROM sys.database_principals AS p
           WHERE p.sid = SUSER_SID(t.principal_name) OR p.name = t.principal_name) AS database_user
   FROM sys.database_ledger_transactions AS t ORDER BY t.transaction_id DESC;
   ```
   `database_user` must be `<api-mi>` for the API's transaction. Then `POST /api/admin/audit/reconcile` (as an admin)
   must report `newFindings: 0`. If `database_user` is empty, re-create the API user with the name `principal_name` shows
   (`CREATE USER [<that name>] FROM EXTERNAL PROVIDER`).

## 4. Deployment and go-live
- Publish only with `IgnoreColumnOrder=True` (T21 §1 change rules), e.g.
  `sqlpackage /Action:Publish /SourceFile:DocHub.Database.dacpac /TargetConnectionString:"…;Authentication=Active Directory Default" /p:IgnoreColumnOrder=True`.
- After the initial data load, insert the go-live baseline once (as the Entra admin, recorded in the ledger):
  `INSERT INTO audit.ReconciliationBaseline (CreatedAt, Reason) VALUES (SYSUTCDATETIME(), N'go-live');`
- Keep `Audit:Reconciliation:Enabled = true` (default; nightly at `RunAtUtc`, window 48 h).

## 5. Alerts to the security mailbox
1. Action group: `az monitor action-group create -g <rg-sec> -n ag-dochub-security --short-name dochubsec --action email security security@<company>`.
2. **Reconciliation findings / ledger verification failures** — the API logs every run
   (`Ledger reconciliation …: {NewFindings} new findings, {ModuleProblems} module problems.`) and every failure. With the
   API's logs in `<law>` (container logs / App Service console logs), create a scheduled query alert (every 1 h):
   ```kusto
   ContainerAppConsoleLogs_CL   // or AppServiceConsoleLogs, depending on the host
   | where Log_s has "Ledger reconciliation"
   | where Log_s has "failed" or Log_s matches regex @"[1-9]\d* new findings" or Log_s matches regex @"[1-9]\d* module problems"
   ```
   `az monitor scheduled-query create -g <rg-sec> -n dochub-reconciliation-findings --scopes <law-id> --condition "count 'Q' > 0" --condition-query Q="<query above>" --evaluation-frequency 1h --window-size 1h --action-groups ag-dochub-security --severity 1`.
   Also alert when no run was logged for 26 h (the job stopped): same query without the filters, condition `count < 1`, window 26 h.
3. **Privileged changes outside deployments** — alert on audit events of principals other than the pipeline:
   ```kusto
   SQLSecurityAuditEvents
   | where DatabaseName == "DocHub" and ServerPrincipalName != "<pipeline-sp>"
   | where ActionName in ("SCHEMA_OBJECT_CHANGE_GROUP", "DATABASE_OBJECT_CHANGE_GROUP", "DATABASE_PRINCIPAL_CHANGE_GROUP", "DATABASE_ROLE_MEMBER_CHANGE_GROUP")
       or Statement has_any ("DISABLE TRIGGER", "NOCHECK", "SYSTEM_VERSIONING", "sp_rename")
   ```
4. Findings are also listed in the app: *Admin → Audit* (`GET /api/admin/audit/findings`).

## Checklist
- [ ] §0 RBAC: pipeline/DBAs without roles on `<server>` and `<rg-sec>`; delete lock on `<rg-sec>`.
- [ ] §1 digest container immutability policy **locked**; `sys.database_ledger_digest_locations` has a row.
- [ ] §2 auditing on, five action groups + predicate, immutable locked `sqldbauditlogs`, events visible in `<law>`.
- [ ] §3 Entra-only auth; users/roles created; principal mapping verified (0 findings after an API write).
- [ ] §4 go-live baseline inserted; nightly reconciliation enabled.
- [ ] §5 action group + the three alert rules; a test alert reached `security@<company>`.
