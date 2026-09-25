-- The change log is append-only for everyone except dbo (triggers write through ownership chaining).
DENY UPDATE, DELETE ON OBJECT::[audit].[ChangeLog] TO [public];
GO
-- The version stamp (cache key/ETag, tamper flag) is maintained only by the audit triggers; nobody may change it directly.
DENY INSERT, UPDATE, DELETE ON OBJECT::[app].[VersionStamp] TO [public];
GO
-- Style usage is derived from ContentJson by the API; support scripts change ContentJson and the API rebuilds the usage.
DENY INSERT, UPDATE, DELETE ON OBJECT::[app].[ContentStyleUsage] TO [support_writer];
GO
GRANT EXECUTE ON OBJECT::[audit].[usp_SetSupportContext] TO [support_writer];
GO
-- Ledger reconciliation (T21 §4): the API runs it and records module-integrity findings.
GRANT EXECUTE ON OBJECT::[audit].[usp_ReconcileLedger] TO [app_api];
GO
GRANT EXECUTE ON OBJECT::[audit].[usp_RecordModuleIntegrityFinding] TO [app_api];
GO
-- Ledger views/verification and module definitions (rule 6) need database permissions ownership chaining doesn't cover.
GRANT VIEW LEDGER CONTENT TO [ledger_reader];
GO
GRANT VIEW DATABASE STATE TO [ledger_reader];
GO
GRANT VIEW DEFINITION ON SCHEMA::[app] TO [ledger_reader];
GO
GRANT VIEW DEFINITION ON SCHEMA::[audit] TO [ledger_reader];
GO
-- The content-hash cache is written only by the API (T07); scripts change content and the API recomputes the hash.
DENY INSERT, UPDATE, DELETE ON OBJECT::[app].[VersionContentHash] TO [support_writer];
