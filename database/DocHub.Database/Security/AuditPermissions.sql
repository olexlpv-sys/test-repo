-- The change log is append-only for everyone except dbo (triggers write through ownership chaining).
DENY UPDATE, DELETE ON OBJECT::[audit].[ChangeLog] TO [public];
GO
GRANT EXECUTE ON OBJECT::[audit].[usp_SetSupportContext] TO [support_writer];
