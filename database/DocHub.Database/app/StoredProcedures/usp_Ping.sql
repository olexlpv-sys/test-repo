/*
Pattern for stored procedures called through IDbProcedures (ADR-09): result set 1 echoes the session context set by the
API connection interceptor, result set 2 returns the server time. Used by health/diagnostic tests.
*/
CREATE PROCEDURE [app].[usp_Ping]
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [c].[UserId], [c].[CorrelationId], [c].[Source]
    FROM [audit].[fn_ChangeContext]() AS [c];

    SELECT SYSUTCDATETIME() AS [ServerTimeUtc];
END;
