/*
Who is changing data right now — evaluated once per audited statement by the audit triggers.
  Source = 'App' only when the API context is complete AND the caller is the API (member of app_api, or db_owner for
  local development). Support scripts (support_writer) are always 'Script', even if they set UserId themselves.
  Session-context values are converted with TRY_CONVERT so malformed values never block a data change.
*/
CREATE FUNCTION [audit].[fn_ChangeContext] ()
RETURNS TABLE
AS
RETURN
    SELECT
        [c].[UserId],
        CAST(CASE
                 WHEN [c].[UserId] IS NOT NULL AND [c].[Ticket] IS NULL
                      AND (IS_ROLEMEMBER(N'app_api') = 1 OR IS_ROLEMEMBER(N'db_owner') = 1) THEN 'App'
                 ELSE 'Script'
             END AS VARCHAR (10)) AS [Source],
        CAST(ORIGINAL_LOGIN() AS NVARCHAR (128)) AS [DbLogin],
        CAST(APP_NAME() AS NVARCHAR (128)) AS [AppName],
        [c].[CorrelationId],
        [c].[OperationContext],
        [c].[Ticket],
        [c].[Reason]
    FROM (SELECT
              TRY_CONVERT(INT, SESSION_CONTEXT(N'UserId')) AS [UserId],
              TRY_CONVERT(NVARCHAR (64), SESSION_CONTEXT(N'CorrelationId')) AS [CorrelationId],
              TRY_CONVERT(NVARCHAR (50), SESSION_CONTEXT(N'OperationContext')) AS [OperationContext],
              TRY_CONVERT(NVARCHAR (50), SESSION_CONTEXT(N'Ticket')) AS [Ticket],
              TRY_CONVERT(NVARCHAR (500), SESSION_CONTEXT(N'Reason')) AS [Reason]) AS [c];
