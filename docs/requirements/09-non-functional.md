# Non-functional requirements

> Part of the [requirements set](README.md). IDs are stable — tasks and tests reference them.

- NFR-1 .NET 10 LTS; Azure SQL Database (compatibility level 160).
- NFR-2 OpenAPI description of the API with an interactive UI (Scalar) in Development.
- NFR-3 Errors returned as RFC 9457 `ProblemDetails` with stable `type` codes.
- NFR-4 Optimistic concurrency on all editable entities (`rowversion`); conflicting updates return `409`.
- NFR-5 Rich text is **validated server-side** against the content schema (allow-list of elements/attributes/values) and HTML is generated server-side with escaped text, preventing stored XSS.
- NFR-6 Tree operations must handle documents with ≥ 2 000 nodes and depth ≥ 15 with tree load < 500 ms (local DB).
- NFR-7 Integration tests run against a real SQL Server (Testcontainers) with the DACPAC deployed.
