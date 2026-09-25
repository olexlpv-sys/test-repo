# T02 — Database project and core schema

| | |
|---|---|
| **Depends on** | — (can run in parallel with T01) |
| **Blocks** | T03, T04 |
| **Size** | M (2–3 days) |
| **Requirements** | FR-F*, FR-T*, FR-N*, FR-V*, FR-P*, FR-CM*, FR-H2, NFR-1, NFR-4 |

## Goal
Create the SQL Database Project (source of truth for the schema — see [ADR-01](../architecture.md)) with all core tables, constraints, indexes and seed data.

## Scope

### 1. Project
- `database/DocHub.Database.slnx` + `database/DocHub.Database/DocHub.Database.sqlproj` (SDK `Microsoft.Build.Sql`, `DSP = Microsoft.Data.Tools.Schema.Sql.SqlAzureV12DatabaseSchemaProvider`).
- Folder layout: `Schemas/`, `app/Tables/`, `audit/Tables/` (created in T03), `Security/`, `Scripts/PostDeployment/`.
- Schemas: `app` (domain), `audit` (change log — T03).
- `database/README.md`: how to build (`dotnet build`) and publish locally (`SqlPackage /Action:Publish /SourceFile:… /TargetConnectionString:…`), incl. installing `microsoft.sqlpackage` as a dotnet tool (add `.config/dotnet-tools.json`).
- CI: add a job that builds the DACPAC and publishes it as a build artifact.

### 2. Tables

Common conventions: `int IDENTITY` PKs, `datetime2(3)` UTC timestamps with `DEFAULT SYSUTCDATETIME()`, `RowVersion rowversion` on every editable table, FKs `ON DELETE NO ACTION` unless noted.

**`app.[User]`**
| Column | Type | Notes |
|---|---|---|
| Id | int PK | |
| Login | nvarchar(100) | unique |
| DisplayName | nvarchar(200) | |
| Email | nvarchar(256) null | |
| IsAdmin | bit | default 0 |
| IsActive | bit | default 1 |

**`app.Folder`** — virtual folder tree
| Column | Type | Notes |
|---|---|---|
| Id | int PK | |
| ParentFolderId | int null FK → Folder | null = root level |
| Name | nvarchar(200) | not empty (check) |
| SortOrder | int | |
| CreatedAt / CreatedByUserId | | FK → User |
| RowVersion | rowversion | |

Unique sibling names: unique index `(ParentFolderId, Name) WHERE ParentFolderId IS NOT NULL` + unique index `(Name) WHERE ParentFolderId IS NULL`.

**`app.NodeType`** — editable dictionary
| Column | Type | Notes |
|---|---|---|
| Id | int PK | |
| Code | varchar(50) | unique, e.g. `CHAPTER` |
| Name | nvarchar(100) | |
| Description | nvarchar(500) null | |
| SortOrder | int | |
| IsActive | bit | default 1 |
| RowVersion | rowversion | |

**`app.Document`**
| Column | Type | Notes |
|---|---|---|
| Id | int PK | |
| FolderId | int FK → Folder | |
| Title | nvarchar(300) | |
| OwnerUserId | int FK → User | creator |
| CreatedAt | datetime2(3) | |
| DeletedAt / DeletedByUserId | null | soft delete (status `Deleted`) |
| RowVersion | rowversion | |

Index `(FolderId) INCLUDE (Title, OwnerUserId) WHERE DeletedAt IS NULL`.

**`app.DocumentVersion`**
| Column | Type | Notes |
|---|---|---|
| Id | int PK | |
| DocumentId | int FK → Document | |
| Status | tinyint | 1 Draft, 2 Signed, 3 Deleted (check constraint) |
| VersionNumber | int null | assigned on signing |
| BasedOnVersionId | int null FK → DocumentVersion | source of the copy |
| CreatedAt / CreatedByUserId | | |
| SignedAt / SignedByUserId | null | |
| SignedContentHash | varbinary(32) null | SHA-256 of canonical content at signing (T07) |
| RowVersion | rowversion | |

Constraints:
- Unique filtered index `(DocumentId) WHERE Status = 1` — **at most one draft per document**.
- Unique filtered index `(DocumentId, VersionNumber) WHERE VersionNumber IS NOT NULL`.
- Check: `Status = 2` ⇔ `VersionNumber IS NOT NULL AND SignedAt IS NOT NULL AND SignedByUserId IS NOT NULL`.

**`app.DocumentNode`** — tree node
| Column | Type | Notes |
|---|---|---|
| Id | int PK | |
| DocumentVersionId | int FK → DocumentVersion | |
| LogicalNodeId | uniqueidentifier | stable across versions ([ADR-03](../architecture.md)) |
| ParentNodeId | int null | null = top level |
| NodeTypeId | int FK → NodeType | |
| Title | nvarchar(500) | |
| SortOrder | int | order among siblings |
| CreatedAt / CreatedByUserId, ModifiedAt / ModifiedByUserId | | |
| RowVersion | rowversion | |

Constraints / indexes:
- Unique `(DocumentVersionId, Id)` and **composite FK** `(DocumentVersionId, ParentNodeId) → (DocumentVersionId, Id)` — the parent must belong to the same version.
- Unique `(DocumentVersionId, LogicalNodeId)` and unique `(Id, DocumentVersionId, LogicalNodeId)` (target of the `NodeContent` composite FK).
- Index `(DocumentVersionId, ParentNodeId, SortOrder)`.

**`app.NodeContent`** — 1:1 with node, separated to keep tree queries light
| Column | Type | Notes |
|---|---|---|
| NodeId | int PK, FK → DocumentNode `ON DELETE CASCADE` | |
| DocumentVersionId | int | redundant copy of the node's version — keeps audit rows resolvable after cascade delete (T03) |
| LogicalNodeId | uniqueidentifier | redundant copy, same reason; composite FK `(NodeId, DocumentVersionId, LogicalNodeId)` → node keeps them consistent |
| ContentHtml | nvarchar(max) | sanitized HTML |
| PlainText | nvarchar(max) | derived |
| ContentHash | varbinary(32) | SHA-256 of `ContentHtml` |
| ModifiedAt / ModifiedByUserId | | |
| RowVersion | rowversion | |

**`app.DocumentPermission`**
| Column | Type | Notes |
|---|---|---|
| Id | int PK | |
| DocumentId | int FK → Document | per document, not per version |
| UserId | int FK → User | |
| Role | tinyint | 1 Editor, 2 Approver |
| LogicalNodeId | uniqueidentifier null | null = whole document; only allowed for Editor (check) |
| GrantedAt / GrantedByUserId | | |

Unique index `(DocumentId, UserId, Role, LogicalNodeId)` (handle NULL via two filtered indexes).
Owner is **not** stored here — it is `Document.OwnerUserId`.

**`app.Comment`**
| Column | Type | Notes |
|---|---|---|
| Id | int PK | |
| DocumentId | int FK | |
| DocumentVersionId | int FK | |
| LogicalNodeId | uniqueidentifier null | null = comment on the whole document |
| ParentCommentId | int null FK → Comment | reply |
| AuthorUserId | int FK → User | |
| Body | nvarchar(4000) | plain text |
| CreatedAt, EditedAt null, DeletedAt null | | soft delete |
| ResolvedAt / ResolvedByUserId | null | |
| RowVersion | rowversion | |

Index `(DocumentVersionId, LogicalNodeId)`.

### 3. Seed data (`Scripts/PostDeployment/Script.PostDeployment.sql`, idempotent `MERGE`, fixed ids with `IDENTITY_INSERT`)
- Users: `1 admin (IsAdmin)`, `2 alice`, `3 bob`, `4 carol`, `5 dave` — display names and emails of your choice.
- Node types: `CHAPTER` Chapter, `SECTION` Section, `SUBSECTION` Subsection, `PARAGRAPH` Paragraph, `APPENDIX` Appendix.
- Root folders: `General`, `Templates`.
- Optional (Development only, controlled by SQLCMD variable `$(SeedDemoData)`): one demo document with the tree from [requirements FR-T1](../requirements.md) and a signed v1.

## Out of scope
Audit table and triggers (T03), EF Core mapping (T04).

## Acceptance criteria
- [ ] `dotnet build database/DocHub.Database.slnx` produces `DocHub.Database.dacpac` with no warnings (SQL71xxx warnings treated as errors).
- [ ] Publishing to an empty local SQL Server creates all objects and seed data; re-publishing is a no-op (idempotent seed).
- [ ] Inserting a second Draft for the same document fails (filtered unique index).
- [ ] Inserting a node whose parent belongs to a different version fails (composite FK).
- [ ] Setting `Status = 2` without `VersionNumber`/`SignedAt` fails (check constraint).
- [ ] A short SQL test script `database/tests/constraints.sql` demonstrates the three checks above (run manually or in CI via `sqlcmd`).
