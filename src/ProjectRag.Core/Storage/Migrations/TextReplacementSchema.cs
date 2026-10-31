using FluentMigrator;

namespace ProjectRag.Core.Storage.Migrations;

/// <summary>
/// The replacements of a source that fix the text of some of its files, such as the lost brackets of a published PDF file.
/// </summary>
[Migration(4, "The replacements of the sources")]
public sealed class TextReplacementSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            -- The replacements in their order: [{"pattern": "Metal-*.pdf", "find": "sparse_color-(Tv?)\b", "replace": "sparse_color<$1>"}].
            ALTER TABLE source ADD COLUMN replacements jsonb NOT NULL DEFAULT '[]';
            """);
    }

    public override void Down()
    {
        Execute.Sql("ALTER TABLE source DROP COLUMN replacements;");
    }
}
