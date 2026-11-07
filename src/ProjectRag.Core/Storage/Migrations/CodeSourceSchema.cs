using FluentMigrator;

namespace ProjectRag.Core.Storage.Migrations;

/// <summary>
/// The code bases: the kind of a source, the patterns that bring dot files back, the language of a document and the trigram index of its content.
/// </summary>
/// <remarks>
/// A source of the kind <c>code</c> is a local code base, see ADR 0003. The language is NULL for the documents of the sources of the kind <c>docs</c>.
/// The index covers the documents with a language only, so a query of the exact search must repeat <c>language IS NOT NULL</c>.
/// </remarks>
[Migration(7, "The code sources")]
public sealed class CodeSourceSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE source ADD COLUMN kind text NOT NULL DEFAULT 'docs' CHECK (kind IN ('docs', 'code'));
            ALTER TABLE source ADD COLUMN dot_globs text[] NOT NULL DEFAULT '{}';

            ALTER TABLE document ADD COLUMN language text;
            CREATE INDEX document_content_trgm ON document USING gin (content gin_trgm_ops) WHERE language IS NOT NULL;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP INDEX document_content_trgm;
            ALTER TABLE document DROP COLUMN language;
            ALTER TABLE source DROP COLUMN dot_globs;
            ALTER TABLE source DROP COLUMN kind;
            """);
    }
}
