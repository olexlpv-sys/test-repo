SET IDENTITY_INSERT [app].[User] ON;

MERGE [app].[User] AS [target]
USING (VALUES
    (0, N'system', N'System',          NULL,                     0, 0),
    (1, N'admin',  N'Administrator',   N'admin@dochub.local',    1, 1),
    (2, N'alice',  N'Alice Anderson',  N'alice@dochub.local',    0, 1),
    (3, N'bob',    N'Bob Brown',       N'bob@dochub.local',      0, 1),
    (4, N'carol',  N'Carol Clark',     N'carol@dochub.local',    0, 1),
    (5, N'dave',   N'Dave Davis',      N'dave@dochub.local',     0, 1),
    (6, N'erin',   N'Erin Evans',      N'erin@dochub.local',     0, 1)
) AS [source] ([Id], [Login], [DisplayName], [Email], [IsAdmin], [IsActive])
ON [target].[Id] = [source].[Id]
WHEN MATCHED AND (
       [target].[Login] <> [source].[Login]
    OR [target].[DisplayName] <> [source].[DisplayName]
    OR ISNULL([target].[Email], N'') <> ISNULL([source].[Email], N'')
    OR [target].[IsAdmin] <> [source].[IsAdmin]
    OR [target].[IsActive] <> [source].[IsActive]) THEN
    UPDATE SET [Login] = [source].[Login], [DisplayName] = [source].[DisplayName], [Email] = [source].[Email],
               [IsAdmin] = [source].[IsAdmin], [IsActive] = [source].[IsActive]
WHEN NOT MATCHED BY TARGET THEN
    INSERT ([Id], [Login], [DisplayName], [Email], [IsAdmin], [IsActive])
    VALUES ([source].[Id], [source].[Login], [source].[DisplayName], [source].[Email], [source].[IsAdmin], [source].[IsActive]);

SET IDENTITY_INSERT [app].[User] OFF;
