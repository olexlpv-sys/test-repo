# Development process

Applies to every item in the [work queue](work-queue.md). Short on purpose — read it once per session.

## 1. Language
Everything stored in the repository is **English**: code, identifiers, comments, commit messages, PR texts, docs, UI strings, test names, seed data.

## 2. Artifacts and their review gates
**Every artifact is reviewed before its queue item can be `done`.** Author and reviewer are different (a human or a fresh agent session without the author's context).

| Artifact | Examples | Reviewer checks |
|---|---|---|
| REQ | `docs/requirements/*.md`, decisions log | complete, unambiguous, consistent with other REQ files, every ID testable |
| SPEC | task files, `architecture.md`, `content-format.md` | every referenced REQ ID covered by scope + acceptance criteria; contracts consistent across tasks; no contradiction with decisions log |
| DB | SQL project objects, seed, support-script templates | constraints enforce the rules, triggers audit every path, idempotent deploy |
| CODE | API, infrastructure, SPA | correctness against the task's acceptance criteria and REQ IDs, security, authorization, concurrency |
| TEST | test code, fixtures | each acceptance criterion has at least one test that would fail without the feature; no flaky/sleep-based tests |

## 3. Review loop — "until green"
1. Author opens a PR: code + tests + updated docs; CI green.
2. Reviewer produces findings (template §5). Each finding is classified **valid** or **not valid** (§4).
3. Author fixes **all valid findings**, each with a regression test that fails before the fix; not-valid findings are closed with a one-line reason.
4. Reviewer re-reviews **only the delta** and the previously open findings.
5. Repeat until **green**: zero open valid findings **and** CI green. There is no round limit.

## 4. Defect validity rule
A finding is a **valid defect only if it is reachable by a user** through a supported entry point:
- a Web UI action;
- a public API call by any authenticated (seeded) user, including hostile input;
- a support-team SQL script against the documented schema (the support team is a user of the DB — e.g. an audit gap for script changes is a valid defect);
- a documented deployment/configuration step.

A valid finding **must** state: entry point, steps to reproduce, expected vs actual, affected REQ ID. Without a concrete reproduction path it is **not valid** — this includes style preferences, hypothetical future use, unreachable code paths, internal invariants that no entry point can break, and "could be cleaner" refactorings. Such notes may be recorded but never block.

For REQ/SPEC artifacts, a finding is valid when it would lead an implementer to build **user-visible** behavior wrongly (contradiction, ambiguity, missing acceptance criterion for a REQ ID).

Severity (all valid findings must be fixed before green): **Blocker** — data loss/corruption, security, wrong permissions, audit gap, signed content changeable via API · **Major** — requirement violated / wrong result · **Minor** — UX glitch with a workaround.

## 5. Review request template (keep it this small)
```
Review <artifact type> for queue item <Q-id>.
Inputs: PR diff (or file list), task file <path>, REQ files listed in the task's "Read first".
Apply docs/process.md §4. Output only a table:
| # | Severity | Valid? | Entry point | Steps | Expected / Actual | REQ |
End with: GREEN or NOT GREEN.
```

## 6. Context & token economy
- **One queue item per session / agent.** Start from [work-queue.md](work-queue.md); load only the task file and its **Read first** list. Do not read other task files or the whole `docs/` folder.
- Locate code with search (grep/glob) instead of reading whole files; read only the relevant ranges.
- Cross-layer contract = the committed OpenAPI snapshot (`src/DocHub.Api/openapi.v1.json`) — frontend items read it, not backend code.
- Reviewers get the diff + task file + REQ files, never the whole repo. Re-reviews get only the delta.
- When running builds/tests, surface only failures (tail of the output), not full logs.
- Handoff note in the queue: **≤ 5 lines** (what was done, deviations, follow-ups). No long summaries elsewhere.
- Docs stay terse and link by ID instead of repeating text. A decision is written once (decisions log) and referenced.
- Stable interfaces (`IVersionGuard`, `IDocumentAuthorization`, diff engine, content schema) are the seams that let items run in parallel without loading each other's context.

## 7. Definition of Done
See [execution plan §5](tasks/00-execution-plan.md#5-definition-of-done-applies-to-every-task) + review green (§3) + tests per [testing strategy](testing-strategy.md).

## 8. Git
One branch + PR per queue item, conventional commit messages (`feat(api): …`, `test(db): …`, `docs(req): …`). The PR description lists the queue item, REQ IDs and the review result table.
