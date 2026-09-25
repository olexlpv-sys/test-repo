-- The API: CRUD and stored procedures in [app], read access to [audit] (history endpoints).
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[app] TO [app_api];
GO
GRANT SELECT ON SCHEMA::[audit] TO [app_api];
GO
-- Support team scripts: data changes in [app], read access to [audit].
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[app] TO [support_writer];
GO
GRANT SELECT ON SCHEMA::[audit] TO [support_writer];
GO
GRANT SELECT ON SCHEMA::[app] TO [readonly];
GO
GRANT SELECT ON SCHEMA::[audit] TO [readonly];
