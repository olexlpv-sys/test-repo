-- Temporal + updatable ledger table (T21): immutable history, FOR SYSTEM_TIME queries, principal of every transaction.
CREATE TABLE [app].[User]
(
    [Id]          INT            IDENTITY (1, 1) NOT NULL,
    [Login]       NVARCHAR (100) NOT NULL,
    [DisplayName] NVARCHAR (200) NOT NULL,
    [Email]       NVARCHAR (256) NULL,
    [IsAdmin]     BIT            NOT NULL CONSTRAINT [DF_User_IsAdmin] DEFAULT (0),
    [IsActive]    BIT            NOT NULL CONSTRAINT [DF_User_IsActive] DEFAULT (1),
    [ValidFrom]         DATETIME2 (7)    GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    [ValidTo]           DATETIME2 (7)    GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME ([ValidFrom], [ValidTo]),
    CONSTRAINT [PK_User] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_User_Login] UNIQUE ([Login]),
    CONSTRAINT [CK_User_Login_NotEmpty] CHECK (LEN([Login]) > 0),
    CONSTRAINT [CK_User_DisplayName_NotEmpty] CHECK (LEN([DisplayName]) > 0)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = [history].[User]), LEDGER = ON (LEDGER_VIEW = [app].[User_Ledger] (TRANSACTION_ID_COLUMN_NAME = [ledger_transaction_id], SEQUENCE_NUMBER_COLUMN_NAME = [ledger_sequence_number], OPERATION_TYPE_COLUMN_NAME = [ledger_operation_type], OPERATION_TYPE_DESC_COLUMN_NAME = [ledger_operation_type_desc])));
GO
CREATE NONCLUSTERED INDEX [IX_User_IsActive]
    ON [app].[User] ([IsActive]) INCLUDE ([DisplayName]);
GO
-- Prefix search of the user picker (T05): login is covered by UQ_User_Login.
CREATE NONCLUSTERED INDEX [IX_User_DisplayName]
    ON [app].[User] ([DisplayName]) INCLUDE ([Login], [Email], [IsAdmin]) WHERE [IsActive] = 1;
GO
CREATE NONCLUSTERED INDEX [IX_User_Email]
    ON [app].[User] ([Email]) WHERE [Email] IS NOT NULL AND [IsActive] = 1;
