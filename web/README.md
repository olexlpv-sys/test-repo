# DocHub web

React 19 + TypeScript (strict) + Mantine 9, TanStack Query, React Router; API client generated from `../src/DocHub.Api/openapi.v1.json`.

```bash
npm ci
npm run dev          # http://localhost:5173, proxies /api to DOCHUB_API or http://localhost:5080
npm test             # Vitest + Testing Library (fake API, no server needed)
npm run lint && npm run format:check
npm run build
npm run gen:api      # after an API contract change (the snapshot is updated by the API tests)
```

**Smoke test (Playwright)** — needs the running stack: SQL Server (`docker compose up -d`), the published database (see `database/README.md`), the API in test auth mode (`dotnet run --project src/DocHub.Api`) and `npm run dev`:

```bash
npm run e2e                                              # DOCHUB_WEB overrides http://localhost:5173
PLAYWRIGHT_CHROMIUM=/opt/pw-browsers/chromium npm run e2e # use a preinstalled Chromium
```

**Styling**: design tokens and the card/chip/tab classes live in `src/styles.css`; Mantine overrides (brand blue `#1e5bdc`, `variant="soft"` buttons, inputs, modals) in `src/theme.ts`.
