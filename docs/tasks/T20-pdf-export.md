# T20 — PDF export

| | |
|---|---|
| **Depends on** | T09 (HTML renderer), T05 (style CSS), T07 (signatures, version stamp), T10 (`EnsureCanView`), T15 (UI entry point) |
| **Size** | L (3.5–4 days) |
| **Requirements** | FR-E1 … FR-E8, FR-P5, NFR-L4, NFR-L9 |
| **Read first** (nothing else) | [13-export](../requirements/13-export.md) · [content-format](../content-format.md) §2 "Rendering" · [12-load-and-performance](../requirements/12-load-and-performance.md) (NFR-L4, L9) · [11-data-access](../requirements/11-data-access.md) · [process](../process.md) |

## Goal
Asynchronous, high-fidelity PDF export of a document version (or a subtree), reusing the server HTML renderer so that the PDF looks like the editor.

## Design
- **Rendering pipeline:**
  1. Load the tree, contents (HTML from T09 `ContentHtmlRenderer`) and the style CSS (T05).
  2. Compose one print HTML document: title page, table of contents, sections with numbered headings, signature page. It uses a print stylesheet (`@page` size/margins, `break-before`, running header/footer, a `DRAFT` watermark via a fixed element).
  3. Convert it to PDF with **headless Chromium through Playwright for .NET** (`page.PdfAsync` with header/footer templates and page numbers).
  4. The TOC page numbers come from a two-pass render: pass 1 measures the heading pages, pass 2 injects the numbers.
  - The composer is pure .NET (Infrastructure), unit-testable without Chromium. Fonts from the style catalog are bundled in the container (no system-font dependency).
- **Job model:** table `app.ExportJob` (Id, DocumentId, DocumentVersionId, LogicalNodeId null, Options JSON, Status Queued/Running/Succeeded/Failed, RequestedBy, RequestedAt, StartedAt, FinishedAt, Error, BlobPath, FileSize, VersionStamp). It is audited by the T03 trigger (FR-E7), and like every object it lives in the DB project (FR-D4). All CRUD goes through EF Core (FR-D1).
- **Worker:** a hosted service `PdfExportWorker` claims queued jobs (`UPDATE TOP(1) … WITH (READPAST, UPDLOCK)`). Each API instance runs at most 2 concurrent renders (configurable); a job running longer than 5 min fails.
  - **[A]** In production the worker can run as a separate container instance with the same image, so rendering never competes with API requests.
- **Storage:** **[A]** Azure Blob Storage (Azurite locally and in tests), container `exports`, path `{documentId}/{versionId}/{stamp}-{optionsHash}.pdf`. Files of drafts expire after 24 h. Files of signed versions are kept and reused (FR-E6): an identical request with the same `VersionStamp` and options returns `Succeeded` at once, without rendering.

## API
| Method | Route | Notes |
|---|---|---|
| POST | `/api/versions/{versionId}/exports/pdf` | `{ logicalNodeId?, pageSize: "A4"|"Letter", titlePage: true, toc: true, headerFooter: true, signaturePage: true }` → `202 { jobId, status }`. Calls `EnsureCanView` (FR-P5). Returns an existing job for an identical pending or cached request. |
| GET | `/api/exports/{jobId}` | `{ status, progress (0–100), error?, fileName?, fileSize? }` — only the requester or an admin |
| GET | `/api/exports/{jobId}/file` | streams the PDF (`Content-Disposition: attachment; filename="<title> - <versionLabel>.pdf"`). Re-checks `EnsureCanView` at download time. |

## UI (in the T15 version bar)
- An **Export PDF** button opens a small dialog: whole document or the selected section, page size, and checkboxes for title page, TOC, header/footer and signature page.
- The status is shown as a toast with progress; it polls every 2 s. The download starts automatically when the export succeeds, and failures show the error.
- The section context menu offers **Export this section to PDF**.

## Acceptance criteria
- [ ] PDF of the FR-T1 example document: title page, TOC with correct page numbers, numbered headings, and header/footer with "page X of Y".
- [ ] Visual regression: the PDFs of the content fixtures (T09: heading styles, fonts, spacing, nested lists, a table with colspan/rowspan, borders and shading) match approved page snapshots (pixel diff within tolerance).
- [ ] A draft export shows the DRAFT watermark. A signed version shows the signature page with all approvers. A tampered signed version (script edit) shows the warning banner.
- [ ] Subtree export contains only that node and its descendants, with their numbering kept.
- [ ] A second export of the same signed version with the same options is served from cache (no render, `Succeeded` immediately). After a script edit of that version it renders again.
- [ ] Non-viewer: `POST` and `GET …/file` for a deleted document → `404`. Another user's job id → `404`.
- [ ] A 2 000-node document exports in ≤ 60 s, and a 300-node document in ≤ 10 s. With 10 exports queued, API p99 stays ≤ 3 s (included in the T19 load mix).
- [ ] Every export creates an audited `ExportJob` row. A failed render (e.g. Chromium crash) marks the job `Failed` with an error, and the worker continues with other jobs.
- [ ] Playwright: open a document, export the PDF, and the file downloads and is a valid PDF with the expected page count.
