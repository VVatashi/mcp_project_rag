using FluentMigrator;

namespace ProjectRag.Core.Storage.Migrations;

/// <summary>
/// The web pages of a source, which the indexer reads by their addresses next to the files of the folder.
/// </summary>
[Migration(5, "The web pages of the sources")]
public sealed class SourcePageSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            -- The addresses of the pages: {https://registry.khronos.org/KTX/specs/2.0/ktxspec.v2.html}. A source of pages only has an empty root_path.
            ALTER TABLE source ADD COLUMN urls text[] NOT NULL DEFAULT '{}';
            """);
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE source DROP COLUMN urls;");
    }
}
