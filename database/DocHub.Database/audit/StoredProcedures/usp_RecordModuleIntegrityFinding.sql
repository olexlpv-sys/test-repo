/*
Records a module-integrity finding of the API's check (T21 §4, rule 6): an audit module whose definition doesn't match the
hash generated from the DB project, can't be read, is missing or is disabled. Recorded once per module and detail.
*/
CREATE PROCEDURE [audit].[usp_RecordModuleIntegrityFinding]
    @Module NVARCHAR (256),
    @Detail NVARCHAR (1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;
    EXEC sys.sp_getapplock @Resource = N'audit.usp_ReconcileLedger', @LockMode = 'Exclusive', @LockOwner = 'Transaction';

    INSERT INTO [audit].[ReconciliationFinding] ([Kind], [TableName], [Detail])
    SELECT 'ModuleIntegrity', @Module, @Detail
    WHERE NOT EXISTS (SELECT 1 FROM [audit].[ReconciliationFinding]
                      WHERE [Kind] = 'ModuleIntegrity' AND [TableName] = @Module AND [Detail] IS NOT DISTINCT FROM @Detail);

    COMMIT TRANSACTION;
END;
