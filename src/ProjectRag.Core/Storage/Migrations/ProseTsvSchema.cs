using FluentMigrator;

namespace ProjectRag.Core.Storage.Migrations;

/// <summary>
/// The full text vector of the documentation only: the column <c>tsv</c> is NULL for the chunks of code files.
/// </summary>
/// <remarks>
/// <para>
/// The documentation search filters by the kind of the chunk after the lookup in the GIN index.
/// With the code chunks in the index, a common word returned thousands of them before the filter dropped them.
/// Now only the chunks of the kind <c>prose</c> have a vector, and the GIN index is partial.
/// The query must repeat <c>kind = 'prose'</c>, so the planner can use the index, see <see cref="Search.SearchStore"/>.
/// </para>
/// <para>
/// A generated column cannot change its expression, so the migration drops and adds it. PostgreSQL writes the table again.
/// </para>
/// </remarks>
[Migration(11, "The full text vector of the documentation only")]
public sealed class ProseTsvSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            DROP INDEX chunk_tsv;
            ALTER TABLE chunk DROP COLUMN tsv;
            ALTER TABLE chunk ADD COLUMN tsv tsvector GENERATED ALWAYS AS (
                CASE WHEN kind = 'prose' THEN
                    setweight(to_tsvector('russian'::regconfig, heading), 'A')
                    || setweight(to_tsvector('russian'::regconfig, body), 'B')
                END) STORED;
            CREATE INDEX chunk_tsv ON chunk USING gin (tsv) WHERE kind = 'prose';
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP INDEX chunk_tsv;
            ALTER TABLE chunk DROP COLUMN tsv;
            ALTER TABLE chunk ADD COLUMN tsv tsvector GENERATED ALWAYS AS (
                setweight(to_tsvector('russian'::regconfig, heading), 'A')
                || setweight(to_tsvector('russian'::regconfig, body), 'B')) STORED;
            CREATE INDEX chunk_tsv ON chunk USING gin (tsv);
            """);
    }
}
