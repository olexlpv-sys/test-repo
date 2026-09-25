# Load profile & performance

> Part of the [requirements set](README.md). IDs are stable — tasks and tests reference them.

## Load profile (production target)
- NFR-L1 **Users:** 50 000 readers; 500 editors/owners (approvers are drawn from the same population). All users exist in `app.[User]` (for load tests: generated seed of 50 500 users).
- NFR-L2 **Data volume:** 10 000 documents in 100 folders.
  **[A]** Sizing assumptions for tests: on average 300 nodes per document (max 2 000, depth ≤ 15), 5 versions per document, 2 KB of `ContentJson` per node, 10 permission grants per document, 20 comments per version.
  → ≈ 15 M `DocumentNode` rows, ≈ 15 M `NodeContent` rows (~30 GB), ≈ 100 k grants, ≈ 1 M comments; `audit.ChangeLog` grows by ≈ 1–2 M rows per month.
- NFR-L3 **Throughput:** sustained **20 requests/second** of mixed traffic, bursts up to 40 req/s for 1 minute.
  **[A]** Mix: 70 % reads by readers (document list incl. title filter, open document/tree, node content), 20 % editor activity (content autosave, tree edits, history, compare), 10 % other (comments, signatures, admin).

## Service level
- NFR-L4 **Every API request completes in ≤ 3 s** at the NFR-L3 load (p99 ≤ 3 s; no request timeouts, 0 % 5xx) on the NFR-L2 data volume.
- NFR-L5 Internal budgets that keep NFR-L4 achievable (p95 at load):
  | Operation | p95 |
  |---|---|
  | permission check (`usp_CheckPermission`) | 20 ms |
  | document list page (`usp_ListDocuments`) | 300 ms |
  | open document (header + tree, 2 000 nodes) | 800 ms |
  | node content read / autosave | 300 ms |
  | history page, compare (2 000 nodes) | 1.5 s |
  | new draft (deep copy, 2 000 nodes) | 2.5 s |
- NFR-L6 The web UI shows the main window and an opened document (first node content) within 3 s on a normal office connection at the NFR-L3 load.

## Consequences for the design (binding)
- NFR-L7 *(withdrawn — full-text search is out of scope, Q13.)*
- NFR-L8 `DocumentVersion.IsCurrent` marks the current version of each document (draft if present, else latest signed), kept up to date by the lifecycle operations, so the document list and "open document" read the current version with an index seek.
- NFR-L9 **Signed versions are immutable through the API**, so their tree, content and compare results are cached server-side (`HybridCache`) keyed by version id + **`app.VersionStamp.LastChangeLogId`** — a per-version stamp that the audit triggers advance on every change to the version's nodes/content (including script edits), so any change is a cache miss. HTTP: `ETag` = the stamp, `Cache-Control: private, no-cache` (always revalidated, `304` when unchanged), `Vary: X-User-Id`. The view check (FR-P5) always runs **before** a cache hit is served.
- NFR-L10 Every hot query is index-backed: grants `(UserId, DocumentId)` and `(DocumentId, Role)`; nodes `(DocumentVersionId, ParentNodeId, SortOrder)`; content through the node PK; the audit table `(LogicalNodeId, ChangedAt)` and `(DocumentId, ChangedAt)`. **[A]** `audit.ChangeLog` is page-compressed and partitioned by month.
- NFR-L11 **[A]** The Azure SQL target for the load test is General Purpose 4 vCores (a starting point; the load test determines the final tier). The API runs at least 2 instances behind a load balancer. The API is stateless apart from the local cache.
- NFR-L12 Load and soak tests are part of the quality gates (see [testing strategy](../testing-strategy.md)): a production-scale data generator, load scenarios following the NFR-L3 mix, a 1-hour soak without degradation, and reports compared to NFR-L4/L5.
