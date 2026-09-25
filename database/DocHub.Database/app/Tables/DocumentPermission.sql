CREATE TABLE [app].[DocumentPermission]
(
    [Id]              INT              IDENTITY (1, 1) NOT NULL,
    [DocumentId]      INT              NOT NULL,
    [UserId]          INT              NOT NULL,
    -- 1 Editor, 2 Approver. The owner is Document.OwnerUserId and never stored here.
    [Role]            TINYINT          NOT NULL,
    -- NULL = whole document; a node scope is only allowed for editors.
    [LogicalNodeId]   UNIQUEIDENTIFIER NULL,
    [GrantedAt]       DATETIME2 (3)    NOT NULL CONSTRAINT [DF_DocumentPermission_GrantedAt] DEFAULT (SYSUTCDATETIME()),
    [GrantedByUserId] INT              NOT NULL,
    CONSTRAINT [PK_DocumentPermission] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_DocumentPermission_Document] FOREIGN KEY ([DocumentId]) REFERENCES [app].[Document] ([Id]),
    CONSTRAINT [FK_DocumentPermission_User] FOREIGN KEY ([UserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [FK_DocumentPermission_GrantedBy] FOREIGN KEY ([GrantedByUserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [CK_DocumentPermission_Role] CHECK ([Role] >= 1 AND [Role] <= 2),
    CONSTRAINT [CK_DocumentPermission_NodeScopeOnlyEditor] CHECK ([LogicalNodeId] IS NULL OR [Role] = 1)
);
GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_DocumentPermission_Document]
    ON [app].[DocumentPermission] ([DocumentId], [UserId], [Role]) WHERE [LogicalNodeId] IS NULL;
GO
CREATE UNIQUE NONCLUSTERED INDEX [UX_DocumentPermission_Node]
    ON [app].[DocumentPermission] ([DocumentId], [UserId], [Role], [LogicalNodeId]) WHERE [LogicalNodeId] IS NOT NULL;
GO
CREATE NONCLUSTERED INDEX [IX_DocumentPermission_User_Document]
    ON [app].[DocumentPermission] ([UserId], [DocumentId]) INCLUDE ([Role], [LogicalNodeId]);
GO
CREATE NONCLUSTERED INDEX [IX_DocumentPermission_Document_Role]
    ON [app].[DocumentPermission] ([DocumentId], [Role]);
