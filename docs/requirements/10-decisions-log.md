# Decisions log & open questions

> Part of the [requirements set](README.md). IDs are stable — tasks and tests reference them.

Every answer from the product owner is recorded here; the affected requirement files are updated in the same commit.

## Open questions

| # | Question | Current assumption |
|---|---|---|

## Assumptions made by the spec (confirm or override)
| # | Assumption |
|---|---|
| A-1 | The document title is a **document-level** attribute (not versioned, not part of the signing hash): the owner can rename a non-deleted document at any time; renaming does not outdate signatures. |
| A-2 | A draft can be discarded only when a signed version exists; for a never-signed document discard is rejected (`409 only-version`) and the owner deletes the document instead. |
| A-3 | Editing a draft makes earlier approver signatures outdated; approvers must sign again. |
| A-4 | Admins may move deleted documents (to be able to empty and delete folders). |
| A-5 | A section showing track changes is read-only; the user turns the view off to edit (no editing inside track changes / accept-reject in this release). |


## Resolved

| # | Question | Decision |
|---|---|---|
| Q1 | Web UI technology | **React** + TS + Vite + TipTap |
| Q3 | Who signs? | **Approver** |
| Q4 | What may a node-scoped Editor do? | **Text only**; structure is defined by the Owner |
| Q5 | Is reading open to everyone? | **Yes** |
| Q6 | Do comments carry over to a new draft? | **No**, bound to a version; previous versions' comments can be shown |
| Q2 | New draft from which version? | From the **latest signed** version only |
| Q7 | Who manages folders and node types? | **Administrators** |
| Q8 | Rich-text storage format | **Structured JSON (TipTap/ProseMirror) modelled on WordprocessingML** + derived HTML — see [content-format.md](../content-format.md) |
| Q9 | Restore deleted documents via API? | **Yes** (owner or admin) |
| Q10 | Several approvers — one or all? | **All approvers must sign** |
| Q11 | Owner as Approver of own document? | **No**; a test-mode user dropdown lets one person act as different users |
| Q12 | Node-level Editor grant includes descendants? | **Yes** |
| Q13 | Search | **Full-text / content search is out of scope**; only the document-list title filter remains (FR-D6). T18 removed from the queue. |
| Q15 | Where is change history shown? | **Inside the editor, per section/node**: inline timeline + track changes with authors |
| Q17 | CI | **Deferred until physically needed**: the workflow runs only manually; items close on green local verification (clean-copy Release build + tests). Re-enable on PRs/merges to `main`. |
| Q18 | Tamper evidence against privileged users (DBA, deployment pipeline) | Triggers + **ledger tables** (append-only change log, updatable ledger for document data) + digests and Azure SQL Auditing outside the pipeline's reach + nightly reconciliation — T21 |
| Q16 | Export | **PDF export** (async, cached for signed versions) — [13-export](13-export.md) |
| Q14 | Load profile | 50 k readers, 500 editors/owners, 10 k documents, 100 folders, **≤ 3 s at 20 req/s** ([12-load-and-performance](12-load-and-performance.md)) |
