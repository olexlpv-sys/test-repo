# T03 — Database-level change tracking (audit)

| | |
|---|---|
| **Depends on** | T02 |
| **Blocks** | T04 (session context interceptor), T11 |
| **Size** | M (2 days) |
| **Requirements** | FR-H1, FR-H3, FR-H5 |
| **Read first** (nothing else) | [04-change-tracking](../requirements/04-change-tracking.md) · [architecture](../architecture.md) (only sections linked in the text) · [process](../process.md) |

## Goal
Every data change — made by the API **or by a support script** — is recorded in `audit.ChangeLog` by the database itself, with who/when/what. See [ADR-04](../architecture.md).

## Scope

### 1. Table `audit.ChangeLog`
| Column | Type | Notes |
|---|---|---|
| Id | bigint IDENTITY PK | |
| ChangedAt | datetime2(7) | `DEFAULT SYSUTCDATETIME()` |
| TableName | sysname | e.g. `app.NodeContent` |
| Operation | char(1) | `I`, `U`, `D` |
| EntityId | int | PK of the changed row |
| DocumentId | int null | denormalized for fast filtering |
| DocumentVersionId | int null | |
| LogicalNodeId | uniqueidentifier null | for node / content rows |
| OldValues | nvarchar(max) null | JSON of the `deleted` row (`ISJSON` check) |
| NewValues | nvarchar(max) null | JSON of the `inserted` row |
| ChangedColumns | nvarchar(1000) null | comma-separated list for `U` (only columns whose value actually changed) |
| UserId | int null | from `SESSION_CONTEXT(N'UserId')` |
| Source | varchar(10) | `App` if `UserId` present in session context, else `Script` |
| DbLogin | nvarchar(128) | `ORIGINAL_LOGIN()` |
| AppName | nvarchar(128) null | `APP_NAME()` |
| CorrelationId | nvarchar(64) null | from session context (HTTP request id) |
| Operation | nvarchar(50) null | from session context (bulk operation marker) |
| Ticket | nvarchar(50) null | from session context, set by support scripts |
| Reason | nvarchar(500) null | from session context, set by support scripts |

Indexes: `(LogicalNodeId, ChangedAt)`, `(DocumentId, ChangedAt)`, `(TableName, EntityId, ChangedAt)`.
The table is **append-only**: `DENY UPDATE, DELETE ON audit.ChangeLog TO public`; a role `support_writer` gets DML on `app` but only `SELECT` on `audit`.

### 2. Triggers
`AFTER INSERT, UPDATE, DELETE` trigger per tracked table:
`app.Folder`, `app.NodeType`, `app.ContentStyle`, `app.Document`, `app.DocumentVersion`, `app.VersionSignature`, `app.DocumentNode`, `app.NodeContent`, `app.DocumentPermission`, `app.Comment`
(`app.[User]` too — it's seed data, but support may edit it). For `app.NodeContent` log `ContentJson` and omit the derived columns (`ContentHtml`, `PlainText`) to halve the log volume.

Requirements for triggers:
- **Set-based**, multi-row safe (a script updating 1 000 rows must write 1 000 log rows in one statement). Use `FULL OUTER JOIN inserted/deleted ON Id` and per-row `(SELECT … FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)`.
- `SET NOCOUNT ON`; skip when `@@ROWCOUNT = 0`.
- Exclude `RowVersion` from JSON (binary, noisy); `ContentHtml` **is** included (needed for history).
- For `U`, skip rows where no audited column actually changed (compare JSON or column-by-column), fill `ChangedColumns`.
- Resolve `DocumentId` / `DocumentVersionId` / `LogicalNodeId` from the row itself where present (`DocumentNode`, `NodeContent` carries redundant `DocumentVersionId` + `LogicalNodeId` — see T02 — so cascade deletes stay resolvable) and join `DocumentVersion` for `DocumentId`.
- To avoid hand-writing 9 near-identical triggers, a generator script (`database/tools/Generate-AuditTriggers.sql` or a small PowerShell/C# script) is acceptable; the generated `.sql` files are committed.

### 3. Session context contract
| Key | Set by | Meaning |
|---|---|---|
| `UserId` (int) | API on connection open (T04) | acting user |
| `CorrelationId` (nvarchar) | API | `HttpContext.TraceIdentifier` |
| `Operation` (nvarchar) | API, for bulk operations (e.g. `CopyVersion` in T07) | lets history (T11) collapse bulk rows |
| `Ticket`, `Reason` | support scripts | why the change was made |

Stored procedure **`audit.usp_SetSupportContext @Ticket nvarchar(50), @Reason nvarchar(500), @ActingUserId int = NULL`** — sets the keys for the current session. A script template `database/support/_TEMPLATE.sql` shows the expected usage:
```sql
EXEC audit.usp_SetSupportContext @Ticket = N'INC-1234', @Reason = N'Fix typo in signed v3 at customer request';
BEGIN TRAN;
  UPDATE app.NodeContent SET ContentHtml = … WHERE NodeId = …;
COMMIT;
```
Scripts **without** the context call are still audited (`Source = 'Script'`, `Ticket = NULL`, `DbLogin` identifies the person).

### 4. Guard on signed versions (optional, recommended)
Signed content is immutable for the API (enforced in code, T07/T09). The DB does **not** block scripts, but the change is detectable: T07 stores `SignedContentHash`; T11 exposes "modified after signing". Add view **`audit.vSignedVersionTampering`** listing `ChangeLog` rows that touched nodes/contents of versions with `Status = 2` after `SignedAt`.

## Out of scope
API for reading the log (T11). Retention/archiving of the log.

## Acceptance criteria (automated in `tests/DocHub.Database.Tests`, run in CI)
- [ ] With `sp_set_session_context 'UserId', 2` an `UPDATE` on `app.NodeContent` writes one row: `Source='App'`, `UserId=2`, `OldValues`/`NewValues` contain old/new `ContentHtml`, `ChangedColumns` lists only changed columns.
- [ ] Without session context the same update writes `Source='Script'`, `UserId=NULL`, `DbLogin=ORIGINAL_LOGIN()`.
- [ ] After `audit.usp_SetSupportContext` the row carries `Ticket` and `Reason`.
- [ ] A multi-row `UPDATE` of 500 nodes writes 500 log rows; an update that sets a column to the same value writes 0 rows.
- [ ] Deleting a node (cascade to content) writes `D` rows for both, each with `DocumentVersionId` and `LogicalNodeId` populated.
- [ ] `UPDATE`/`DELETE` on `audit.ChangeLog` by a non-dbo user fails.
