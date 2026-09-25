CREATE TABLE [app].[ContentStyle]
(
    [Id]             INT            IDENTITY (1, 1) NOT NULL,
    [StyleId]        VARCHAR (50)   NOT NULL,
    [Name]           NVARCHAR (100) NOT NULL,
    [Kind]           TINYINT        NOT NULL,
    [BasedOnStyleId] VARCHAR (50)   NULL,
    [PropertiesJson] NVARCHAR (MAX) NOT NULL,
    [IsBuiltIn]      BIT            NOT NULL CONSTRAINT [DF_ContentStyle_IsBuiltIn] DEFAULT (0),
    [IsActive]       BIT            NOT NULL CONSTRAINT [DF_ContentStyle_IsActive] DEFAULT (1),
    [RowVersion]     ROWVERSION     NOT NULL,
    CONSTRAINT [PK_ContentStyle] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_ContentStyle_StyleId] UNIQUE ([StyleId]),
    CONSTRAINT [FK_ContentStyle_BasedOn] FOREIGN KEY ([BasedOnStyleId]) REFERENCES [app].[ContentStyle] ([StyleId]),
    -- 1 Paragraph, 2 Character, 3 Table
    CONSTRAINT [CK_ContentStyle_Kind] CHECK ([Kind] >= 1 AND [Kind] <= 3),
    CONSTRAINT [CK_ContentStyle_PropertiesJson] CHECK (ISJSON([PropertiesJson]) = 1),
    CONSTRAINT [CK_ContentStyle_NotBasedOnSelf] CHECK ([BasedOnStyleId] IS NULL OR [BasedOnStyleId] <> [StyleId])
);
