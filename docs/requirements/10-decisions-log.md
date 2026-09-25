# Decisions log & open questions

> Part of the [requirements set](README.md). IDs are stable — tasks and tests reference them.

Every answer from the product owner is recorded here; the affected requirement files are updated in the same commit.

## Open questions

| # | Question | Current assumption |
|---|---|---|
| Q13 | Search: substring (`LIKE`) over titles and node text, or linguistic full-text search (stemming, ranking)? | Substring, case/accent-insensitive (FR-D6); full-text can replace it later behind the same procedure contract |

## Assumptions made by the spec (confirm or override)
| # | Assumption |
|---|---|
| A-1 | The document title can be changed only while a draft exists (the title is versioned together with the draft's content). |
| A-2 | A draft can be discarded only when a signed version exists; a never-signed document is deleted instead of discarded. |
| A-3 | Editing a draft makes earlier approver signatures outdated; approvers must sign again. |
| A-4 | Admins may move deleted documents (to be able to empty and delete folders). |


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
