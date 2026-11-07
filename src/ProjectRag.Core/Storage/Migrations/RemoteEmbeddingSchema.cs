using FluentMigrator;

namespace ProjectRag.Core.Storage.Migrations;

/// <summary>
/// The counters of a run for the remote embedding backend.
/// </summary>
[Migration(12, "The remote embedding counters of the runs")]
public sealed class RemoteEmbeddingSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            -- chunks_embedded counts all new vectors of the run. chunks_embedded_remote counts the part that the remote backend made. remote_cost is in USD.
            ALTER TABLE index_run
                ADD COLUMN chunks_embedded_remote integer NOT NULL DEFAULT 0,
                ADD COLUMN remote_cost numeric NOT NULL DEFAULT 0;
            """);
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE index_run DROP COLUMN chunks_embedded_remote, DROP COLUMN remote_cost;");
    }
}
