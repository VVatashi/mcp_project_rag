using FluentMigrator;

namespace ProjectRAG.Cli.Migrations;

/// <summary>Creates the two tables of the index and the indexes that serve the two search modes.</summary>
[Migration(2026_09_04_0001, "Documents and chunks with full text and vector search")]
public sealed class CreateDocumentsAndChunks : Migration
{
    /// <summary>The length of one embedding. The migration keeps its own number because the table it created never changes.</summary>
    private const int VectorSize = 1024;

    public override void Up()
    {
        Execute.Sql("CREATE EXTENSION IF NOT EXISTS vector;");

        // The whole text of a file lives in documents.content as well as in the chunks. It costs a second copy and
        // pays for itself when the chunk size changes, because the chunks can be built again without the file.
        Create.Table("documents")
            .WithColumn("id").AsInt64().NotNullable().PrimaryKey().Identity()
            .WithColumn("uuid").AsCustom("uuid").NotNullable().Unique().WithDefaultValue(RawSql.Insert("gen_random_uuid()"))
            .WithColumn("title").AsCustom("text").NotNullable()
            .WithColumn("path").AsCustom("text").NotNullable()
            .WithColumn("hash").AsCustom("text").NotNullable()
            .WithColumn("content").AsCustom("text").NotNullable()
            .WithColumn("indexed_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(RawSql.Insert("now()"));

        // Windows spells one path in many cases, so the key of a document is its path in lower case.
        // Two files that differ only in case therefore count as one document, which no document set relies on.
        Execute.Sql("CREATE UNIQUE INDEX documents_path_idx ON documents (lower(path));");

        Create.Table("chunks")
            .WithColumn("id").AsInt64().NotNullable().PrimaryKey().Identity()
            .WithColumn("document_id").AsInt64().NotNullable().ForeignKey("chunks_document_id_fkey", "documents", "id").OnDelete(System.Data.Rule.Cascade)
            .WithColumn("ordinal").AsInt32().NotNullable()
            .WithColumn("heading_path").AsCustom("text[]").NotNullable().WithDefaultValue(RawSql.Insert("'{}'"))
            .WithColumn("headings").AsCustom("text").NotNullable().WithDefaultValue(string.Empty)
            .WithColumn("start_line").AsInt32().NotNullable()
            .WithColumn("end_line").AsInt32().NotNullable()
            .WithColumn("content").AsCustom("text").NotNullable()
            .WithColumn("embedding").AsCustom($"vector({VectorSize})").NotNullable()
            .WithColumn("token_count").AsInt32().NotNullable()
            .WithColumn("created_at").AsCustom("timestamptz").NotNullable().WithDefaultValue(RawSql.Insert("now()"));

        Create.UniqueConstraint("chunks_document_id_ordinal_key").OnTable("chunks").Columns("document_id", "ordinal");
        Create.Index("chunks_document_id_idx").OnTable("chunks").OnColumn("document_id").Ascending();

        // The generated column, the vector index and the full text index need syntax the fluent interface does not write.
        // A generated column takes immutable functions only, and array_to_string is stable, so the joined heading path
        // lives in its own column. The headings carry weight A and the body carries weight B, which ranks a title match higher.
        Execute.Sql($"""
            ALTER TABLE chunks ADD COLUMN content_tsv tsvector GENERATED ALWAYS AS (
                setweight(to_tsvector('{DatabaseOptions.TextSearchConfiguration}', headings), 'A') ||
                setweight(to_tsvector('{DatabaseOptions.TextSearchConfiguration}', content), 'B')) STORED;
            """);
        Execute.Sql("CREATE INDEX chunks_content_tsv_idx ON chunks USING gin (content_tsv);");
        Execute.Sql("CREATE INDEX chunks_embedding_idx ON chunks USING hnsw (embedding vector_cosine_ops);");
    }

    public override void Down()
    {
        Delete.Table("chunks");
        Delete.Table("documents");
    }
}
