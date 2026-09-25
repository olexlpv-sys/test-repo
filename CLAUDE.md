# CLAUDE.md

- Everything in this repo is **English** (code, comments, docs, commits, UI strings).
- Start every session from `docs/work-queue.md`: take one item, follow its protocol.
- Load only the item's task file and its **Read first** list. Do not read other task files or all of `docs/`.
- Rules for reviews, defect validity (only user-reachable defects count) and "review until green": `docs/process.md`.
- Test requirements (integration tests against real SQL Server are mandatory): `docs/testing-strategy.md`.
- Schema changes only in `database/DocHub.Database` (no EF migrations).
- Show only failing output from builds/tests.

## Commands
- Build: `dotnet build DocHub.slnx` (warnings are errors)
- Test: `dotnet test --solution DocHub.slnx` (Microsoft.Testing.Platform + xUnit v3; filter e.g. `-- --filter-trait "Category=Performance"`)
- Run API: `dotnet run --project src/DocHub.Api` → http://localhost:5080 (`/health`, `/openapi/v1.json`, `/scalar`)
- Local SQL Server: `cp .env.example .env && docker compose up -d`
- Package versions live only in `Directory.Packages.props`.
- DB publish: added by Q02/T02.
