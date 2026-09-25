/*
Document list of a folder (FR-D2, T07). Result set 1: one page of documents; result set 2: TotalCount.
  @IncludeDeleted  deleted documents are returned only to their owner and admins (FR-P5)
  @Search          case- and accent-insensitive substring of the title (FR-D6; wildcards are literal)
  @Status          'Draft' | 'Signed' | 'Deleted' (derived: Deleted if deleted, else Draft if a draft exists, else Signed)
  @SortBy          'title' | 'status' | 'modifiedAt' | 'createdAt' | 'owner' | 'latestSignedVersion'; @SortDir 'asc' | 'desc'
SignaturesSigned counts valid signatures of current approvers (content hash = the cached hash of the draft); it is NULL
when the cached hash is missing or stale (DraftHashValid = 0) and the API computes it.
*/
CREATE PROCEDURE [app].[usp_ListDocuments]
    @FolderId          INT,
    @UserId            INT,
    @IncludeDeleted    BIT            = 0,
    @IncludeSubfolders BIT            = 0,
    @Search            NVARCHAR (300) = NULL,
    @Status            VARCHAR (10)   = NULL,
    @SortBy            VARCHAR (30)   = 'title',
    @SortDir           VARCHAR (4)    = 'asc',
    @Page              INT            = 1,
    @PageSize          INT            = 50
AS
BEGIN
    SET NOCOUNT ON;

    IF @Status IS NOT NULL AND @Status NOT IN ('Draft', 'Signed', 'Deleted')
        THROW 50020, N'Unknown status filter.', 1;
    IF @SortBy NOT IN ('title', 'status', 'modifiedAt', 'createdAt', 'owner', 'latestSignedVersion')
        THROW 50021, N'Unknown sort column.', 1;
    IF @SortDir NOT IN ('asc', 'desc')
        THROW 50022, N'Unknown sort direction.', 1;
    IF @Page < 1 OR @PageSize < 1 OR @PageSize > 200
        THROW 50023, N'Invalid paging.', 1;

    DECLARE @IsAdmin BIT = COALESCE((SELECT [IsAdmin] FROM [app].[User] WHERE [Id] = @UserId AND [IsActive] = 1), 0);
    DECLARE @Pattern NVARCHAR (700) = CASE WHEN NULLIF(LTRIM(RTRIM(@Search)), N'') IS NULL THEN NULL ELSE
        N'%' + REPLACE(REPLACE(REPLACE(REPLACE(LTRIM(RTRIM(@Search)), N'\', N'\\'), N'%', N'\%'), N'_', N'\_'), N'[', N'\[') + N'%' END;

    CREATE TABLE [#Folders] ([Id] INT NOT NULL PRIMARY KEY);
    IF @IncludeSubfolders = 1
    BEGIN
        WITH [tree] AS (
            SELECT [Id], 0 AS [Depth] FROM [app].[Folder] WHERE [Id] = @FolderId
            UNION ALL
            SELECT [f].[Id], [t].[Depth] + 1 FROM [app].[Folder] AS [f] JOIN [tree] AS [t] ON [f].[ParentFolderId] = [t].[Id] WHERE [t].[Depth] < 1000)
        INSERT INTO [#Folders] ([Id]) SELECT DISTINCT [Id] FROM [tree] OPTION (MAXRECURSION 1000);
    END
    ELSE
        INSERT INTO [#Folders] ([Id]) SELECT [Id] FROM [app].[Folder] WHERE [Id] = @FolderId;

    CREATE TABLE [#Docs]
    (
        [Id]                  INT            NOT NULL PRIMARY KEY,
        [RowVersion]          BINARY (8)     NOT NULL,
        [FolderId]            INT            NOT NULL,
        [Title]               NVARCHAR (300) COLLATE DATABASE_DEFAULT NOT NULL,
        [Status]              VARCHAR (10)   COLLATE DATABASE_DEFAULT NOT NULL,
        [LatestSignedVersion] INT            NULL,
        [DraftVersionId]      INT            NULL,
        [OwnerUserId]         INT            NOT NULL,
        [OwnerDisplayName]    NVARCHAR (200) COLLATE DATABASE_DEFAULT NOT NULL,
        [CreatedAt]           DATETIME2 (7)  NOT NULL,
        [ModifiedAt]          DATETIME2 (7)  NOT NULL
    );

    INSERT INTO [#Docs]
    SELECT [d].[Id], [d].[RowVersion], [d].[FolderId], [d].[Title],
           CASE WHEN [d].[DeletedAt] IS NOT NULL THEN 'Deleted' WHEN [dr].[Id] IS NOT NULL THEN 'Draft' ELSE 'Signed' END,
           [sv].[LatestSignedVersion], [dr].[Id], [d].[OwnerUserId], [o].[DisplayName], [d].[CreatedAt],
           COALESCE([st].[LastChangedAt], [cv].[CreatedAt], [d].[CreatedAt])
    FROM [app].[Document] AS [d]
    JOIN [#Folders] AS [f] ON [f].[Id] = [d].[FolderId]
    JOIN [app].[User] AS [o] ON [o].[Id] = [d].[OwnerUserId]
    OUTER APPLY (SELECT MAX([v].[VersionNumber]) AS [LatestSignedVersion] FROM [app].[DocumentVersion] AS [v] WHERE [v].[DocumentId] = [d].[Id] AND [v].[Status] = 2) AS [sv]
    OUTER APPLY (SELECT TOP (1) [v].[Id] FROM [app].[DocumentVersion] AS [v] WHERE [v].[DocumentId] = [d].[Id] AND [v].[Status] = 1) AS [dr]
    OUTER APPLY (SELECT TOP (1) [v].[Id], [v].[CreatedAt] FROM [app].[DocumentVersion] AS [v] WHERE [v].[DocumentId] = [d].[Id] AND [v].[IsCurrent] = 1) AS [cv]
    LEFT JOIN [app].[VersionStamp] AS [st] ON [st].[DocumentVersionId] = [cv].[Id]
    WHERE ([d].[DeletedAt] IS NULL OR (@IncludeDeleted = 1 AND (@IsAdmin = 1 OR [d].[OwnerUserId] = @UserId)))
      AND (@Pattern IS NULL OR [d].[Title] COLLATE Latin1_General_CI_AI LIKE @Pattern COLLATE Latin1_General_CI_AI ESCAPE N'\');

    IF @Status IS NOT NULL
        DELETE FROM [#Docs] WHERE [Status] <> @Status;

    SELECT [d].[Id], [d].[RowVersion], [d].[FolderId], [d].[Title], [d].[Status], [d].[LatestSignedVersion], [d].[DraftVersionId],
           CAST(CASE WHEN [d].[DraftVersionId] IS NULL THEN NULL WHEN [h].[DocumentVersionId] IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS [DraftHashValid],
           CASE WHEN [d].[DraftVersionId] IS NULL OR [h].[DocumentVersionId] IS NULL THEN NULL ELSE [signed].[Count] END AS [SignaturesSigned],
           CASE WHEN [d].[DraftVersionId] IS NULL THEN NULL ELSE [required].[Count] END AS [SignaturesRequired],
           [d].[OwnerUserId], [d].[OwnerDisplayName],
           CAST(CASE WHEN [d].[OwnerUserId] = @UserId THEN 1 ELSE 0 END AS BIT) AS [IsOwner],
           CAST(CASE WHEN EXISTS (SELECT 1 FROM [app].[DocumentPermission] AS [p] WHERE [p].[DocumentId] = [d].[Id] AND [p].[UserId] = @UserId AND [p].[Role] = 1) THEN 1 ELSE 0 END AS BIT) AS [IsEditor],
           CAST(CASE WHEN EXISTS (SELECT 1 FROM [app].[DocumentPermission] AS [p] WHERE [p].[DocumentId] = [d].[Id] AND [p].[UserId] = @UserId AND [p].[Role] = 2) THEN 1 ELSE 0 END AS BIT) AS [IsApprover],
           [d].[CreatedAt], [d].[ModifiedAt]
    FROM [#Docs] AS [d]
    LEFT JOIN [app].[VersionStamp] AS [ds] ON [ds].[DocumentVersionId] = [d].[DraftVersionId]
    LEFT JOIN [app].[VersionContentHash] AS [h]
        ON [h].[DocumentVersionId] = [d].[DraftVersionId] AND [h].[ContentChangeLogId] = COALESCE([ds].[ContentChangeLogId], 0)
    OUTER APPLY (SELECT COUNT(*) AS [Count] FROM [app].[DocumentPermission] AS [p] WHERE [p].[DocumentId] = [d].[Id] AND [p].[Role] = 2) AS [required]
    OUTER APPLY (SELECT COUNT(*) AS [Count]
                 FROM [app].[VersionSignature] AS [s]
                 WHERE [s].[DocumentVersionId] = [d].[DraftVersionId] AND [s].[WithdrawnAt] IS NULL AND [s].[ContentHash] = [h].[ContentHash]
                   AND EXISTS (SELECT 1 FROM [app].[DocumentPermission] AS [p] WHERE [p].[DocumentId] = [d].[Id] AND [p].[UserId] = [s].[UserId] AND [p].[Role] = 2)) AS [signed]
    ORDER BY
        CASE WHEN @SortDir = 'asc' AND @SortBy = 'title' THEN [d].[Title] END ASC,
        CASE WHEN @SortDir = 'desc' AND @SortBy = 'title' THEN [d].[Title] END DESC,
        CASE WHEN @SortDir = 'asc' AND @SortBy = 'status' THEN [d].[Status] END ASC,
        CASE WHEN @SortDir = 'desc' AND @SortBy = 'status' THEN [d].[Status] END DESC,
        CASE WHEN @SortDir = 'asc' AND @SortBy = 'modifiedAt' THEN [d].[ModifiedAt] END ASC,
        CASE WHEN @SortDir = 'desc' AND @SortBy = 'modifiedAt' THEN [d].[ModifiedAt] END DESC,
        CASE WHEN @SortDir = 'asc' AND @SortBy = 'createdAt' THEN [d].[CreatedAt] END ASC,
        CASE WHEN @SortDir = 'desc' AND @SortBy = 'createdAt' THEN [d].[CreatedAt] END DESC,
        CASE WHEN @SortDir = 'asc' AND @SortBy = 'owner' THEN [d].[OwnerDisplayName] END ASC,
        CASE WHEN @SortDir = 'desc' AND @SortBy = 'owner' THEN [d].[OwnerDisplayName] END DESC,
        CASE WHEN @SortDir = 'asc' AND @SortBy = 'latestSignedVersion' THEN [d].[LatestSignedVersion] END ASC,
        CASE WHEN @SortDir = 'desc' AND @SortBy = 'latestSignedVersion' THEN [d].[LatestSignedVersion] END DESC,
        [d].[Id] ASC
    OFFSET (CAST(@Page AS BIGINT) - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;

    SELECT COUNT(*) AS [TotalCount] FROM [#Docs];
END;
