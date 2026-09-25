# Work queue

The single source of "what's next". Process rules: [process.md](process.md). Dependencies & rationale: [execution plan](tasks/00-execution-plan.md).

## Protocol (per item)
1. Take the **first `todo` item whose dependencies are `done`** (items with **different** lane letters can run in parallel in separate agent sessions; items within one lane run in lane order).
2. Set `in-progress`, load **only** the task file + its *Read first* list.
3. Implement with tests ([testing strategy](testing-strategy.md)); **local verification green**: `dotnet build DocHub.slnx -c Release` + `dotnet test --solution DocHub.slnx -c Release` on a clean copy of the tree (CI is deferred — decisions log Q17).
4. Set `in-review`; request review with the template in [process §5](process.md#5-review-request-template-keep-it-this-small) in a fresh context.
5. Fix valid findings → re-review delta → repeat until **GREEN**.
6. Set `done`, write a handoff note (≤ 5 lines) in the table below, including actuals: wall-clock, review rounds (process §9).

Status: `todo` · `in-progress` · `in-review` · `done` · `blocked`

## Queue

| # | Item | Artifact | Depends on | Lane | Status | Review |
|---|---|---|---|---|---|---|
| Q00 | Review requirements & task specs (this doc set) | REQ + SPEC | — | – | done | **GREEN** after 7 rounds |
| Q01 | [T01](tasks/T01-api-solution-setup.md) API solution | CODE | Q00 | A | done | **GREEN** (2 rounds) |
| Q02 | [T02](tasks/T02-database-project-and-schema.md) DB project & schema | DB + TEST | Q01 | A | done | **GREEN** (2 rounds) |
| Q03 | [T03](tasks/T03-database-change-tracking.md) Audit triggers | DB + TEST | Q02 | A | in-review | – |
| Q04 | [T04](tasks/T04-api-foundation.md) API foundation | CODE + TEST | Q01, Q03 | A | todo | – |
| Q05 | [T05](tasks/T05-users-and-node-types-api.md) Users, node types, content styles | CODE + TEST | Q04 | B | todo | – |
| Q06 | [T06](tasks/T06-virtual-folders-api.md) Folders | CODE + TEST | Q04 | B | todo | – |
| Q07 | [T07](tasks/T07-documents-and-versions-api.md) Documents, signing, restore | CODE + TEST | Q04, Q06 | A | todo | – |
| Q08 | [T14](tasks/T14-web-shell-folders-documents.md) Web shell & lists | CODE + TEST | Q05, Q06, Q07 | C | todo | – |
| Q09 | [T10](tasks/T10-permissions.md) Permissions | CODE + TEST | Q07 | B | todo | – |
| Q10 | [T08](tasks/T08-document-tree-api.md) Tree | CODE + TEST | Q05, Q07 | A | todo | – |
| Q11 | [T09](tasks/T09-node-content-api.md) Styled content | CODE + TEST | Q05, Q10 | A | todo | – |
| Q12 | [T17](tasks/T17-web-admin-tab.md) Web admin | CODE + TEST | Q05, Q08, Q14 | C | todo | – |
| Q13 | [T13](tasks/T13-comments-api.md) Comments | CODE + TEST | Q07, Q09 | B | todo | – |
| Q14 | [T11](tasks/T11-change-history-api.md) History + diff engine | CODE + TEST | Q03, Q11 | A | todo | – |
| Q15 | [T12](tasks/T12-version-comparison-api.md) Compare | CODE + TEST | Q11, Q14 (diff engine) | B | todo | – |
| Q16 | [T15](tasks/T15-web-document-editor.md) Web section editor + inline history | CODE + TEST | Q08, Q09, Q10, Q11, Q14 | C | todo | – |
| Q17 | [T16](tasks/T16-web-compare-comments-permissions.md) Web compare/comments/permissions | CODE + TEST | Q09, Q13, Q15, Q16 | C | todo | – |
| Q18 | [T18](tasks/T18-search.md) Search (SPs, API, UI) | DB + CODE + TEST | Q11, Q16 | B | todo | – |
| Q21 | [T20](tasks/T20-pdf-export.md) PDF export (API, worker, UI) | CODE + TEST | Q05, Q07, Q09, Q11, Q16 | B | todo | – |
| Q19 | [T19](tasks/T19-load-and-performance.md) Load & performance harness + tuning | TEST + CODE | Q11 (harness), Q13, Q14, Q15, Q16, Q18, Q21 (full mix) | B | todo | – |
| Q20 | Release-candidate E2E & NFR pass (full Playwright, full-scale load + soak, a11y, mutation report, DACPAC drift report) | TEST | Q12, Q17, Q19 | – | todo | – |

Lanes: **A** backend core / DB · **B** backend features · **C** frontend.

## Handoff notes
- **Q00** — Doc set reviewed in 7 fresh-context rounds (process §4, REQ/SPEC rule): 19 → 12 → 11 → 7 → 7 → 1 → 0 valid findings, all fixed. Product input added during review (data access via EF + SPs, load profile, in-editor history, PDF export) was reviewed in the same loop. Next: Q01.
- **Q01** — .NET 10 solution (`DocHub.slnx`), CPM, warnings-as-errors, `/health`, OpenAPI+Scalar (dev only), xUnit v3 on Microsoft.Testing.Platform (`dotnet test --solution`), docker-compose SQL 2022, CI on every push/PR (green). Deviation: SDK installed from Ubuntu apt (dot.net blocked by proxy); CI now runs on all branch pushes. Review: 2 findings fixed → GREEN. Actuals: ≈ 40 min wall-clock (incl. SDK install and one CI run), 2 review rounds.
- **Q02** — `database/DocHub.Database` (Microsoft.Build.Sql, Azure SQL): 13 tables, constraints, load-profile indexes, roles/grants, idempotent seed that never overwrites admin data. `DocHub.Testing`: Testcontainers SQL fixture + DacFx deploy (DACPAC built via the solution). 31 tests incl. redeploy drift check; CI job publishes with SqlPackage + deploy-report drift gate (green). Follow-ups: **T18 needs a SQL image with full-text** (`mssql-server-fts`; default image has none); optional demo-document seed deferred; tool manifest at repo root. Actuals: ≈ 50 min, 2 review rounds (fixed: Release DACPAC path, seed re-linking styles).
