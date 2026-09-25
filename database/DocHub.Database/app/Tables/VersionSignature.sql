CREATE TABLE [app].[VersionSignature]
(
    [Id]                INT             IDENTITY (1, 1) NOT NULL,
    [DocumentVersionId] INT             NOT NULL,
    [UserId]            INT             NOT NULL,
    [SignedAt]          DATETIME2 (3)   NOT NULL CONSTRAINT [DF_VersionSignature_SignedAt] DEFAULT (SYSUTCDATETIME()),
    -- Hash of the draft content when signing; the signature is valid only while it equals the current hash (FR-V6).
    [ContentHash]       VARBINARY (32)  NOT NULL,
    [WithdrawnAt]       DATETIME2 (3)   NULL,
    [Comment]           NVARCHAR (1000) NULL,
    CONSTRAINT [PK_VersionSignature] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [FK_VersionSignature_Version] FOREIGN KEY ([DocumentVersionId]) REFERENCES [app].[DocumentVersion] ([Id]),
    CONSTRAINT [FK_VersionSignature_User] FOREIGN KEY ([UserId]) REFERENCES [app].[User] ([Id]),
    CONSTRAINT [CK_VersionSignature_ContentHash] CHECK (DATALENGTH([ContentHash]) = 32),
    CONSTRAINT [CK_VersionSignature_Withdrawn] CHECK ([WithdrawnAt] IS NULL OR [WithdrawnAt] >= [SignedAt])
);
GO
-- One active signature per approver and version.
CREATE UNIQUE NONCLUSTERED INDEX [UX_VersionSignature_Active]
    ON [app].[VersionSignature] ([DocumentVersionId], [UserId]) WHERE [WithdrawnAt] IS NULL;
