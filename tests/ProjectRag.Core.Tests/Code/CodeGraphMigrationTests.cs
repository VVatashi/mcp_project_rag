using Dapper;

using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using ProjectRag.Core.Storage;

namespace ProjectRag.Core.Tests.Code;

public sealed class CodeGraphMigrationTests
{
    private const string VariableName = "PROJECTRAG_TEST_DATABASE";

    [Fact]
    public async Task Migration10_ClearsTheGraphAndAddsTheColumnsOfTheUpdate()
    {
        string? connectionString = Environment.GetEnvironmentVariable(VariableName);
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString), $"Set {VariableName} to run the tests of the database.");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        NpgsqlConnectionStringBuilder builder = new(connectionString);
        string database = (builder.Database ?? throw new InvalidOperationException($"{VariableName} names no database.")) + "_migration";
        builder.Database = "postgres";
        await using (NpgsqlConnection admin = new(builder.ConnectionString))
        {
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await admin.ExecuteAsync($"CREATE DATABASE \"{database}\"");
        }

        builder.Database = database;
        NpgsqlConnection.ClearAllPools();
        DatabaseMigrator.MigrateUp(builder.ConnectionString, NullLoggerFactory.Instance, 9);

        await using NpgsqlConnection connection = new(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync("""
            INSERT INTO source (name, root_path, include_globs) VALUES ('demo', '/demo', '{**/*}');
            INSERT INTO code_project (source_id, path, name, is_test, document_count, error_count) VALUES (1, 'a.csproj', 'A', false, 1, 0);
            INSERT INTO code_symbol (source_id, project_id, kind, name, full_name, doc_id, signature, namespace, accessibility, modifiers)
                VALUES (1, 1, 'class', 'A', 'A', 'T:A', 'class A', '', 'public', '{}');
            INSERT INTO code_type_edge (source_id, from_id, to_id, kind, count) VALUES (1, 1, 1, 'call', 1);
            INSERT INTO code_graph_state (source_id, built_at, input_hash, extractor_version, symbol_count) VALUES (1, now(), 'h', 1, 1);
            """);

        DatabaseMigrator.MigrateUp(builder.ConnectionString, NullLoggerFactory.Instance);
        NpgsqlConnection.ClearAllPools();

        foreach (string table in new[] { "code_project", "code_symbol", "code_type_edge", "code_graph_state" })
            Assert.Equal(0, await connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table}"));

        Assert.Equal(1, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM source"));
        await connection.ExecuteAsync("""
            INSERT INTO code_graph_state (source_id) VALUES (1);
            INSERT INTO code_type_edge (source_id, from_id, to_id, kind, count, document_id) VALUES (1, 1, 1, 'call', 1, 5);
            """);
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT update_count FROM code_graph_state"));
        Assert.Null(await connection.ExecuteScalarAsync<DateTime?>("SELECT updated_at FROM code_graph_state"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM pg_indexes WHERE tablename = 'code_type_edge' AND indexname LIKE '%document%'"));
    }
}
