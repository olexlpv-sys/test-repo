# T18 — Search (stored procedures, API, UI)

| | |
|---|---|
| **Depends on** | T07 (list SP pattern), T09 (`PlainText`), T14, T15 |
| **Size** | M (2–2.5 days) |
| **Requirements** | FR-D2, FR-D5, FR-D6, FR-UI1, FR-UI2 |
| **Read first** (nothing else) | [11-data-access](../requirements/11-data-access.md) · [08-web-ui](../requirements/08-web-ui.md) · [process](../process.md) |

## Goal
Search across documents and inside a document, implemented as stored procedures for performance (FR-D2).

## DB (in `database/DocHub.Database`)
- `app.usp_SearchDocuments @UserId, @Query, @FolderId = NULL, @IncludeSubfolders = 1, @Scope = 'All', @VersionScope = 'Current', @Page, @PageSize`
  - `Current` = the draft if one exists, else the latest signed version; `AllSigned` = every signed version.
  - Matches `Document.Title`, `DocumentNode.Title`, `NodeContent.PlainText` (FR-D6); excludes deleted documents unless the caller is owner/admin.
  - Result set 1: documents `{ DocumentId, Title, FolderPath, Status, VersionLabel, MatchCount, Rank }` (title matches rank above text matches); result set 2: up to 3 matching nodes per document `{ DocumentId, DocumentVersionId, LogicalNodeId, NodeTitle, Snippet }` (±60 chars around the first hit); result set 3: total count.
- `app.usp_SearchInDocument @DocumentVersionId, @Query` → `{ LogicalNodeId, NodeId, NodeTitle, MatchIn ('Title'|'Content'), Snippet, HitCount }` in tree order.
- `@Query`: trimmed, 2–200 chars; `%`, `_`, `[` escaped; parameterized only.
- Supporting indexes as needed (e.g. `(DocumentVersionId) INCLUDE (Title)` on nodes); keep the contract stable so a full-text implementation can replace `LIKE` later.

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
- [ ] DB tests: title vs content match, accent/case-insensitive match, wildcard characters in the query treated literally, deleted documents excluded for non-owners, `Current` vs `AllSigned` scope, paging and totals.
- [ ] API integration tests: results equal an EF-built reference query on generated data; `q` shorter than 2 chars → `400`.
- [ ] Performance: `usp_SearchDocuments` < 500 ms and `usp_SearchInDocument` < 100 ms on the NFR-6 data set (tagged perf test).
- [ ] UI: search everywhere → click snippet → document opens with the node selected; find-in-document navigates between hits.
- [ ] Playwright: search for a word typed into a node in an earlier step finds that document.
