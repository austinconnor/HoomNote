using System.Text.Json;
using Microsoft.Data.Sqlite;
using HoomNote.Core.Documents;
using HoomNote.Core.Services;
using HoomNote.Infrastructure.Serialization;

namespace HoomNote.Infrastructure.Storage;

public sealed class SqliteDocumentRepository : IDocumentRepository
{
    private static int _providerInitialized;
    private readonly SqliteConnection _connection;
    private bool _ftsEnabled;

    public SqliteDocumentRepository(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        if (Interlocked.Exchange(ref _providerInitialized, 1) == 0)
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString());
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _connection.OpenAsync(cancellationToken);
        await ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", cancellationToken);
        await ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS documents (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                kind INTEGER NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                tags_json TEXT NOT NULL,
                settings_json TEXT NOT NULL,
                sections_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS pages (
                id TEXT PRIMARY KEY,
                document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                ordinal INTEGER NOT NULL,
                title TEXT NOT NULL,
                page_json TEXT NOT NULL,
                recognized_text TEXT NOT NULL,
                recognized_regions_json TEXT NOT NULL DEFAULT '[]',
                updated_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_pages_document_ordinal ON pages(document_id, ordinal);
            CREATE TABLE IF NOT EXISTS command_journal (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                document_id TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                description TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ink_append_journal (
                page_id TEXT NOT NULL REFERENCES pages(id) ON DELETE CASCADE,
                object_id TEXT NOT NULL,
                object_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(page_id, object_id)
            );
            CREATE INDEX IF NOT EXISTS ix_ink_append_page ON ink_append_journal(page_id);
            """, cancellationToken);

        await EnsureRecognizedRegionsColumnAsync(cancellationToken);

        try
        {
            await ExecuteAsync("""
                CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(
                    document_id UNINDEXED,
                    page_id UNINDEXED,
                    document_title,
                    page_title,
                    body,
                    source
                );
                """, cancellationToken);
            _ftsEnabled = true;
        }
        catch (SqliteException)
        {
            await ExecuteAsync("""
                CREATE TABLE IF NOT EXISTS search_fallback (
                    document_id TEXT NOT NULL,
                    page_id TEXT NOT NULL,
                    document_title TEXT NOT NULL,
                    page_title TEXT NOT NULL,
                    body TEXT NOT NULL,
                    source TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_search_fallback_document ON search_fallback(document_id);
                """, cancellationToken);
            _ftsEnabled = false;
        }
    }

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.title, d.kind, d.updated_utc, COUNT(p.id)
            FROM documents d LEFT JOIN pages p ON p.document_id = d.id
            GROUP BY d.id ORDER BY d.updated_utc DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<DocumentSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DocumentSummary(
                Guid.Parse(reader.GetString(0)), reader.GetString(1),
                (DocumentKind)reader.GetInt32(2), reader.GetInt32(4),
                DateTimeOffset.Parse(reader.GetString(3))));
        }

        return results;
    }

    public async Task<HoomNoteDocument?> LoadAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT title, kind, created_utc, updated_utc, schema_version, tags_json, settings_json, sections_json
            FROM documents WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", documentId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var document = new HoomNoteDocument
        {
            Id = documentId,
            Title = reader.GetString(0),
            Kind = (DocumentKind)reader.GetInt32(1),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(2)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(3)),
            SchemaVersion = reader.GetInt32(4),
            Tags = Deserialize<List<string>>(reader.GetString(5)) ?? [],
            Settings = Deserialize<DocumentSettings>(reader.GetString(6)) ?? new(),
            Sections = Deserialize<List<NotebookSection>>(reader.GetString(7)) ?? []
        };
        await reader.DisposeAsync();

        await using var pageCommand = _connection.CreateCommand();
        pageCommand.CommandText = "SELECT page_json, recognized_text, recognized_regions_json, updated_utc FROM pages WHERE document_id = $id ORDER BY ordinal;";
        pageCommand.Parameters.AddWithValue("$id", documentId.ToString("D"));
        await using var pageReader = await pageCommand.ExecuteReaderAsync(cancellationToken);
        while (await pageReader.ReadAsync(cancellationToken))
        {
            var page = Deserialize<NotePage>(pageReader.GetString(0));
            if (page is not null)
            {
                page.RecognizedText = pageReader.GetString(1);
                page.RecognizedRegions = Deserialize<List<RecognizedTextRegion>>(pageReader.GetString(2)) ?? [];
                if (DateTimeOffset.TryParse(pageReader.GetString(3), out var pageUpdatedAt))
                    page.UpdatedAt = pageUpdatedAt;
                document.Pages.Add(page);
            }
        }
        await pageReader.DisposeAsync();

        if (document.Pages.Count > 0)
        {
            await using var appendCommand = _connection.CreateCommand();
            appendCommand.CommandText = """
                SELECT j.page_id, j.object_json FROM ink_append_journal j
                INNER JOIN pages p ON p.id = j.page_id
                WHERE p.document_id = $document ORDER BY j.created_utc;
                """;
            appendCommand.Parameters.AddWithValue("$document", documentId.ToString("D"));
            await using var appendReader = await appendCommand.ExecuteReaderAsync(cancellationToken);
            var pagesById = document.Pages.ToDictionary(page => page.Id);
            while (await appendReader.ReadAsync(cancellationToken))
            {
                if (!Guid.TryParse(appendReader.GetString(0), out var pageId) ||
                    !pagesById.TryGetValue(pageId, out var page)) continue;
                var canvasObject = Deserialize<CanvasObject>(appendReader.GetString(1));
                if (canvasObject is not null && page.Objects.All(item => item.Id != canvasObject.Id))
                    page.Objects.Add(canvasObject);
            }
            foreach (var page in document.Pages) page.Objects.Sort((left, right) => left.ZIndex.CompareTo(right.ZIndex));
        }

        return document;
    }

    public async Task SaveAsync(HoomNoteDocument document, CancellationToken cancellationToken = default)
    {
        document.UpdatedAt = DateTimeOffset.UtcNow;
        var persistedVersions = await LoadPersistedPageVersionsAsync(document.Id, cancellationToken);
        var pages = document.Pages.Select((page, ordinal) => new
        {
            Page = page,
            page.Id,
            Ordinal = ordinal,
            page.Title,
            page.RecognizedText,
            page.RecognizedRegions,
            page.UpdatedAt
        }).ToArray();
        var currentPageIds = pages.Select(item => item.Id).ToHashSet();
        var removedPageIds = persistedVersions.Keys.Where(id => !currentPageIds.Contains(id)).ToArray();
        var changedSnapshots = pages
            .Where(item => !persistedVersions.TryGetValue(item.Id, out var persisted) || persisted != item.UpdatedAt)
            .Select(item => item.Page with { Objects = [.. item.Page.Objects] })
            .ToArray();
        // Dense imported pages can contain hundreds of thousands of points. Serialize only pages
        // that actually changed, and keep that traversal off the UI thread.
        var serializedPages = await Task.Run(
            () => changedSnapshots.ToDictionary(page => page.Id, Serialize), cancellationToken);
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO documents(id,title,kind,created_utc,updated_utc,schema_version,tags_json,settings_json,sections_json)
                    VALUES($id,$title,$kind,$created,$updated,$schema,$tags,$settings,$sections)
                    ON CONFLICT(id) DO UPDATE SET title=excluded.title, kind=excluded.kind,
                    updated_utc=excluded.updated_utc, schema_version=excluded.schema_version,
                    tags_json=excluded.tags_json, settings_json=excluded.settings_json,
                    sections_json=excluded.sections_json;
                    """;
                command.Parameters.AddWithValue("$id", document.Id.ToString("D"));
                command.Parameters.AddWithValue("$title", document.Title);
                command.Parameters.AddWithValue("$kind", (int)document.Kind);
                command.Parameters.AddWithValue("$created", document.CreatedAt.ToString("O"));
                command.Parameters.AddWithValue("$updated", document.UpdatedAt.ToString("O"));
                command.Parameters.AddWithValue("$schema", document.SchemaVersion);
                command.Parameters.AddWithValue("$tags", Serialize(document.Tags));
                command.Parameters.AddWithValue("$settings", Serialize(document.Settings));
                command.Parameters.AddWithValue("$sections", Serialize(document.Sections));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var removedPageId in removedPageIds)
            {
                await using var deletePage = _connection.CreateCommand();
                deletePage.Transaction = transaction;
                deletePage.CommandText = "DELETE FROM pages WHERE document_id = $document AND id = $id;";
                deletePage.Parameters.AddWithValue("$document", document.Id.ToString("D"));
                deletePage.Parameters.AddWithValue("$id", removedPageId.ToString("D"));
                await deletePage.ExecuteNonQueryAsync(cancellationToken);
            }

            for (var index = 0; index < pages.Length; index++)
            {
                var page = pages[index];
                if (!serializedPages.TryGetValue(page.Id, out var pageJson))
                {
                    await using var orderCommand = _connection.CreateCommand();
                    orderCommand.Transaction = transaction;
                    orderCommand.CommandText = """
                        UPDATE pages SET ordinal = $ordinal, title = $title,
                            recognized_text = $recognized, recognized_regions_json = $regions,
                            updated_utc = $updated
                        WHERE document_id = $document AND id = $id;
                        """;
                    orderCommand.Parameters.AddWithValue("$ordinal", page.Ordinal);
                    orderCommand.Parameters.AddWithValue("$title", page.Title);
                    orderCommand.Parameters.AddWithValue("$recognized", page.RecognizedText);
                    orderCommand.Parameters.AddWithValue("$regions", Serialize(page.RecognizedRegions));
                    orderCommand.Parameters.AddWithValue("$updated", page.UpdatedAt.ToString("O"));
                    orderCommand.Parameters.AddWithValue("$document", document.Id.ToString("D"));
                    orderCommand.Parameters.AddWithValue("$id", page.Id.ToString("D"));
                    await orderCommand.ExecuteNonQueryAsync(cancellationToken);
                    continue;
                }

                await using var pageCommand = _connection.CreateCommand();
                pageCommand.Transaction = transaction;
                pageCommand.CommandText = """
                    INSERT INTO pages(id,document_id,ordinal,title,page_json,recognized_text,recognized_regions_json,updated_utc)
                    VALUES($id,$document,$ordinal,$title,$json,$recognized,$regions,$updated)
                    ON CONFLICT(id) DO UPDATE SET document_id=excluded.document_id,
                        ordinal=excluded.ordinal, title=excluded.title, page_json=excluded.page_json,
                        recognized_text=excluded.recognized_text,
                        recognized_regions_json=excluded.recognized_regions_json,
                        updated_utc=excluded.updated_utc;
                    """;
                pageCommand.Parameters.AddWithValue("$id", page.Id.ToString("D"));
                pageCommand.Parameters.AddWithValue("$document", document.Id.ToString("D"));
                pageCommand.Parameters.AddWithValue("$ordinal", page.Ordinal);
                pageCommand.Parameters.AddWithValue("$title", page.Title);
                pageCommand.Parameters.AddWithValue("$json", pageJson);
                pageCommand.Parameters.AddWithValue("$recognized", page.RecognizedText);
                pageCommand.Parameters.AddWithValue("$regions", Serialize(page.RecognizedRegions));
                pageCommand.Parameters.AddWithValue("$updated", page.UpdatedAt.ToString("O"));
                await pageCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var clearAppends = _connection.CreateCommand())
            {
                clearAppends.Transaction = transaction;
                clearAppends.CommandText = """
                    DELETE FROM ink_append_journal WHERE page_id IN
                        (SELECT id FROM pages WHERE document_id = $document);
                    """;
                clearAppends.Parameters.AddWithValue("$document", document.Id.ToString("D"));
                await clearAppends.ExecuteNonQueryAsync(cancellationToken);
            }

            await ReindexAsync(document, changedSnapshots, removedPageIds, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<Dictionary<Guid, DateTimeOffset>> LoadPersistedPageVersionsAsync(Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.updated_utc,
                   EXISTS(SELECT 1 FROM ink_append_journal j WHERE j.page_id = p.id)
            FROM pages p WHERE p.document_id = $id;
            """;
        command.Parameters.AddWithValue("$id", documentId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var versions = new Dictionary<Guid, DateTimeOffset>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Guid.TryParse(reader.GetString(0), out var pageId) ||
                !DateTimeOffset.TryParse(reader.GetString(1), out var updatedAt)) continue;
            versions[pageId] = reader.GetBoolean(2) ? DateTimeOffset.MinValue : updatedAt;
        }
        return versions;
    }

    public async Task<bool> SaveInkAppendsAsync(HoomNoteDocument document,
        IReadOnlyList<(Guid PageId, InkStrokeObject Stroke)> appends,
        CancellationToken cancellationToken = default)
    {
        if (appends.Count == 0) return true;
        var pageIds = appends.Select(item => item.PageId).Distinct().ToArray();
        await using (var existsCommand = _connection.CreateCommand())
        {
            existsCommand.CommandText = $"""
                SELECT COUNT(*) FROM pages
                WHERE document_id = $document AND id IN ({string.Join(',', pageIds.Select((_, index) => $"$page{index}"))});
                """;
            existsCommand.Parameters.AddWithValue("$document", document.Id.ToString("D"));
            for (var index = 0; index < pageIds.Length; index++)
                existsCommand.Parameters.AddWithValue($"$page{index}", pageIds[index].ToString("D"));
            var existingCount = Convert.ToInt32(await existsCommand.ExecuteScalarAsync(cancellationToken));
            if (existingCount != pageIds.Length) return false;
        }

        var serialized = await Task.Run(() => appends.Select(item => (
            item.PageId,
            item.Stroke.Id,
            Json: JsonSerializer.Serialize<CanvasObject>(item.Stroke, HoomNoteJson.Options))).ToArray(), cancellationToken);
        document.UpdatedAt = DateTimeOffset.UtcNow;
        var pageUpdatedAt = document.Pages.Where(page => pageIds.Contains(page.Id))
            .ToDictionary(page => page.Id, page => page.UpdatedAt);
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var documentCommand = _connection.CreateCommand())
            {
                documentCommand.Transaction = transaction;
                documentCommand.CommandText = "UPDATE documents SET updated_utc = $updated WHERE id = $id;";
                documentCommand.Parameters.AddWithValue("$updated", document.UpdatedAt.ToString("O"));
                documentCommand.Parameters.AddWithValue("$id", document.Id.ToString("D"));
                await documentCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var (pageId, updatedAt) in pageUpdatedAt)
            {
                await using var pageCommand = _connection.CreateCommand();
                pageCommand.Transaction = transaction;
                pageCommand.CommandText = "UPDATE pages SET updated_utc = $updated WHERE id = $id;";
                pageCommand.Parameters.AddWithValue("$updated", updatedAt.ToString("O"));
                pageCommand.Parameters.AddWithValue("$id", pageId.ToString("D"));
                await pageCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var append in serialized)
            {
                await using var appendCommand = _connection.CreateCommand();
                appendCommand.Transaction = transaction;
                appendCommand.CommandText = """
                    INSERT OR IGNORE INTO ink_append_journal(page_id,object_id,object_json,created_utc)
                    VALUES($page,$object,$json,$created);
                    """;
                appendCommand.Parameters.AddWithValue("$page", append.PageId.ToString("D"));
                appendCommand.Parameters.AddWithValue("$object", append.Id.ToString("D"));
                appendCommand.Parameters.AddWithValue("$json", append.Json);
                appendCommand.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
                await appendCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task DeleteAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        await ExecuteWithTransactionAsync("DELETE FROM documents WHERE id = $id;", transaction, cancellationToken,
            ("$id", documentId.ToString("D")));
        await ExecuteWithTransactionAsync(
            $"DELETE FROM {(_ftsEnabled ? "search_fts" : "search_fallback")} WHERE document_id = $id;",
            transaction, cancellationToken, ("$id", documentId.ToString("D")));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var results = new List<SearchResult>();
        await using (var notebookCommand = _connection.CreateCommand())
        {
            notebookCommand.CommandText = """
                SELECT id, title FROM documents WHERE title LIKE $query
                ORDER BY updated_utc DESC LIMIT 25;
                """;
            notebookCommand.Parameters.AddWithValue("$query", $"%{query}%");
            await using var notebookReader = await notebookCommand.ExecuteReaderAsync(cancellationToken);
            while (await notebookReader.ReadAsync(cancellationToken))
                results.Add(new SearchResult(Guid.Parse(notebookReader.GetString(0)), null,
                    notebookReader.GetString(1), "Notebook", "Notebook title", "title"));
        }
        await using var command = _connection.CreateCommand();
        command.CommandText = _ftsEnabled
            ? """
              SELECT document_id, page_id, document_title, page_title,
                     snippet(search_fts, 4, '[', ']', ' … ', 12), source
              FROM search_fts WHERE search_fts MATCH $query LIMIT 100;
              """
            : """
              SELECT document_id, page_id, document_title, page_title, substr(body, 1, 180), source
              FROM search_fallback WHERE body LIKE $query OR document_title LIKE $query OR page_title LIKE $query
              LIMIT 100;
              """;
        command.Parameters.AddWithValue("$query", _ftsEnabled ? BuildFtsPrefixQuery(query) : $"%{query}%");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new SearchResult(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }

        return results.Take(100).ToArray();
    }

    public async Task SaveRecognizedTextAsync(HoomNoteDocument document, NotePage page, string recognizedText,
        IReadOnlyList<RecognizedTextRegion>? recognizedRegions = null,
        CancellationToken cancellationToken = default)
    {
        page.RecognizedText = recognizedText;
        if (recognizedRegions is not null) page.RecognizedRegions = [.. recognizedRegions];
        page.UpdatedAt = DateTimeOffset.UtcNow;
        document.UpdatedAt = DateTimeOffset.UtcNow;
        var typed = string.Join(Environment.NewLine,
            page.Objects.OfType<RichTextObject>().Select(text => text.Content.PlainText));
        var body = string.Join(Environment.NewLine, new[] { typed, recognizedText }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        var table = _ftsEnabled ? "search_fts" : "search_fallback";
        await using var transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var updateDocument = _connection.CreateCommand())
            {
                updateDocument.Transaction = transaction;
                updateDocument.CommandText = "UPDATE documents SET updated_utc = $updated WHERE id = $id;";
                updateDocument.Parameters.AddWithValue("$updated", document.UpdatedAt.ToString("O"));
                updateDocument.Parameters.AddWithValue("$id", document.Id.ToString("D"));
                await updateDocument.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var updatePage = _connection.CreateCommand())
            {
                updatePage.Transaction = transaction;
                updatePage.CommandText = """
                    UPDATE pages SET recognized_text = $recognized, recognized_regions_json = $regions,
                        updated_utc = $updated
                    WHERE document_id = $document AND id = $page;
                    """;
                updatePage.Parameters.AddWithValue("$recognized", recognizedText);
                updatePage.Parameters.AddWithValue("$regions", Serialize(page.RecognizedRegions));
                updatePage.Parameters.AddWithValue("$updated", page.UpdatedAt.ToString("O"));
                updatePage.Parameters.AddWithValue("$document", document.Id.ToString("D"));
                updatePage.Parameters.AddWithValue("$page", page.Id.ToString("D"));
                await updatePage.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var deleteSearch = _connection.CreateCommand())
            {
                deleteSearch.Transaction = transaction;
                deleteSearch.CommandText = $"DELETE FROM {table} WHERE document_id = $document AND page_id = $page;";
                deleteSearch.Parameters.AddWithValue("$document", document.Id.ToString("D"));
                deleteSearch.Parameters.AddWithValue("$page", page.Id.ToString("D"));
                await deleteSearch.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var insertSearch = _connection.CreateCommand())
            {
                insertSearch.Transaction = transaction;
                insertSearch.CommandText = $"""
                    INSERT INTO {table}(document_id,page_id,document_title,page_title,body,source)
                    VALUES($document,$page,$documentTitle,$pageTitle,$body,$source);
                    """;
                insertSearch.Parameters.AddWithValue("$document", document.Id.ToString("D"));
                insertSearch.Parameters.AddWithValue("$page", page.Id.ToString("D"));
                insertSearch.Parameters.AddWithValue("$documentTitle", document.Title);
                insertSearch.Parameters.AddWithValue("$pageTitle", page.Title);
                insertSearch.Parameters.AddWithValue("$body", body);
                insertSearch.Parameters.AddWithValue("$source", "typed + handwriting");
                await insertSearch.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private async Task ReindexAsync(HoomNoteDocument document, IReadOnlyList<NotePage> changedPages,
        IReadOnlyList<Guid> removedPageIds, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        var table = _ftsEnabled ? "search_fts" : "search_fallback";
        await using (var titleCommand = _connection.CreateCommand())
        {
            titleCommand.Transaction = transaction;
            titleCommand.CommandText = $"UPDATE {table} SET document_title = $title WHERE document_id = $document;";
            titleCommand.Parameters.AddWithValue("$title", document.Title);
            titleCommand.Parameters.AddWithValue("$document", document.Id.ToString("D"));
            await titleCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var pageId in removedPageIds.Concat(changedPages.Select(page => page.Id)).Distinct())
        {
            await using var deleteCommand = _connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = $"DELETE FROM {table} WHERE document_id = $document AND page_id = $page;";
            deleteCommand.Parameters.AddWithValue("$document", document.Id.ToString("D"));
            deleteCommand.Parameters.AddWithValue("$page", pageId.ToString("D"));
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var page in changedPages)
        {
            var typed = string.Join(Environment.NewLine,
                page.Objects.OfType<RichTextObject>().Select(text => text.Content.PlainText));
            var body = string.Join(Environment.NewLine, new[] { typed, page.RecognizedText }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO {table}(document_id,page_id,document_title,page_title,body,source)
                VALUES($document,$page,$documentTitle,$pageTitle,$body,$source);
                """;
            command.Parameters.AddWithValue("$document", document.Id.ToString("D"));
            command.Parameters.AddWithValue("$page", page.Id.ToString("D"));
            command.Parameters.AddWithValue("$documentTitle", document.Title);
            command.Parameters.AddWithValue("$pageTitle", page.Title);
            command.Parameters.AddWithValue("$body", body);
            command.Parameters.AddWithValue("$source", string.IsNullOrWhiteSpace(page.RecognizedText) ? "typed" : "typed + handwriting");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureRecognizedRegionsColumnAsync(CancellationToken cancellationToken)
    {
        await using var query = _connection.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM pragma_table_info('pages') WHERE name = 'recognized_regions_json';";
        var exists = Convert.ToInt32(await query.ExecuteScalarAsync(cancellationToken)) > 0;
        if (exists) return;
        await ExecuteAsync("ALTER TABLE pages ADD COLUMN recognized_regions_json TEXT NOT NULL DEFAULT '[]';",
            cancellationToken);
    }

    private async Task ExecuteWithTransactionAsync(string sql, SqliteTransaction transaction,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, HoomNoteJson.Options);
    private static T? Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, HoomNoteJson.Options);
    private static string BuildFtsPrefixQuery(string query)
    {
        var tokens = query.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => $"\"{token.Replace("\"", "\"\"", StringComparison.Ordinal)}\"*")
            .ToArray();
        return tokens.Length == 0 ? "\"\"" : string.Join(" AND ", tokens);
    }
}
