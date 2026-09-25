# T12 — Version comparison API

| | |
|---|---|
| **Depends on** | T09 (content), T11 §3 diff engine (or builds it) |
| **Blocks** | T16 (compare view) |
| **Size** | M ([sizing](../process.md#9-sizing-and-agent-effort)) |
| **Requirements** | FR-C1, FR-C2 |
| **Read first** (nothing else) | [05-version-comparison](../requirements/05-version-comparison.md) · [content-format](../content-format.md) · [architecture](../architecture.md) (only sections linked in the text) · [06-permissions](../requirements/06-permissions.md) (FR-P5) · [12-load-and-performance](../requirements/12-load-and-performance.md) (NFR-L9) · [process](../process.md) |

## Goal
Compare two versions of the same document — any pair of signed versions, or a signed version vs the current draft — at structure and content level.

## API
| Method | Route | Notes |
|---|---|---|
| GET | `/api/documents/{id}/compare?base={versionId}&target={versionId}&includeUnchanged=false` | `target` may be the literal `draft` (current draft) and `base` may be `latestSigned` |
| GET | `/api/documents/{id}/compare/nodes/{logicalNodeId}?base=…&target=…` | content diff for one node (same format as T11 `/diff`) |

`base` and `target` must belong to document `{id}` (`400` otherwise); comparing a version with itself returns no changes.

### Response
```json
{
  "base":   { "versionId": 10, "label": "v1" },
  "target": { "versionId": 14, "label": "Draft (based on v2)" },
  "summary": { "added": 3, "removed": 1, "moved": 2, "renamed": 1, "typeChanged": 0, "contentChanged": 5, "unchanged": 40 },
  "tree": [
    {
      "logicalNodeId": "…",
      "status": "Modified",
      "changes": ["Renamed", "ContentChanged"],
      "base":   { "title": "Section 1", "number": "1.1", "nodeType": "SECTION", "parentLogicalNodeId": "…" },
      "target": { "title": "Scope",     "number": "1.1", "nodeType": "SECTION", "parentLogicalNodeId": "…" },
      "contentStats": { "inserted": 12, "deleted": 3 },
      "children": [ … ]
    }
  ]
}
```
`status` ∈ `Added, Removed, Modified, Unchanged`; `changes` ⊆ `Renamed, TypeChanged, Moved, Reordered, ContentChanged`.

## Algorithm
1. Load both trees (T08 loader) with `ContentHash` (not HTML).
2. Match nodes by `LogicalNodeId` (full outer join).
3. Per matched pair: compare `Title`, `NodeTypeId`, parent `LogicalNodeId` (→ `Moved`), sibling order (→ `Reordered`, only when relative order among matched siblings changed — use LCS so one insertion doesn't mark all siblings), `ContentHash` (→ `ContentChanged`).
4. Build the **merged tree**: target structure, with removed nodes inserted at their base position (under their base parent if that still exists, else under the nearest existing ancestor) so the UI can render one tree.
5. `contentStats` computed with the diff engine only for `ContentChanged` nodes (lazy: the full diff is fetched per node via the second endpoint).

## Caching
Compare results of two **Signed** versions are cached (`HybridCache`, key = both version ids + both `VersionStamp.LastChangeLogId` values; `EnsureCanView` first) — NFR-L9.

## Acceptance criteria
- [ ] v1 vs v2 with: 1 added node, 1 removed subtree, 1 moved node, 1 renamed node, 2 content edits → summary and per-node statuses exactly match.
- [ ] Inserting one node at the top of 20 siblings does **not** mark the other 19 as `Reordered`.
- [ ] `target=draft` works; when no draft exists → `404`.
- [ ] Versions of another document → `400`.
- [ ] Compare of two 2 000-node versions returns in < 1 s locally (without per-node diffs).
- [ ] A deleted document's compare endpoints → `404` for non-owner/non-admin users, `200` for owner and admin (FR-P5, via `EnsureCanView`).
