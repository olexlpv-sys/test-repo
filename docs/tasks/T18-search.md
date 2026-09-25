# T18 — Search (stored procedures, API, UI)

| | |
|---|---|
| **Depends on** | T07 (list SP pattern), T09 (`PlainText`), T14, T15 |
| **Size** | M (2–2.5 days) |
| **Requirements** | FR-D2, FR-D5, FR-D6, FR-P5, FR-UI1, FR-UI2, NFR-L4, NFR-L5, NFR-L7, NFR-L8 |
| **Read first** (nothing else) | [11-data-access](../requirements/11-data-access.md) · [12-load-and-performance](../requirements/12-load-and-performance.md) · [06-permissions](../requirements/06-permissions.md) (FR-P5) · [08-web-ui](../requirements/08-web-ui.md) · [process](../process.md) |

## Goal
Search across documents and inside a document, implemented as stored procedures for performance (FR-D2).

## DB (in `database/DocHub.Database`)
- `app.usp_SearchDocuments @UserId, @Query, @FolderId = NULL, @IncludeSubfolders = 1, @Scope = 'All', @VersionScope = 'Current', @Page, @PageSize`
  - `Current` = the draft if one exists, else the latest signed version — filtered by `DocumentVersion.IsCurrent = 1` (NFR-L8); `AllSigned` = every signed version.
  - Full-text `CONTAINSTABLE` over `Document.Title`, `DocumentNode.Title`, `NodeContent.PlainText` (FR-D6); the query is converted to prefix terms (`"word*" AND "other*"`); `RANK` from `CONTAINSTABLE` drives ordering; excludes deleted documents unless the caller is owner/admin (FR-P5).
  - Result set 1: **one row per (document, version)** `{ DocumentId, DocumentVersionId, Title, FolderPath, Status, VersionLabel, MatchCount, Rank }` — with `Current` that is one row per document; with `AllSigned` a document appears once per matching signed version, grouped under the document in the UI (title matches rank above text matches); result set 2: up to 3 matching nodes per (document, version) `{ DocumentId, DocumentVersionId, LogicalNodeId, NodeTitle, Snippet }` (±60 chars around the first hit); result set 3: total count.
- `app.usp_SearchInDocument @UserId, @DocumentVersionId, @Query` → returns nothing (and the API `404`) when the caller may not view the document (FR-P5); otherwise `{ LogicalNodeId, NodeId, NodeTitle, MatchIn ('Title'|'Content'), Snippet, HitCount }` in tree order.
- `@Query`: trimmed, 2–200 chars; split into words, full-text special characters (`" * ( ) & | ! ~`) stripped, noise-word-only queries → empty result; parameterized only.
- Full-text catalog `ftDocHub` + full-text indexes (change tracking `AUTO`) on the three columns, owned by the DB project (FR-D4). Snippet extraction on `PlainText` via `CHARINDEX` of the first matched term (only for the ≤ 3 returned nodes).

## API
| Method | Route | Notes |
|---|---|---|
| GET | `/api/search?q=&folderId=&includeSubfolders=true&scope=All|Title|Content&versionScope=Current|AllSigned&page&pageSize` | calls `usp_SearchDocuments` |
| GET | `/api/versions/{versionId}/search?q=` | calls `usp_SearchInDocument` |

Snippets are plain text; the UI highlights the query itself (no HTML from the server).

## UI
- Main window: search box above the document list — typing filters the current folder by title (T07 `search` parameter); **Enter / "Search everywhere"** opens a results view with documents, folder path, and node snippets; clicking a snippet opens the document at that node (`/documents/:id?node=…`).
- Document form: **Find in document** (`Ctrl+F` inside the form) — results list with snippets, next/previous navigation selects the node in the tree and highlights hits in the editor.

## Acceptance criteria
- [ ] DB tests: title vs content match, prefix match (`contr` finds `contract`), accent/case-insensitive match, special characters stripped safely, deleted documents excluded for non-owners, `Current` vs `AllSigned` (one row per matching signed version), paging and totals. (Full-text population is asynchronous — tests wait for `FULLTEXTCATALOGPROPERTY(…,'PopulateStatus') = 0`.)
- [ ] API integration tests: results equal a reference implementation on generated data; `q` shorter than 2 chars → `400`; in-document search of a deleted document by a non-owner → `404`.
- [ ] Performance: single request `usp_SearchDocuments` < 500 ms and `usp_SearchInDocument` < 100 ms on the NFR-6 data set; under load p95 ≤ 1.5 s on the NFR-L2 volume (verified by T19).
- [ ] UI: search everywhere → click snippet → document opens with the node selected; find-in-document navigates between hits.
- [ ] Playwright: search for a word typed into a node in an earlier step finds that document.
