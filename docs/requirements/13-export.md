# Export

> Part of the [requirements set](README.md). IDs are stable — tasks and tests reference them.

- FR-E1 Any user who can view a document version (FR-P5) can **export it to PDF**: the whole document, or a selected node with its subtree.
- FR-E2 The PDF reproduces the on-screen Word-like layout: the style catalog (fonts, sizes, spacing, lists, tables with merged cells, borders, shading), section headings with tree numbering, page breaks. Page setup: A4 by default, Letter optional, portrait, 2.5 cm margins.
- FR-E3 Options:
  - title page (title, folder path, version label, owner, date);
  - table of contents generated from the tree, with page numbers;
  - header and footer with document title, version label and "page X of Y";
  - **signature page** for signed versions (approvers, signed-at times, signing notes).
- FR-E4 Status marking: drafts carry a diagonal **"DRAFT"** watermark. Signed versions flagged "modified after signing" (FR-H5) carry a warning banner on every page. Deleted documents cannot be exported by anyone except the owner or an admin (same rule as viewing).
- FR-E5 Export is **asynchronous**. The user starts it, sees progress, and downloads the file when it is ready; the UI starts the download automatically. Every API call involved stays within NFR-L4 (≤ 3 s). Target generation time: ≤ 10 s for 300 nodes and ≤ 60 s for 2 000 nodes.
- FR-E6 PDFs of **signed** versions are cached and reused while the version stamp is unchanged (NFR-L9). A script edit produces a new stamp, so the next export regenerates the file.
- FR-E7 Every export is recorded (who, what, options, when) in the audit trail.
- FR-E8 **[A]** Out of scope for this release: export with track changes, export to DOCX (designed for in content-format.md §3), batch export of whole folders.
