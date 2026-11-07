using Dapper;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Tests.Storage;

public sealed class ProseTsvMigrationTests
{
    private const string VariableName = "PROJECTRAG_TEST_DATABASE";

    [Fact]
    public async Task Migration11_LeavesTsvNullForCodeChunksAndKeepsItForProse()
    {
        string? connectionString = Environment.GetEnvironmentVariable(VariableName);
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), $"Set {VariableName} to run the tests of the database.");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string database = (builder.Database ?? throw new InvalidOperationException($"{VariableName} names no database.")) + "_prose_tsv";
        builder.Database = "postgres";
        await using (NpgsqlConnection admin = new(builder.ConnectionString))
        {
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await admin.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        }

        builder.Database = database;
        NpgsqlConnection.ClearAllPools();
        DatabaseMigrator.MigrateUp(builder.ConnectionString, NullLoggerFactory.Instance, 10);

        await using NpgsqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("""
            INSERT INTO source (name, root_path, include_globs) VALUES ('demo', '/demo', '{**/*}');
            INSERT INTO document (source_id, path, title, content, content_hash, parser_version, size_bytes, modified_at)
                VALUES (1, 'a.md', 'a', 'x', 'h1', '1', 1, now()), (1, 'a.cs', 'a', 'x', 'h2', '1', 1, now());
            INSERT INTO chunk (document_id, ordinal, heading, body, char_start, char_end, body_hash, kind)
                VALUES (1, 0, 'Guide', 'The synchronizer pairs the scene', 0, 1, 'c1', 'prose'), (2, 0, 'a.cs', 'The synchronizer pairs the scene', 0, 1, 'c2', 'code');
            """);
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM chunk WHERE tsv IS NOT NULL"));

        DatabaseMigrator.MigrateUp(builder.ConnectionString, NullLoggerFactory.Instance);
        NpgsqlConnection.ClearAllPools();

        Assert.Equal("code", await connection.ExecuteScalarAsync<string>("SELECT kind FROM chunk WHERE tsv IS NULL"));
        Assert.Equal("prose", await connection.ExecuteScalarAsync<string>("SELECT kind FROM chunk WHERE tsv @@ plainto_tsquery('russian', 'synchronizer')"));
        Assert.Contains("WHERE (kind = 'prose'", await connection.ExecuteScalarAsync<string>("SELECT indexdef FROM pg_indexes WHERE indexname = 'chunk_tsv'"), StringComparison.Ordinal);

        await connection.ExecuteAsync("INSERT INTO chunk (document_id, ordinal, heading, body, char_start, char_end, body_hash, kind) VALUES (2, 1, 'a.cs', 'another synchronizer', 0, 1, 'c3', 'code')");
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM chunk WHERE tsv IS NOT NULL"));
    }
}
