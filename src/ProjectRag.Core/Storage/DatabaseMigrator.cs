using FluentMigrator.Runner;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using ProjectRag.Core.Storage.Migrations;

namespace ProjectRag.Core.Storage;

/// <summary>
/// Brings the schema of the database to the last migration.
/// </summary>
/// <remarks>
/// The runner of FluentMigrator gets a service provider of its own, so its services stay out of the container of the application.
/// The application runs the migrations before its first query: Npgsql reads the types of the database, such as <c>vector</c>, on the first connection.
/// </remarks>
public static class DatabaseMigrator
{
    /// <summary>
    /// Applies the migrations that the database does not have yet.
    /// </summary>
    /// <param name="connectionString">The connection string of the database.</param>
    /// <param name="loggers">The loggers of the application. The runner logs each migration through them.</param>
    public static void MigrateUp(string connectionString, ILoggerFactory loggers) => Migrate(connectionString, loggers, null);

    /// <summary>
    /// Applies the migrations up to a version.
    /// </summary>
    /// <param name="connectionString">The connection string of the database.</param>
    /// <param name="loggers">The loggers of the application.</param>
    /// <param name="version">The number of the last migration to apply.</param>
    public static void MigrateUp(string connectionString, ILoggerFactory loggers, long version) => Migrate(connectionString, loggers, version);

    private static void Migrate(string connectionString, ILoggerFactory loggers, long? version)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentNullException.ThrowIfNull(loggers);

        using ServiceProvider services = new ServiceCollection()
            .AddSingleton(loggers)
            .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
            .AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(InitialSchema).Assembly).For.Migrations())
            .BuildServiceProvider(validateScopes: false);

        using IServiceScope scope = services.CreateScope();
        IMigrationRunner runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        if (version is { } last)
            runner.MigrateUp(last);
        else
            runner.MigrateUp();
    }
}
