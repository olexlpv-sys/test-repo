CREATE TABLE [app].[ContentStyleUsage]
(
    [StyleId] VARCHAR (50) NOT NULL,
    [NodeId]  INT          NOT NULL,
    CONSTRAINT [PK_ContentStyleUsage] PRIMARY KEY CLUSTERED ([StyleId], [NodeId]),
    CONSTRAINT [FK_ContentStyleUsage_NodeContent] FOREIGN KEY ([NodeId]) REFERENCES [app].[NodeContent] ([NodeId]) ON DELETE CASCADE,
    -- A style that is used by any content cannot be deleted (deactivate it instead).
    CONSTRAINT [FK_ContentStyleUsage_Style] FOREIGN KEY ([StyleId]) REFERENCES [app].[ContentStyle] ([StyleId])
);
GO
CREATE NONCLUSTERED INDEX [IX_ContentStyleUsage_Node]
    ON [app].[ContentStyleUsage] ([NodeId]);
