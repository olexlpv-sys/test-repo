# Requirements

Requirements are split by area so that a task only needs to load the files it references (context economy).
Items marked **[A]** are assumptions — confirm or correct them in the [decisions log](10-decisions-log.md).

| File | Area | IDs |
|---|---|---|
| [01-folders.md](01-folders.md) | Virtual folders | FR-F* |
| [02-document-tree.md](02-document-tree.md) | Document tree, node types, styled content | FR-T*, FR-N* |
| [03-versioning-and-signing.md](03-versioning-and-signing.md) | Lifecycle, versions, multi-approver signing, restore | FR-V* |
| [04-change-tracking.md](04-change-tracking.md) | Audit, support scripts, history | FR-H* |
| [05-version-comparison.md](05-version-comparison.md) | Compare versions | FR-C* |
| [06-permissions.md](06-permissions.md) | Owner / Editor / Approver / Admin | FR-P* |
| [07-comments.md](07-comments.md) | Comments | FR-CM* |
| [08-web-ui.md](08-web-ui.md) | Web UI incl. test mode | FR-UI* |
| [09-non-functional.md](09-non-functional.md) | Performance, security, quality | NFR-* |
| [10-decisions-log.md](10-decisions-log.md) | Product decisions, open questions, spec assumptions | Q*, A-* |
| [12-load-and-performance.md](12-load-and-performance.md) | Load profile (50 k readers, 10 k docs, 20 req/s), SLO p99 ≤ 3 s, binding design consequences | NFR-L* |
| [13-export.md](13-export.md) | PDF export | FR-E* |
| [11-data-access.md](11-data-access.md) | EF Core for CRUD, stored procedures for permissions and lists, DB project owns all objects | FR-D* |

Related specs: [content-format.md](../content-format.md) (rich-text format), [architecture.md](../architecture.md).

## Glossary

| Term | Meaning |
|---|---|
| **Folder** | Virtual folder. Folders form a tree of arbitrary depth. Purely logical (not a file system). |
| **Document** | Logical document. Belongs to exactly one folder. Has an owner and a sequence of versions. |
| **Document version** | A snapshot of the document tree. Status `Draft`, `Signed` or `Deleted`. Signed versions are numbered v1, v2, v3… |
| **Node** | An element of the document tree (e.g. "Chapter 1", "Section 2"). Has a type, a free-text title, an order among siblings, and rich-text content. |
| **Logical node id** | A stable GUID identifying "the same node" across versions. Used for history, comparison, node-scoped permissions and comments. |
| **Node type** | Entry of an editable dictionary (Chapter, Section, Subsection, …). |
| **Change log** | Audit trail of every data change, written by the database itself (so it also captures support scripts). |

## Out of scope (first release)

Real authentication (Entra ID), DOCX import/export (designed for, see ../content-format.md §3; PDF export is in scope — FR-E*), full-text / content search (only the title filter of the document list is in scope — FR-D6), attachments/images upload,
notifications, approval workflow (multi-step), real-time collaborative editing, Azure infrastructure-as-code.
