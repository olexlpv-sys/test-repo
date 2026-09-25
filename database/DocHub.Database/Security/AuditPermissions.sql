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
