# Repeatable local/staging deployment rehearsal.
#
# Creates disposable GUID-named databases only. Uses integrated authentication.
# Do not put SQL passwords, Colab keys or case text in this script or its output.
param(
    [string]$Server = "localhost\MSSQLSERVER2025",
    [string]$OutputDirectory = "docs/rehearsal-results",
    [switch]$KeepDatabases
)

$ErrorActionPreference = "Stop"
$sqlcmd = (Get-Command sqlcmd -ErrorAction Stop).Source
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH-mm-ssZ")
$result = [ordered]@{
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    server = $Server
    status = "running"
    databasesKept = $false
    migration = [ordered]@{}
    backupRestore = [ordered]@{}
    fullText = [ordered]@{}
    colab = [ordered]@{}
    reembedding = [ordered]@{}
}
$databases = New-Object System.Collections.Generic.List[string]
$backupFile = $null

function Invoke-Sql {
    param([string]$Database, [string]$Query, [switch]$Master)
    $target = if ($Master) { "master" } else { $Database }
    $output = & $sqlcmd -S $Server -E -C -b -d $target -Q $Query -h -1 -W -s "|" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed against $target." }
    return (($output | Out-String).Trim())
}

function Invoke-SqlFile {
    param([string]$Database, [string]$Path)
    $output = & $sqlcmd -S $Server -E -C -b -d $Database -i $Path 2>&1
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd file failed against $Database." }
    return (($output | Out-String).Trim())
}

function Scalar {
    param([string]$Database, [string]$Query)
    return (Invoke-Sql -Database $Database -Query "SET NOCOUNT ON; $Query").Trim()
}

function New-Database {
    param([string]$Name)
    Invoke-Sql -Master -Query "IF DB_ID(N'$Name') IS NOT NULL THROW 52001, N'Database name collision.', 1; CREATE DATABASE [$Name];" | Out-Null
    $databases.Add($Name)
}

function Drop-Database {
    param([string]$Name)
    if (-not $databases.Contains($Name)) { return }
    try {
        Invoke-Sql -Master -Query "IF DB_ID(N'$Name') IS NOT NULL BEGIN ALTER DATABASE [$Name] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Name]; END;" | Out-Null
    } catch { }
}

function Measure-Milliseconds {
    param([scriptblock]$Action)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    & $Action | Out-Null
    $watch.Stop()
    return [math]::Round($watch.Elapsed.TotalMilliseconds, 1)
}

function Run-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed." }
}

function Seed-Legacy {
    param([string]$Database, [string]$UserId, [string]$WorkspaceId, [string]$DocumentId, [string]$InterruptedId)
    $created = "2026-11-01T10:00:00Z"
    $rows = @(
        @{ Id = [guid]::NewGuid().ToString(); Page = 1; Json = "N'[1.0,0.0]'" },
        @{ Id = [guid]::NewGuid().ToString(); Page = 2; Json = "N''" },
        @{ Id = [guid]::NewGuid().ToString(); Page = 3; Json = "N'not JSON: synthetic archive'" },
        @{ Id = [guid]::NewGuid().ToString(); Page = 4; Json = "NULL" }
    )
    $chunkSql = ($rows | ForEach-Object {
        "INSERT INTO [Chunks] ([Id],[WorkspaceId],[DocumentId],[Page],[Text],[Status],[Embedding],[EmbeddingModel],[CreatedAt],[Type],[Verbatim]) VALUES ('$($_.Id)','$WorkspaceId','$DocumentId',$($_.Page),N'archiveonlyneedle retained quotation',N'embedded',$($_.Json),N'LegacyModel','$created',N'clinical',1);"
    }) -join "`n"
    $seed = @"
INSERT INTO [Users] ([Id],[FirebaseUid],[Email],[CreatedAt]) VALUES ('$UserId',N'rehearsal-user',N'synthetic@example.invalid','$created');
INSERT INTO [Workspaces] ([Id],[Name],[OwnerId],[CreatedAt]) VALUES ('$WorkspaceId',N'Rehearsal workspace','$UserId','$created');
INSERT INTO [Documents] ([Id],[WorkspaceId],[FileName],[Status],[ParsedText],[PageCount],[ChunkedPagesCount],[ChunkingStatus],[ChunkedPageNumbers],[UploadedBy],[CreatedAt],[UpdatedAt],[GuidelineStatus],[Society],[Year],[ProcessingOperation],[Revision])
VALUES ('$DocumentId','$WorkspaceId',N'rehearsal.pdf',N'indexed',N'Retained source text',4,4,N'completed',N'[1,2,3,4]','$UserId','$created','$created',N'final',N'Synthetic Society',2024,NULL,7);
INSERT INTO [Documents] ([Id],[WorkspaceId],[FileName],[Status],[ParsedText],[PageCount],[ChunkedPagesCount],[ChunkingStatus],[ChunkedPageNumbers],[UploadedBy],[CreatedAt],[UpdatedAt],[GuidelineStatus],[Society],[Year],[ProcessingOperation],[Revision])
VALUES ('$InterruptedId','$WorkspaceId',N'interrupted.pdf',N'review',NULL,NULL,NULL,NULL,NULL,'$UserId','$created','$created',N'draft',N'Synthetic Society',2024,N'embedding',9);
$chunkSql
"@
    Invoke-Sql -Database $Database -Query $seed | Out-Null
    return $rows.Count
}

function Seed-FullText {
    param([string]$Database, [string]$UserId, [string]$WorkspaceId, [string]$DocumentId, [string]$FirstChunk, [string]$SecondChunk, [string]$ThirdChunk)
    $created = "2026-11-01T10:00:00Z"
    $seed = @"
INSERT INTO [Users] ([Id],[FirebaseUid],[Email],[CreatedAt]) VALUES ('$UserId',N'fts-user',N'synthetic@example.invalid','$created');
INSERT INTO [Workspaces] ([Id],[Name],[OwnerId],[CreatedAt]) VALUES ('$WorkspaceId',N'Full-Text rehearsal','$UserId','$created');
INSERT INTO [Documents] ([Id],[WorkspaceId],[FileName],[Status],[CreatedAt],[UpdatedAt],[GuidelineStatus]) VALUES ('$DocumentId','$WorkspaceId',N'fts-synthetic.pdf',N'review','$created','$created',N'final');
INSERT INTO [Chunks] ([Id],[WorkspaceId],[DocumentId],[Page],[Text],[Section],[Status],[Embedding],[EmbeddingModel],[CreatedAt],[Type],[Verbatim])
VALUES ('$FirstChunk','$WorkspaceId','$DocumentId',1,N'Synthetic cystatin monitoring recommendation',N'Renal monitoring: cystatin',N'embedded',0x0000803F00000000,N'synthetic-model','$created',N'clinical',1),
       ('$SecondChunk','$WorkspaceId','$DocumentId',2,N'Synthetic anti-Xa measurement recommendation',N'Coagulation: anti-Xa',N'embedded',0x0000803F00000000,N'synthetic-model','$created',N'clinical',1),
       ('$ThirdChunk','$WorkspaceId','$DocumentId',3,N'Synthetic procalcitonin measurement recommendation',N'Infection: procalcitonin',N'embedded',0x0000803F00000000,N'synthetic-model','$created',N'clinical',1);
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE [name] = N'clinicalrag_rehearsal_app')
    CREATE USER [clinicalrag_rehearsal_app] WITHOUT LOGIN;
GRANT SELECT ON [dbo].[Chunks] TO [clinicalrag_rehearsal_app];
"@
    Invoke-Sql -Database $Database -Query $seed | Out-Null
}

$migrationDb = "ClinicalRagRehearsal_" + [guid]::NewGuid().ToString("N")
$legacyRestoreDb = "ClinicalRagRehearsalMigrated_" + [guid]::NewGuid().ToString("N")
$restoreDb = "ClinicalRagRehearsalRestore_" + [guid]::NewGuid().ToString("N")
$fullTextDb = "ClinicalRagFullTextRehearsal_" + [guid]::NewGuid().ToString("N")

try {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    Run-DotNet @("build", "backend/ClinicalRag.Api.csproj", "--nologo", "--no-restore")
    Run-DotNet @("build", "backend.Tests/ClinicalRag.Tests.csproj", "--nologo", "--no-restore")

    # ---- Migration rehearsal ----
    $userId = [guid]::NewGuid().ToString(); $workspaceId = [guid]::NewGuid().ToString()
    $documentId = [guid]::NewGuid().ToString(); $interruptedId = [guid]::NewGuid().ToString()
    New-Database $migrationDb
    $cs = "Server=$Server;Database=$migrationDb;Integrated Security=true;TrustServerCertificate=true;Encrypt=false"
    $migrationMs = Measure-Milliseconds {
        Run-DotNet @("ef", "database", "update", "20261115025949_DocumentProcessingState",
            "--project", "backend", "--startup-project", "backend", "--connection", $cs, "--no-build")
    }
    $legacyRows = Seed-Legacy -Database $migrationDb -UserId $userId -WorkspaceId $workspaceId -DocumentId $documentId -InterruptedId $interruptedId
    $legacyType = Scalar $migrationDb "SELECT TYPE_NAME([system_type_id]) FROM sys.columns WHERE [object_id] = OBJECT_ID(N'dbo.Chunks') AND [name] = N'Embedding';"
    $legacySourceDb = $migrationDb

    # The rehearsal applies the script to a restored copy of the legacy-state
    # database, not to the original seeded source.
    $backupDirectory = [string](Scalar "master" "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000));")
    $legacyBackupFile = Join-Path $backupDirectory ("ClinicalRagRehearsalLegacy_" + [guid]::NewGuid().ToString("N") + ".bak")
    $legacyBackupMs = Measure-Milliseconds {
        Invoke-Sql -Master -Query "BACKUP DATABASE [$legacySourceDb] TO DISK = N'$legacyBackupFile' WITH COPY_ONLY, CHECKSUM, INIT;" | Out-Null
    }
    $fileRows = (Invoke-Sql -Database $legacySourceDb -Query "SET NOCOUNT ON; SELECT [name], [type_desc] FROM sys.database_files ORDER BY [type_desc];") -split "\r?\n" | Where-Object { $_ -match '\|' }
    $dataLogical = (($fileRows | Where-Object { $_ -match '\|ROWS$' } | Select-Object -First 1) -split '\|' | Select-Object -First 1).Trim()
    $logLogical = (($fileRows | Where-Object { $_ -match '\|LOG$' } | Select-Object -First 1) -split '\|' | Select-Object -First 1).Trim()
    $dataPath = [string](Scalar "master" "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000));")
    $logPath = [string](Scalar "master" "SELECT CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000));")
    $legacyRestoreMs = Measure-Milliseconds {
        Invoke-Sql -Master -Query "RESTORE DATABASE [$legacyRestoreDb] FROM DISK = N'$legacyBackupFile' WITH MOVE N'$dataLogical' TO N'$($dataPath)$legacyRestoreDb.mdf', MOVE N'$logLogical' TO N'$($logPath)$legacyRestoreDb.ldf', RECOVERY, REPLACE;" | Out-Null
        $databases.Add($legacyRestoreDb)
    }
    $migrationDb = $legacyRestoreDb
    $migration = @{
        database = $migrationDb
        legacySourceDatabase = $legacySourceDb
        migrateToDocumentProcessingStateMilliseconds = $migrationMs
        legacyBackupMilliseconds = $legacyBackupMs
        legacyRestoreCopyMilliseconds = $legacyRestoreMs
        legacyColumnType = $legacyType
        insertedLegacyChunks = $legacyRows
    }

    $scriptMs = Measure-Milliseconds { Invoke-SqlFile -Database $migrationDb -Path "db/schema.sql" }
    $chunks = [int](Scalar $migrationDb "SELECT COUNT(*) FROM [dbo].[Chunks];")
    $valid = [int](Scalar $migrationDb "SELECT COUNT(*) FROM [dbo].[Chunks] WHERE [LegacyEmbeddingJson] = N'[1.0,0.0]';")
    $empty = [int](Scalar $migrationDb "SELECT COUNT(*) FROM [dbo].[Chunks] WHERE [LegacyEmbeddingJson] = N'';")
    $malformed = [int](Scalar $migrationDb "SELECT COUNT(*) FROM [dbo].[Chunks] WHERE [LegacyEmbeddingJson] LIKE N'not JSON:%';")
    $nullArchive = [int](Scalar $migrationDb "SELECT COUNT(*) FROM [dbo].[Chunks] WHERE [LegacyEmbeddingJson] IS NULL;")
    $binaryNull = [int](Scalar $migrationDb "SELECT COUNT(*) FROM [dbo].[Chunks] WHERE [Embedding] IS NULL;")
    $accepted = [int](Scalar $migrationDb "SELECT COUNT(*) FROM [dbo].[Chunks] WHERE [Status] = N'accepted';")
    $docStatus = Scalar $migrationDb "SELECT [Status] FROM [dbo].[Documents] WHERE [Id] = '$documentId';"
    $docRevision = [int](Scalar $migrationDb "SELECT [Revision] FROM [dbo].[Documents] WHERE [Id] = '$documentId';")
    $interruptedStatus = Scalar $migrationDb "SELECT [Status] FROM [dbo].[Documents] WHERE [Id] = '$interruptedId';"
    $interruptedRevision = [int](Scalar $migrationDb "SELECT [Revision] FROM [dbo].[Documents] WHERE [Id] = '$interruptedId';")
    $triggerCount = [int](Scalar $migrationDb "SELECT COUNT(*) FROM sys.triggers WHERE [name] = N'TR_Chunks_SearchGeneration';")
    $migration.rows = @{
        chunks = $chunks; validArchive = $valid; emptyArchive = $empty; malformedArchive = $malformed
        nullArchive = $nullArchive; binaryVectorsNull = $binaryNull; acceptedChunks = $accepted
    }
    $migration.scriptFirstRunMilliseconds = $scriptMs
    $migration.scriptReapplyMilliseconds = Measure-Milliseconds { Invoke-SqlFile -Database $migrationDb -Path "db/schema.sql" }
    $migration.documents = @{
        indexedReturnsToReview = @{ status = $docStatus; revision = $docRevision }
        interruptedEmbeddingFails = @{ status = $interruptedStatus; revision = $interruptedRevision }
    }
    $migration.searchGenerationTriggerCount = $triggerCount
    $migration.archivePreserved = ($chunks -eq $legacyRows -and $valid -eq 1 -and $empty -eq 1 -and $malformed -eq 1 -and $nullArchive -eq 1 -and $binaryNull -eq $legacyRows)
    $migration.correctReviewState = ($docStatus -eq "review" -and $docRevision -eq 8 -and $interruptedStatus -eq "failed" -and $interruptedRevision -eq 10)
    $result.migration = $migration

    # ---- Backup and restore drill on the migrated copy ----
    $backupFile = Join-Path $backupDirectory ("ClinicalRagRehearsal_" + [guid]::NewGuid().ToString("N") + ".bak")
    $backupMs = Measure-Milliseconds { Invoke-Sql -Master -Query "BACKUP DATABASE [$migrationDb] TO DISK = N'$backupFile' WITH COPY_ONLY, CHECKSUM, INIT;" }
    $restoreMs = Measure-Milliseconds {
        Invoke-Sql -Master -Query "RESTORE DATABASE [$restoreDb] FROM DISK = N'$backupFile' WITH MOVE N'$dataLogical' TO N'$($dataPath)$restoreDb.mdf', MOVE N'$logLogical' TO N'$($logPath)$restoreDb.ldf', RECOVERY, REPLACE;" | Out-Null
        $databases.Add($restoreDb)
    }
    $restoredChunks = [int](Scalar $restoreDb "SELECT COUNT(*) FROM [dbo].[Chunks];")
    $restoredValid = [int](Scalar $restoreDb "SELECT COUNT(*) FROM [dbo].[Chunks] WHERE [LegacyEmbeddingJson] = N'[1.0,0.0]';")
    $restoredStatus = Scalar $restoreDb "SELECT [Status] FROM [dbo].[Documents] WHERE [Id] = '$documentId';"
    $result.backupRestore = @{
        backupMilliseconds = $backupMs
        restoreMilliseconds = $restoreMs
        restoredChunks = $restoredChunks
        restoredValidArchive = $restoredValid
        restoredDocumentStatus = $restoredStatus
        matches = ($restoredChunks -eq $chunks -and $restoredValid -eq $valid -and $restoredStatus -eq $docStatus)
        backupFileRemoved = $false
    }

    # ---- Real SQL Server Full-Text rehearsal ----
    New-Database $fullTextDb
    $schemaMs = Measure-Milliseconds { Invoke-SqlFile -Database $fullTextDb -Path "db/schema.sql" }
    $ftUser = [guid]::NewGuid().ToString(); $ftWorkspace = [guid]::NewGuid().ToString(); $ftDocument = [guid]::NewGuid().ToString()
    $ftChunk1 = [guid]::NewGuid().ToString(); $ftChunk2 = [guid]::NewGuid().ToString(); $ftChunk3 = [guid]::NewGuid().ToString()
    Seed-FullText -Database $fullTextDb -UserId $ftUser -WorkspaceId $ftWorkspace -DocumentId $ftDocument -FirstChunk $ftChunk1 -SecondChunk $ftChunk2 -ThirdChunk $ftChunk3
    $installed = [int](Scalar $fullTextDb "SELECT CAST(COALESCE(FULLTEXTSERVICEPROPERTY('IsFullTextInstalled'), 0) AS int);")
    $fullText = @{
        database = $fullTextDb
        installed = ($installed -eq 1)
        schemaMilliseconds = $schemaMs
        firstSetupMilliseconds = $null
        secondSetupMilliseconds = $null
        populationMilliseconds = $null
        exactTermQueries = @{}
        fallbackTest = $null
    }
    if ($installed -eq 1) {
        $fullText.firstSetupMilliseconds = Measure-Milliseconds { Invoke-SqlFile -Database $fullTextDb -Path "db/enable-chunk-fulltext.sql" }
        $fullText.secondSetupMilliseconds = Measure-Milliseconds { Invoke-SqlFile -Database $fullTextDb -Path "db/enable-chunk-fulltext.sql" }
        $populationWatch = [Diagnostics.Stopwatch]::StartNew()
        $deadline = (Get-Date).AddSeconds(60)
        $populated = $false
        while ((Get-Date) -lt $deadline) {
            $count = [int](Scalar $fullTextDb "EXECUTE AS USER = N'clinicalrag_rehearsal_app'; SELECT COUNT(*) FROM FREETEXTTABLE([dbo].[Chunks], ([Text],[Section]), N'cystatin monitoring') h WHERE h.[KEY] = '$ftChunk1'; REVERT;")
            if ($count -eq 1) { $populated = $true; break }
            Start-Sleep -Milliseconds 200
        }
        $populationWatch.Stop()
        $fullText.populationMilliseconds = [math]::Round($populationWatch.Elapsed.TotalMilliseconds, 1)
        $fullText.populated = $populated
        $queries = @(
            @{ Query = "cystatin monitoring"; Chunk = $ftChunk1 },
            @{ Query = "anti-Xa measurement"; Chunk = $ftChunk2 },
            @{ Query = "procalcitonin measurement"; Chunk = $ftChunk3 }
        )
        foreach ($query in $queries) {
            $count = [int](Scalar $fullTextDb "EXECUTE AS USER = N'clinicalrag_rehearsal_app'; SELECT COUNT(*) FROM FREETEXTTABLE([dbo].[Chunks], ([Text],[Section]), N'$($query.Query)') h WHERE h.[KEY] = '$($query.Chunk)'; REVERT;")
            $fullText.exactTermQueries[$query.Query] = @{ expectedChunkMatched = ($count -eq 1); matches = $count }
        }
        $env:CLINICALRAG_TEST_SQLSERVER = $Server
        Remove-Item Env:CLINICALRAG_TEST_REQUIRE_FULLTEXT -ErrorAction SilentlyContinue
        try {
            Run-DotNet @("test", "backend.Tests", "--filter", "FullyQualifiedName~SqlRetrievalTests.SQL_Bm25_fallback_searches_section", "--nologo", "--no-restore", "--no-build")
            $fullText.fallbackTest = "passed"
        } catch {
            $fullText.fallbackTest = "failed: $($_.Exception.Message)"
        } finally {
            Remove-Item Env:CLINICALRAG_TEST_SQLSERVER -ErrorAction SilentlyContinue
        }
    } else {
        $fullText.fallbackTest = "blocked: Full-Text Search is not installed on $Server"
    }
    $result.fullText = $fullText

    # ---- Focused lifecycle and re-embedding rehearsals (disposable DBs in tests) ----
    Run-DotNet @("test", "backend.Tests", "--filter", "FullyQualifiedName~MigrationTests.Rehearsal_legacy_json", "--nologo", "--no-restore", "--no-build")
    $result.reembedding = @{ test = "MigrationTests.Rehearsal_legacy_json_embeddings_preserve_archive_return_to_review_and_reembed"; result = "passed" }
    Run-DotNet @("test", "backend.Tests", "--filter", "FullyQualifiedName~ColabOperationsTests.Runtime_rotation_recovers", "--nologo", "--no-restore", "--no-build")
    $result.colab = @{ test = "ColabOperationsTests.Runtime_rotation_recovers_ready_and_operations_without_an_api_restart"; result = "passed"; note = "Transport-level simulated restart; no live Colab runtime was restarted." }

    $result.status = "passed"
    $queryFailures = @($result.fullText.exactTermQueries.Values |
        Where-Object { $_.expectedChunkMatched -ne $true }).Count
    if (-not $result.fullText.installed) { $result.status = "blocked" }
    elseif ($result.migration.archivePreserved -ne $true -or $result.migration.correctReviewState -ne $true -or
        $result.migration.searchGenerationTriggerCount -ne 1 -or $result.backupRestore.matches -ne $true -or
        $result.fullText.populated -ne $true -or $result.fullText.fallbackTest -ne "passed" -or $queryFailures -ne 0) {
        $result.status = "failed"
    }
}
catch {
    $result.status = "failed"
    $result.error = $_.Exception.Message
}
finally {
    if ($KeepDatabases) {
        $result.databasesKept = $true
        $result.keptDatabases = @($databases)
    } else {
        foreach ($name in @($databases)) { Drop-Database $name }
    }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $path = Join-Path $OutputDirectory "deployment-rehearsal-$timestamp.json"
    $json = $result | ConvertTo-Json -Depth 8
    $utf8NoBom = New-Object -TypeName System.Text.UTF8Encoding -ArgumentList $false
    [System.IO.File]::WriteAllText($path, $json, $utf8NoBom)
    $result | ConvertTo-Json -Depth 5
    Write-Output "Rehearsal result: $path"
}

if ($result.status -ne "passed") { exit 1 }

