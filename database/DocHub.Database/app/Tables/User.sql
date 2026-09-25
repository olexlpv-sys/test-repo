CREATE TABLE [app].[User]
(
    [Id]          INT            IDENTITY (1, 1) NOT NULL,
    [Login]       NVARCHAR (100) NOT NULL,
    [DisplayName] NVARCHAR (200) NOT NULL,
    [Email]       NVARCHAR (256) NULL,
    [IsAdmin]     BIT            NOT NULL CONSTRAINT [DF_User_IsAdmin] DEFAULT (0),
    [IsActive]    BIT            NOT NULL CONSTRAINT [DF_User_IsActive] DEFAULT (1),
    CONSTRAINT [PK_User] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_User_Login] UNIQUE ([Login]),
    CONSTRAINT [CK_User_Login_NotEmpty] CHECK (LEN([Login]) > 0),
    CONSTRAINT [CK_User_DisplayName_NotEmpty] CHECK (LEN([DisplayName]) > 0)
);
GO
CREATE NONCLUSTERED INDEX [IX_User_IsActive]
    ON [app].[User] ([IsActive]) INCLUDE ([DisplayName]);
