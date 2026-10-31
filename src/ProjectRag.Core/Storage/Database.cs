using Dapper;

using Npgsql;

namespace ProjectRag.Core.Storage;

/// <summary>
/// The access to the PostgreSQL database of the knowledge base.
/// </summary>
/// <remarks>
/// The stores read and write with Dapper over Npgsql. The columns use snake_case, and Dapper maps them to the properties in PascalCase.
/// A vector goes to the database as an array of <c>real</c> and the SQL casts it with <c>::real[]::vector</c>, so the application needs no vector type of its own.
/// </remarks>
/// <param name="dataSource">The pool of connections.</param>
public sealed class Database(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// The name of the connection string in the configuration section <c>ConnectionStrings</c>.
    /// </summary>
    public const string ConnectionStringName = "ProjectRag";

    /// <summary>
    /// Sets the global options of Dapper. The call is safe to repeat.
    /// </summary>
    public static void ConfigureDapper() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    /// <summary>
    /// Opens a connection from the pool.
    /// </summary>
    /// <param name="cancellationToken">The token that cancels the wait for a connection.</param>
    /// <returns>The open connection. The caller disposes it.</returns>
    public ValueTask<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken) => dataSource.OpenConnectionAsync(cancellationToken);
}
