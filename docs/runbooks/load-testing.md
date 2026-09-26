# Load and performance testing (T19)

How to generate production-like data, run the NFR-L3 traffic mix, probe the UI under load and read the reports.
Requirements: [NFR-L1 … L12](../requirements/12-load-and-performance.md). Environment profiles: decisions log Q19 (NFR-L4a).

## 1. Generate data — `tools/DocHub.DataGen`
```bash
dotnet sqlpackage /Action:Publish /SourceFile:database/DocHub.Database/bin/Debug/DocHub.Database.dacpac /TargetConnectionString:"<fresh database>"
dotnet run --project tools/DocHub.DataGen -- --connection "<same database, db_owner>" --scale 0.1 --seed 42
```
- `--scale 1.0` is the NFR-L2 volume (50 500 users, 100 folders, 10 000 documents × 5 versions, ~300 nodes each, max 2 000,
  depth ≤ 15, 10 grants per document, 20 comments per version); `0.1` is the nightly size. The same seed gives the same data.
- Bulk copies bypass the audit triggers. The generator writes one synthetic audit row per entity, the version stamps and the
  cached version hashes, validates every constraint once (they stay trusted) and inserts the **reconciliation baseline** — so it
  refuses a database that already has one (generate right after the deployment).
- Disk: plan ~25 GB per 0.1 of scale (content is stored three times — JSON, HTML, plain text — plus the audit copy).
- Measured on the development environment (4 vCPU, SQL Server in a container): ~2 300 rows/s (scale 0.003 in 1 min, i.e.
  ~35 min for 0.1). SQL Server spends one core on the load there; the < 5 min (0.1) and < 30 min (1.0) targets must be measured
  on the NFR-L11 Azure SQL tier.

## 2. Run the traffic mix — `tests/DocHub.LoadTests`
```bash
# In process on a generated container database (scale via DOCHUB_LOAD_SCALE, default 0.001):
DOCHUB_LOAD=1 dotnet test --project tests/DocHub.LoadTests -- --filter-class "DocHub.LoadTests.LoadRunTests"
# Against a deployed stack whose database was generated as in §1:
DOCHUB_LOAD_PROFILE=production DOCHUB_LOAD_TARGET=https://api.example DOCHUB_LOAD_CONNECTION="<its database>" \
  dotnet test --project tests/DocHub.LoadTests -- --filter-class "DocHub.LoadTests.LoadRunTests"
```
| `DOCHUB_LOAD_PROFILE` | Load | Pass criteria |
|---|---|---|
| `small` (default) | 2 concurrent users, 60 s | p99 ≤ 10 s per endpoint, 0 failures (NFR-L4a) |
| `production` | 20 req/s, 30 min | p99 ≤ 3 s per endpoint, 0 failures (NFR-L4) |
| `burst` | 40 req/s, 1 min | 0 failures, 0 × 5xx |
| `soak` | 20 req/s, 1 h | as production, and p95 of the last 10 min ≤ 1.1 × the first 10 min |

`DOCHUB_LOAD_DURATION` (seconds) shortens a profile. The mix (NFR-L3): 70 % readers (folder list with title filter → open →
tree → 5 nodes), 20 % editors (open draft → 10 autosaves every 3 s → node history → compare with the latest signed version),
10 % other (comments, sign + withdraw, admin audit reads, PDF export ≈ 1 %). Half way through, the ledger reconciliation runs:
it must finish in < 10 min with 0 findings, and the requests around it must stay within the limit.

- **The API must connect with an `app_api` login** (as in production). Connected as `sa`/db_owner, its audit rows are reported
  as forged (`ForgedAuditRow`) by the reconciliation.
- Reports (`DOCHUB_LOAD_REPORTS`, default `load-reports/` next to the test binaries): NBomber HTML/CSV/MD, `endpoints.csv`
  (requests, failures, 5xx, p50/p95/p99 per endpoint) and `top-queries.csv` (the 10 slowest statements, needs VIEW SERVER STATE).
  Keep them as build artifacts. On Azure SQL, also read Query Store.
- NBomber is free for personal use; running it for an organization needs an NBomber license (`NBomberRunner.WithLicense`).

## 3. Probe the UI under load (NFR-L6)
With the production build (`npm run build && npx vite preview`) against the loaded stack, while §2 runs:
```bash
DOCHUB_LOAD_PROBE=1 DOCHUB_WEB=http://localhost:4173 DOCHUB_API=<api> npx playwright test e2e/load-probe.spec.ts
```
20 probes of "main window ready" and "document form first section visible"; the p95 of each must be ≤ 3 s.

## 4. Telemetry for tuning
Set `OTEL_EXPORTER_OTLP_ENDPOINT` (or `OpenTelemetry:Enabled=true`) on the API: request traces, SQL command traces and
runtime metrics (GC, memory — the soak's memory trend) go to the OTLP collector.

## 5. Results
Every NFR-L5 budget miss becomes a defect in [the work queue](../work-queue.md) (user-reachable: users see slow responses).

| Date | Environment | Data | Profile | Result |
|---|---|---|---|---|
| 2026-09-26 | dev container (4 vCPU) | scale 0.001 | small, 3 min, external stack incl. PDF export | pass: p99 ≤ 0.7 s every endpoint, 0 failures; reconciliation 0 findings; NFR-L6 probe p95 0.65 s / 1.25 s |
