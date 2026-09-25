IF NOT EXISTS (SELECT 1 FROM [app].[Folder])
BEGIN
    SET IDENTITY_INSERT [app].[Folder] ON;

    INSERT INTO [app].[Folder] ([Id], [ParentFolderId], [Name], [SortOrder], [CreatedByUserId]) VALUES
        (1, NULL, N'General',   1024, 1),
        (2, NULL, N'Templates', 2048, 1);

    SET IDENTITY_INSERT [app].[Folder] OFF;
END;
