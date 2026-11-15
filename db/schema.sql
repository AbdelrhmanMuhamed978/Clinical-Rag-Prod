IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE TABLE [AnalysisRuns] (
        [Id] uniqueidentifier NOT NULL,
        [WorkspaceId] uniqueidentifier NULL,
        [UserId] uniqueidentifier NULL,
        [InputHash] nvarchar(64) NOT NULL,
        [ConfigVersion] int NULL,
        [ResultJson] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_AnalysisRuns] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE TABLE [ChunkerGuidances] (
        [Id] uniqueidentifier NOT NULL,
        [DocumentId] uniqueidentifier NOT NULL,
        [Guidance] nvarchar(max) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_ChunkerGuidances] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE TABLE [ConfigVersions] (
        [Id] uniqueidentifier NOT NULL,
        [WorkspaceId] uniqueidentifier NOT NULL,
        [Version] int NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [ConfigJson] nvarchar(max) NOT NULL,
        [CreatedBy] uniqueidentifier NULL,
        [CreatedAt] datetime2 NOT NULL,
        [PublishedAt] datetime2 NULL,
        CONSTRAINT [PK_ConfigVersions] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE TABLE [Rules] (
        [Id] uniqueidentifier NOT NULL,
        [WorkspaceId] uniqueidentifier NOT NULL,
        [RuleId] nvarchar(64) NOT NULL,
        [Title] nvarchar(256) NOT NULL,
        [DrugClasses] nvarchar(max) NULL,
        [AppliesWhen] nvarchar(max) NULL,
        [Level] nvarchar(64) NULL,
        [Message] nvarchar(max) NULL,
        [Caveat] nvarchar(max) NULL,
        [Sources] nvarchar(max) NULL,
        [LinkTerms] nvarchar(512) NULL,
        [CaseLevel] bit NOT NULL,
        [ReviewedBy] uniqueidentifier NULL,
        [ReviewedAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Rules] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE TABLE [TestCases] (
        [Id] uniqueidentifier NOT NULL,
        [WorkspaceId] uniqueidentifier NOT NULL,
        [Name] nvarchar(128) NOT NULL,
        [InputText] nvarchar(max) NOT NULL,
        [ExpectedJson] nvarchar(max) NULL,
        [LastRunJson] nvarchar(max) NULL,
        [LastRunAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_TestCases] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE TABLE [Users] (
        [Id] uniqueidentifier NOT NULL,
        [FirebaseUid] nvarchar(128) NOT NULL,
        [Email] nvarchar(256) NOT NULL,
        [DisplayName] nvarchar(128) NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE TABLE [Workspaces] (
        [Id] uniqueidentifier NOT NULL,
        [Name] nvarchar(128) NOT NULL,
        [OwnerId] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Workspaces] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Workspaces_Users_OwnerId] FOREIGN KEY ([OwnerId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE TABLE [Documents] (
        [Id] uniqueidentifier NOT NULL,
        [WorkspaceId] uniqueidentifier NOT NULL,
        [FileName] nvarchar(256) NOT NULL,
        [StoragePath] nvarchar(512) NULL,
        [ParsedText] nvarchar(max) NULL,
        [Status] nvarchar(32) NOT NULL,
        [PageCount] int NULL,
        [ChunkedPagesCount] int NULL,
        [ChunkingStatus] nvarchar(max) NULL,
        [ChunkedPageNumbers] nvarchar(max) NULL,
        [UploadedBy] uniqueidentifier NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Documents] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Documents_Workspaces_WorkspaceId] FOREIGN KEY ([WorkspaceId]) REFERENCES [Workspaces] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE TABLE [WorkspaceMembers] (
        [WorkspaceId] uniqueidentifier NOT NULL,
        [UserId] uniqueidentifier NOT NULL,
        [Role] nvarchar(16) NOT NULL,
        [JoinedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_WorkspaceMembers] PRIMARY KEY ([WorkspaceId], [UserId]),
        CONSTRAINT [FK_WorkspaceMembers_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_WorkspaceMembers_Workspaces_WorkspaceId] FOREIGN KEY ([WorkspaceId]) REFERENCES [Workspaces] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE TABLE [Chunks] (
        [Id] uniqueidentifier NOT NULL,
        [WorkspaceId] uniqueidentifier NOT NULL,
        [DocumentId] uniqueidentifier NOT NULL,
        [Page] int NOT NULL,
        [PageStart] int NULL,
        [PageEnd] int NULL,
        [Section] nvarchar(256) NULL,
        [SectionHierarchy] nvarchar(64) NULL,
        [Year] int NULL,
        [Strength] nvarchar(32) NULL,
        [Certainty] nvarchar(32) NULL,
        [Text] nvarchar(max) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [Confidence] float NULL,
        [Metadata] nvarchar(max) NULL,
        [Embedding] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ReviewedBy] uniqueidentifier NULL,
        [ReviewedAt] datetime2 NULL,
        CONSTRAINT [PK_Chunks] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Chunks_Documents_DocumentId] FOREIGN KEY ([DocumentId]) REFERENCES [Documents] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE INDEX [IX_Chunks_DocumentId] ON [Chunks] ([DocumentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ConfigVersions_WorkspaceId_Version] ON [ConfigVersions] ([WorkspaceId], [Version]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE INDEX [IX_Documents_WorkspaceId] ON [Documents] ([WorkspaceId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_FirebaseUid] ON [Users] ([FirebaseUid]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE INDEX [IX_WorkspaceMembers_UserId] ON [WorkspaceMembers] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    CREATE INDEX [IX_Workspaces_OwnerId] ON [Workspaces] ([OwnerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045400_Initial'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261114045400_Initial', N'10.0.0');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045632_ChunkMetadata'
)
BEGIN
    DROP INDEX [IX_Chunks_DocumentId] ON [Chunks];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045632_ChunkMetadata'
)
BEGIN
    ALTER TABLE [Documents] ADD [GuidelineStatus] nvarchar(16) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045632_ChunkMetadata'
)
BEGIN
    ALTER TABLE [Documents] ADD [Society] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045632_ChunkMetadata'
)
BEGIN
    ALTER TABLE [Documents] ADD [UpdatedBy] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045632_ChunkMetadata'
)
BEGIN
    ALTER TABLE [Documents] ADD [Year] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045632_ChunkMetadata'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Chunks]') AND [c].[name] = N'SectionHierarchy');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [Chunks] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [Chunks] ALTER COLUMN [SectionHierarchy] nvarchar(256) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045632_ChunkMetadata'
)
BEGIN
    ALTER TABLE [Chunks] ADD [EmbeddingModel] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045632_ChunkMetadata'
)
BEGIN
    ALTER TABLE [Chunks] ADD [Type] nvarchar(16) NOT NULL DEFAULT N'';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045632_ChunkMetadata'
)
BEGIN
    ALTER TABLE [Chunks] ADD [ValidationError] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045632_ChunkMetadata'
)
BEGIN
    ALTER TABLE [Chunks] ADD [Verbatim] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045632_ChunkMetadata'
)
BEGIN
    CREATE INDEX [IX_Chunks_DocumentId_Page] ON [Chunks] ([DocumentId], [Page]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045632_ChunkMetadata'
)
BEGIN
    CREATE INDEX [IX_Chunks_WorkspaceId_Status] ON [Chunks] ([WorkspaceId], [Status]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261114045632_ChunkMetadata'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261114045632_ChunkMetadata', N'10.0.0');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    ALTER TABLE [Rules] ADD [CreatedBy] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    ALTER TABLE [Rules] ADD [Enabled] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    ALTER TABLE [Rules] ADD [ReviewedCatalogRevision] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    ALTER TABLE [Rules] ADD [Revision] int NOT NULL DEFAULT 1;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    ALTER TABLE [Rules] ADD [Status] nvarchar(16) NOT NULL DEFAULT N'draft';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    ALTER TABLE [Rules] ADD [UpdatedBy] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    CREATE TABLE [DrugCatalogs] (
        [WorkspaceId] uniqueidentifier NOT NULL,
        [CatalogJson] nvarchar(max) NOT NULL,
        [Revision] int NOT NULL,
        [UpdatedBy] uniqueidentifier NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_DrugCatalogs] PRIMARY KEY ([WorkspaceId]),
        CONSTRAINT [CK_DrugCatalogs_Revision] CHECK ([Revision] >= 1)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    CREATE TABLE [RuleAuditEntries] (
        [Id] uniqueidentifier NOT NULL,
        [WorkspaceId] uniqueidentifier NOT NULL,
        [RuleId] uniqueidentifier NULL,
        [RuleKey] nvarchar(64) NULL,
        [Action] nvarchar(32) NOT NULL,
        [ActorId] uniqueidentifier NULL,
        [BeforeJson] nvarchar(max) NULL,
        [AfterJson] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_RuleAuditEntries] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    CREATE INDEX [IX_Rules_WorkspaceId_RuleId] ON [Rules] ([WorkspaceId], [RuleId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    EXEC(N'ALTER TABLE [Rules] ADD CONSTRAINT [CK_Rules_Revision] CHECK ([Revision] >= 1)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    EXEC(N'ALTER TABLE [Rules] ADD CONSTRAINT [CK_Rules_Status] CHECK ([Status] IN (''draft'', ''in_review'', ''approved'', ''retired''))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    CREATE INDEX [IX_RuleAuditEntries_WorkspaceId_RuleId_CreatedAt] ON [RuleAuditEntries] ([WorkspaceId], [RuleId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    EXEC(N'DECLARE @legacyReviewResetAt datetime2 = SYSUTCDATETIME();
    INSERT INTO [RuleAuditEntries]
        ([Id], [WorkspaceId], [RuleId], [RuleKey], [Action], [ActorId], [BeforeJson], [AfterJson], [CreatedAt])
    SELECT NEWID(), r.[WorkspaceId], r.[Id], r.[RuleId], N''legacy_review_reset'', NULL,
        (SELECT r.[Id] AS [id], r.[WorkspaceId] AS [workspaceId], r.[RuleId] AS [ruleId],
            r.[Title] AS [title], r.[DrugClasses] AS [drugClasses], r.[AppliesWhen] AS [appliesWhen],
            r.[Level] AS [level], r.[Message] AS [message], r.[Caveat] AS [caveat],
            r.[Sources] AS [sources], r.[LinkTerms] AS [linkTerms], r.[CaseLevel] AS [caseLevel],
            r.[ReviewedBy] AS [reviewedBy], r.[ReviewedAt] AS [reviewedAt],
            r.[CreatedAt] AS [createdAt], r.[UpdatedAt] AS [updatedAt]
         FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER),
        (SELECT r.[Id] AS [id], r.[WorkspaceId] AS [workspaceId], r.[RuleId] AS [ruleId],
            r.[Title] AS [title], r.[DrugClasses] AS [drugClasses], r.[AppliesWhen] AS [appliesWhen],
            r.[Level] AS [level], r.[Message] AS [message], r.[Caveat] AS [caveat],
            r.[Sources] AS [sources], r.[LinkTerms] AS [linkTerms], r.[CaseLevel] AS [caseLevel],
            N''draft'' AS [status], CAST(0 AS bit) AS [enabled], 1 AS [revision],
            CAST(NULL AS uniqueidentifier) AS [reviewedBy], CAST(NULL AS datetime2) AS [reviewedAt],
            CAST(NULL AS int) AS [reviewedCatalogRevision],
            CAST(NULL AS uniqueidentifier) AS [createdBy], CAST(NULL AS uniqueidentifier) AS [updatedBy],
            r.[CreatedAt] AS [createdAt], @legacyReviewResetAt AS [updatedAt]
         FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER),
        @legacyReviewResetAt
    FROM [Rules] r;

    UPDATE [Rules] SET [Status] = N''draft'', [Enabled] = 0, [Revision] = 1,
        [ReviewedBy] = NULL, [ReviewedAt] = NULL, [ReviewedCatalogRevision] = NULL,
        [UpdatedAt] = @legacyReviewResetAt;');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115001436_RuleReviewAndDrugCatalog'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261115001436_RuleReviewAndDrugCatalog', N'10.0.0');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115025949_DocumentProcessingState'
)
BEGIN
    ALTER TABLE [Documents] ADD [FailureReason] nvarchar(512) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115025949_DocumentProcessingState'
)
BEGIN
    ALTER TABLE [Documents] ADD [FailureStage] nvarchar(32) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115025949_DocumentProcessingState'
)
BEGIN
    ALTER TABLE [Documents] ADD [ProcessingOperation] nvarchar(32) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115025949_DocumentProcessingState'
)
BEGIN
    ALTER TABLE [Documents] ADD [Revision] int NOT NULL DEFAULT 1;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115025949_DocumentProcessingState'
)
BEGIN
    EXEC(N'UPDATE [Documents]
    SET [FailureStage] = CASE WHEN [Status] = ''parsing'' THEN ''parsing'' ELSE ''chunking'' END,
        [FailureReason] = CASE WHEN [Status] = ''parsing''
            THEN N''Previous parsing was interrupted. Retry Parse, or use the retained parsed text if available.''
            ELSE N''Previous chunking was interrupted. Completed pages were kept. Choose the remaining pages to retry.'' END,
        [ChunkingStatus] = CASE WHEN [Status] = ''parsing'' THEN [ChunkingStatus] ELSE ''failed'' END,
        [Status] = ''failed''
    WHERE [Status] IN (''parsing'', ''chunking'') OR [ChunkingStatus] = ''in_progress'';

    UPDATE [Documents]
    SET [FailureStage] = CASE WHEN NULLIF(LTRIM(RTRIM([ParsedText])), '''') IS NULL THEN ''parsing'' ELSE ''chunking'' END,
        [FailureReason] = N''Previous processing failed; no reason was recorded. Retry the failed step or upload the PDF again.''
    WHERE [Status] = ''failed'' AND [FailureReason] IS NULL;

    UPDATE d SET [Status] = ''review''
    FROM [Documents] d
    WHERE d.[Status] = ''indexed'' AND (
        NOT EXISTS (SELECT 1 FROM [Chunks] c WHERE c.[DocumentId] = d.[Id] AND c.[WorkspaceId] = d.[WorkspaceId]
            AND c.[Status] = ''embedded'' AND NULLIF(LTRIM(RTRIM(c.[Embedding])), '''') IS NOT NULL)
        OR EXISTS (SELECT 1 FROM [Chunks] c WHERE c.[DocumentId] = d.[Id] AND c.[WorkspaceId] = d.[WorkspaceId]
            AND (c.[Status] NOT IN (''rejected'', ''embedded'') OR (c.[Status] = ''embedded'' AND NULLIF(LTRIM(RTRIM(c.[Embedding])), '''') IS NULL)))
    );');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115025949_DocumentProcessingState'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261115025949_DocumentProcessingState', N'10.0.0');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115104518_BinaryChunkEmbeddings'
)
BEGIN
    ALTER TABLE [Workspaces] ADD [EmbeddingDimensions] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115104518_BinaryChunkEmbeddings'
)
BEGIN
    ALTER TABLE [Workspaces] ADD [EmbeddingModel] nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115104518_BinaryChunkEmbeddings'
)
BEGIN
    DECLARE @var1 nvarchar(max);
    SELECT @var1 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Chunks]') AND [c].[name] = N'EmbeddingModel');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [Chunks] DROP CONSTRAINT ' + @var1 + ';');
    ALTER TABLE [Chunks] ALTER COLUMN [EmbeddingModel] nvarchar(max) COLLATE Latin1_General_100_BIN2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115104518_BinaryChunkEmbeddings'
)
BEGIN
    ALTER TABLE [Documents] ADD [EmbeddingBatchSize] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115104518_BinaryChunkEmbeddings'
)
BEGIN
    ALTER TABLE [Documents] ADD [EmbeddingCompleted] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115104518_BinaryChunkEmbeddings'
)
BEGIN
    ALTER TABLE [Documents] ADD [EmbeddingTotal] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115104518_BinaryChunkEmbeddings'
)
BEGIN
    EXEC sp_rename N'[Chunks].[Embedding]', N'LegacyEmbeddingJson', 'COLUMN';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115104518_BinaryChunkEmbeddings'
)
BEGIN
    ALTER TABLE [Chunks] ADD [Embedding] varbinary(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115104518_BinaryChunkEmbeddings'
)
BEGIN
    EXEC(N'UPDATE [Chunks] SET [Status] = N''accepted'' WHERE [Status] = N''embedded'';

    UPDATE [Documents]
    SET [Status] = N''failed'', [ProcessingOperation] = NULL, [FailureStage] = N''embedding'',
        [FailureReason] = N''Embedding was interrupted by the binary vector upgrade. Legacy vectors were archived. Retry Embed with the current model.'',
        [Revision] = [Revision] + 1, [UpdatedAt] = SYSUTCDATETIME()
    WHERE [ProcessingOperation] = N''embedding'';

    UPDATE [Documents]
    SET [Status] = N''review'', [Revision] = [Revision] + 1, [UpdatedAt] = SYSUTCDATETIME()
    WHERE [Status] = N''indexed'';');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115104518_BinaryChunkEmbeddings'
)
BEGIN
    EXEC(N'ALTER TABLE [Workspaces] ADD CONSTRAINT [CK_Workspaces_EmbeddingDimensions] CHECK ([EmbeddingDimensions] IS NULL OR [EmbeddingDimensions] BETWEEN 1 AND 16384)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115104518_BinaryChunkEmbeddings'
)
BEGIN
    EXEC(N'ALTER TABLE [Documents] ADD CONSTRAINT [CK_Documents_EmbeddingBatchSize] CHECK ([EmbeddingBatchSize] IS NULL OR [EmbeddingBatchSize] BETWEEN 1 AND 32)');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115104518_BinaryChunkEmbeddings'
)
BEGIN
    EXEC(N'ALTER TABLE [Documents] ADD CONSTRAINT [CK_Documents_EmbeddingProgress] CHECK ([EmbeddingCompleted] >= 0 AND ([EmbeddingTotal] IS NULL OR ([EmbeddingTotal] >= 0 AND [EmbeddingCompleted] <= [EmbeddingTotal])))');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115104518_BinaryChunkEmbeddings'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261115104518_BinaryChunkEmbeddings', N'10.0.0');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115125916_ColabOperationalConnection'
)
BEGIN
    CREATE TABLE [ColabConnections] (
        [Id] int NOT NULL,
        [BaseUrl] nvarchar(2048) NOT NULL,
        [ProtectedApiKey] nvarchar(max) NOT NULL,
        [Revision] int NOT NULL,
        [ManualOverride] bit NOT NULL,
        [InstanceId] nvarchar(32) NULL,
        [UpdatedAt] datetimeoffset NOT NULL,
        [LastHeartbeatAt] datetimeoffset NULL,
        CONSTRAINT [PK_ColabConnections] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_ColabConnections_Revision] CHECK ([Revision] >= 1),
        CONSTRAINT [CK_ColabConnections_Singleton] CHECK ([Id] = 1)
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115125916_ColabOperationalConnection'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261115125916_ColabOperationalConnection', N'10.0.0');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115140456_TestRunHarness'
)
BEGIN
    CREATE TABLE [TestRuns] (
        [Id] uniqueidentifier NOT NULL,
        [WorkspaceId] uniqueidentifier NOT NULL,
        [CaseId] uniqueidentifier NOT NULL,
        [ConfigId] uniqueidentifier NULL,
        [ConfigVersion] int NOT NULL,
        [ConfigHash] nvarchar(max) NOT NULL,
        [Status] nvarchar(16) NOT NULL,
        [Diff] nvarchar(max) NULL,
        [DurationMilliseconds] float NOT NULL,
        [StartedAt] datetime2 NOT NULL,
        [CompletedAt] datetime2 NULL,
        CONSTRAINT [PK_TestRuns] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115140456_TestRunHarness'
)
BEGIN
    CREATE INDEX [IX_TestRuns_WorkspaceId_CaseId_StartedAt] ON [TestRuns] ([WorkspaceId], [CaseId], [StartedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115140456_TestRunHarness'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261115140456_TestRunHarness', N'10.0.0');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115155603_WorkspaceAdministration'
)
BEGIN
    ALTER TABLE [Workspaces] ADD [DeletedAt] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115155603_WorkspaceAdministration'
)
BEGIN
    ALTER TABLE [Workspaces] ADD [DeletedBy] uniqueidentifier NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115155603_WorkspaceAdministration'
)
BEGIN
    ALTER TABLE [Workspaces] ADD [MembershipRevision] int NOT NULL DEFAULT 1;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115155603_WorkspaceAdministration'
)
BEGIN
    CREATE TABLE [PendingUploadDeletions] (
        [Id] uniqueidentifier NOT NULL,
        [StoragePath] nvarchar(512) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [LastAttemptAt] datetime2 NULL,
        CONSTRAINT [PK_PendingUploadDeletions] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115155603_WorkspaceAdministration'
)
BEGIN
    CREATE TABLE [WorkspaceInvitations] (
        [Id] uniqueidentifier NOT NULL,
        [WorkspaceId] uniqueidentifier NOT NULL,
        [Email] nvarchar(256) NOT NULL,
        [Role] nvarchar(16) NOT NULL,
        [InvitedBy] uniqueidentifier NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_WorkspaceInvitations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_WorkspaceInvitations_Workspaces_WorkspaceId] FOREIGN KEY ([WorkspaceId]) REFERENCES [Workspaces] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115155603_WorkspaceAdministration'
)
BEGIN
    CREATE INDEX [IX_ChunkerGuidances_DocumentId_CreatedAt] ON [ChunkerGuidances] ([DocumentId], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115155603_WorkspaceAdministration'
)
BEGIN
    CREATE INDEX [IX_PendingUploadDeletions_LastAttemptAt] ON [PendingUploadDeletions] ([LastAttemptAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115155603_WorkspaceAdministration'
)
BEGIN
    CREATE UNIQUE INDEX [IX_WorkspaceInvitations_WorkspaceId_Email] ON [WorkspaceInvitations] ([WorkspaceId], [Email]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261115155603_WorkspaceAdministration'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261115155603_WorkspaceAdministration', N'10.0.0');
END;

COMMIT;
GO

