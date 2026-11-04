using FluentMigrator;

namespace ProjectRag.Core.Storage.Migrations;

/// <summary>
/// The index on the parent of a section.
/// A delete of a document removes its sections. The database then looks up the children of each removed section.
/// Without the index each look-up reads the whole table, so the delete of a large document timed out.
/// </summary>
[Migration(6, "The index on the parents of the sections")]
public sealed class SectionParentIndexSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("CREATE INDEX section_parent ON section (parent_id);");
    }

    public override void Down()
    {
        Execute.Sql("DROP INDEX IF EXISTS section_parent;");
    }
}
