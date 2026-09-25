# Change tracking (audit)

> Part of the [requirements set](README.md). IDs are stable — tasks and tests reference them.

- FR-H1 Every change to folders, documents, versions, nodes, node content, permissions and comments is recorded: who, when, what (old and new values).
- FR-H2 A `User` entity is introduced; for now users are a **hardcoded seed** in the database (no real authentication).
- FR-H3 Data may be changed directly in the database by the **support team via SQL scripts**. Such changes **must also be captured** in the change log, marked as script changes, with the DB login and an optional ticket/reason.
- FR-H4 API to view the change history of node text (per node, across versions) including a readable diff between consecutive states.
- FR-H5 **[A]** If a Signed version is modified by a script after signing, the system detects it (content hash) and flags the version as "modified after signing".
