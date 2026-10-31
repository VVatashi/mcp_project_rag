using FluentMigrator;

namespace ProjectRag.Core.Storage.Migrations;

/// <summary>
/// The categories of the documents, such as <c>csharp</c> or <c>vulkan</c>, and the rules of the sources that give them.
/// </summary>
/// <remarks>
/// One folder may hold the documentation of several topics, so the category belongs to a document, not to its source.
/// A rule of a source matches the path of a document with a pattern. A document without a matching rule takes the name of its source.
/// </remarks>
[Migration(3, "The categories of the documents")]
public sealed class DocumentCategorySchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            -- The rules in their order: [{"pattern": "ECMA-33*.pdf", "category": "csharp"}]. The first matching rule wins.
            ALTER TABLE source ADD COLUMN category_rules jsonb NOT NULL DEFAULT '[]';

            ALTER TABLE document ADD COLUMN category text NOT NULL DEFAULT '';
            UPDATE document d SET category = s.name FROM source s WHERE s.source_id = d.source_id;
            CREATE INDEX document_category ON document (category);
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP INDEX document_category;
            ALTER TABLE document DROP COLUMN category;
            ALTER TABLE source DROP COLUMN category_rules;
            """);
    }
}
