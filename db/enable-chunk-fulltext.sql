-- Optional DBA operation, not an application migration. Run outside a transaction
-- after schema.sql on SQL Server with the Full-Text Search component installed.
-- LocalDB does not provide that component; retrieval uses BM25 there instead.
SET NOCOUNT ON;

IF COALESCE(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'), 0) <> 1
    THROW 51001, N'Full-Text Search is not installed. Install the SQL Server component or keep the BM25 fallback.', 1;

DECLARE @tableId int = OBJECT_ID(N'dbo.Chunks');
IF @tableId IS NULL
    THROW 51002, N'Apply the Clinical RAG schema before enabling chunk Full-Text Search.', 1;

DECLARE @keyIndex sysname = (
    SELECT TOP (1) i.[name]
    FROM sys.indexes i
    JOIN sys.index_columns c ON c.object_id = i.object_id AND c.index_id = i.index_id AND c.key_ordinal = 1
    WHERE i.object_id = @tableId AND i.is_unique = 1 AND i.has_filter = 0 AND i.is_disabled = 0
      AND COL_NAME(c.object_id, c.column_id) = N'Id'
      AND (SELECT COUNT(*) FROM sys.index_columns k
           WHERE k.object_id = i.object_id AND k.index_id = i.index_id AND k.key_ordinal > 0) = 1
    ORDER BY i.is_primary_key DESC, i.index_id
);
IF @keyIndex IS NULL
    THROW 51003, N'Chunks requires an enabled, single-column unique Id index for Full-Text Search.', 1;

IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = @tableId)
BEGIN
    -- Never drop or silently replace an existing DBA-managed index/catalog.
    IF NOT EXISTS (
        SELECT 1 FROM sys.fulltext_indexes f
        JOIN sys.index_columns k ON k.object_id = f.object_id AND k.index_id = f.unique_index_id AND k.key_ordinal = 1
        WHERE f.object_id = @tableId AND COL_NAME(k.object_id, k.column_id) = N'Id'
    )
        THROW 51004, N'The existing chunk Full-Text key is not Id. Review it manually; no index was replaced.', 1;

    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_index_columns
                   WHERE object_id = @tableId AND COL_NAME(object_id, column_id) = N'Text')
        EXEC(N'ALTER FULLTEXT INDEX ON [dbo].[Chunks] ADD ([Text] LANGUAGE 1033);');
    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_index_columns
                   WHERE object_id = @tableId AND COL_NAME(object_id, column_id) = N'Section')
        EXEC(N'ALTER FULLTEXT INDEX ON [dbo].[Chunks] ADD ([Section] LANGUAGE 1033);');
    IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = @tableId AND is_enabled = 0)
        EXEC(N'ALTER FULLTEXT INDEX ON [dbo].[Chunks] ENABLE;');
    EXEC(N'ALTER FULLTEXT INDEX ON [dbo].[Chunks] SET CHANGE_TRACKING AUTO;');
END
ELSE
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE [name] = N'ClinicalRagChunks')
        EXEC(N'CREATE FULLTEXT CATALOG [ClinicalRagChunks] WITH ACCENT_SENSITIVITY = ON;');

    -- Disable stop words for the new index so short clinical terms are retained.
    -- LANGUAGE 1033 is English; choose appropriate languages for other corpora.
    DECLARE @createSql nvarchar(max) =
        N'CREATE FULLTEXT INDEX ON [dbo].[Chunks] ([Text] LANGUAGE 1033, [Section] LANGUAGE 1033) KEY INDEX '
        + QUOTENAME(@keyIndex)
        + N' ON [ClinicalRagChunks] WITH (CHANGE_TRACKING = AUTO, STOPLIST = OFF);';
    EXEC(@createSql);
END;
