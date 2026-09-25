# Testing strategy

Goal: **maximum quality** — every acceptance criterion and every REQ ID is proven by automated tests that run in CI against real infrastructure. Integration tests are mandatory, not optional.

## 1. Test levels

| Level | Project / tool | What | Runs |
|---|---|---|---|
| DB | `tests/DocHub.Database.Tests` — xUnit + Testcontainers.MsSql, DACPAC deployed | every stored procedure (results, edge cases, parameters), constraints, filtered unique indexes, composite FKs, audit triggers (App vs Script, multi-row, no-op updates, cascades), `usp_SetSupportContext`, append-only audit, seed idempotency (deploy twice) | every PR |
| Unit | `tests/DocHub.Domain.Tests` — xUnit + FsCheck | domain rules, sibling ordering, tree ops, canonical hash, content schema validator/canonicalizer, diff engine, role matrix; **property-based** tests: canonicalization is idempotent, hash is stable under key order, move never creates cycles | every PR |
| API integration | `tests/DocHub.Api.Tests` — `WebApplicationFactory` + real SQL container + Respawn | every endpoint: happy path, validation `400`, `401`, `403` for each role, `404`, `409` (editability, concurrency, conflicts), audit row assertion for every write | every PR |
| Authorization matrix | part of API integration | table-driven: *every mutating endpoint × every role (owner, doc editor, node editor, approver, other, admin)* → expected status | every PR |
| SP ↔ EF equivalence | API integration | each stored procedure's output equals an EF-built reference query on generated data (list, search, permissions) | every PR |
| Contract | Verify snapshot of `/openapi/v1.json` | unintended API changes fail; SPA TS client regenerated from the snapshot | every PR |
| Frontend unit/component | Vitest + Testing Library + MSW | components, hooks, permission-dependent rendering, autosave/conflict logic, editor extensions (JSON in → JSON out) | every PR |
| E2E | Playwright against the full stack (`docker compose`: SQL + API + SPA) | milestone scenarios from the execution plan, incl. test-mode user switching (owner → approver 1 → approver 2), support-script change visible in history | every PR (smoke) + nightly (full) |
| Performance | tagged integration tests with generated data | NFR-6 tree load, draft copy, compare, SP single-request targets (FR-D5) | nightly + before release |
| Load & soak | `tools/DocHub.DataGen` + NBomber (`tests/DocHub.LoadTests`) — [T19](tasks/T19-load-and-performance.md) | NFR-L3 traffic mix at 20 req/s (burst 40), p99 ≤ 3 s, 1 h soak; scale 0.1 nightly in CI, 1.0 on Azure SQL before release | nightly (0.1) + before release (1.0) |
| Security | integration + unit | XSS/injection payload fixtures for content, links, names; authz matrix; test-mode auth disabled in Production | every PR |
| PDF visual regression | T20 — render fixtures to PDF, rasterize pages, pixel-diff against approved snapshots | page layout, styles, TOC page numbers, watermark | every PR |
| Accessibility | `@axe-core/playwright` | main window, document form, admin tab: no serious/critical violations | nightly |

## 2. Quality gates (CI fails otherwise)
- All tests green; **no skipped tests** in `main`; a flaky test is a defect (fix or delete-with-replacement in the same PR, never retry-until-green).
- Coverage (coverlet / Vitest): Domain ≥ 90 % lines, Infrastructure + Api ≥ 80 %, web ≥ 80 %; no decrease on changed files.
- Mutation testing (Stryker.NET) on `DocHub.Domain`: score ≥ 70 % (nightly, trend tracked).
- Load: nightly scale-0.1 run within thresholds; release blocked unless the full-scale run meets NFR-L4/L5.
- Warnings as errors (.NET analyzers, ESLint, SQL project build).

## 3. Rules
- **Every acceptance criterion → at least one test**, named after it; the test would fail without the feature.
- **Every valid defect → regression test first**, then the fix (see [process §4](process.md#4-defect-validity-rule)).
- Tests use the real database — no EF InMemory / SQLite substitutes.
- Test data through builders (`DocumentBuilder`, `TreeBuilder`) and the seeded users (`alice` owner, `bob` editor, `carol`/`dave` approvers, `erin` no role, `admin`).
- Time is injected (`TimeProvider`) — no sleeps; async waits in E2E use Playwright auto-waiting only.
- One shared SQL container per test run; isolation by Respawn (API) or a database per test class (DB tests).
