CREATE TABLE [app].[NodeType]
(
    [Id]          INT            IDENTITY (1, 1) NOT NULL,
    [Code]        VARCHAR (50)   NOT NULL,
    [Name]        NVARCHAR (100) NOT NULL,
    [Description] NVARCHAR (500) NULL,
    [SortOrder]   INT            NOT NULL CONSTRAINT [DF_NodeType_SortOrder] DEFAULT (0),
    [IsActive]    BIT            NOT NULL CONSTRAINT [DF_NodeType_IsActive] DEFAULT (1),
    [RowVersion]  ROWVERSION     NOT NULL,
    CONSTRAINT [PK_NodeType] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_NodeType_Code] UNIQUE ([Code]),
    CONSTRAINT [CK_NodeType_Name_NotEmpty] CHECK (LEN([Name]) > 0)
);
