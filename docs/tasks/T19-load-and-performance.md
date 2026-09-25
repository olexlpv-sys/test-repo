# T19 — Load & performance test harness and tuning

| | |
|---|---|
| **Depends on** | T07, T08, T09 (harness); T10–T15, T18 (full scenario mix, UI probe) |
| **Size** | L (4 days: 2.5 harness + 1.5 tuning) |
| **Requirements** | NFR-L1 … NFR-L12, NFR-6 |
| **Read first** (nothing else) | [12-load-and-performance](../requirements/12-load-and-performance.md) · [11-data-access](../requirements/11-data-access.md) · [testing strategy](../testing-strategy.md) · [process](../process.md) |

## Goal
Prove NFR-L4 (p99 ≤ 3 s at 20 req/s on production-scale data) and keep proving it on every nightly build.

## Scope
1. **Data generator** `tools/DocHub.DataGen` (.NET console app): builds the NFR-L2 volume with set-based inserts (`SqlBulkCopy`) into a deployed DACPAC database. Deterministic seed, configurable scale (`--scale 0.1` for CI, `1.0` for the full test). It creates 50 500 users, 100 folders, 10 000 documents with 5 versions each, realistic tree shapes (depth distribution, 1–2 000 nodes), content produced from the Content Schema fixtures (with derived columns and `ContentStyleUsage` rows), grants, comments and signatures. The audit triggers are disabled during the bulk load, and the generator writes one synthetic audit row per entity so history queries have data.
2. **Load scenarios** with **NBomber** (`tests/DocHub.LoadTests`), following the NFR-L3 mix:
   - Reader: list folder → open document → read 5 nodes → search.
   - Editor: open draft → autosave content every 3 s × 10 → view node history → compare with latest signed.
   - Other: comment, sign/withdraw, admin reads, **PDF export** (≈ 1 % of traffic, T20).
   - Users are picked from the generated population through the test-mode `X-User-Id` header.
3. **Runs:**
   - Nightly at scale 0.1 in CI (a SQL container, smoke thresholds).
   - Before release at scale 1.0 against Azure SQL (NFR-L11 tier): a 20 req/s 30-minute run, a 40 req/s 1-minute burst, and a 1-hour soak.
   - Reports (HTML + a CSV of p50/p95/p99 per endpoint) are stored as build artifacts.
4. **Observability used for tuning:** OpenTelemetry traces and metrics from the API (`AddAspNetCoreInstrumentation`, EF Core / SqlClient instrumentation). Query Store is enabled in Azure SQL. The top 10 slowest queries are listed in the report.
5. **Tuning backlog:** every NFR-L5 budget miss becomes a defect in the work queue. These are user-reachable, because users see slow responses, so they are valid under process §4.

## Acceptance criteria
- [ ] The generator creates the full NFR-L2 volume in < 30 min, and scale 0.1 in < 5 min.
- [ ] Nightly scale-0.1 run passes its thresholds (p99 ≤ 3 s, 0 % errors).
- [ ] Full-scale run: p99 ≤ 3 s for every endpoint at 20 req/s, and the burst at 40 req/s has 0 % 5xx.
- [ ] Soak: p95 drifts by no more than +10 % between the first and the last 10 minutes; no memory growth trend in the API.
- [ ] Every NFR-L5 budget is met, or a defect is filed and linked in the work queue.
- [ ] **NFR-L6**: while the 20 req/s load runs, a Playwright probe measures main window ready and document form first-section visible — both ≤ 3 s (p95 of 20 probes).
