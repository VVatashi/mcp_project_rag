using FluentMigrator;

namespace ProjectRag.Core.Storage.Migrations;

/// <summary>
/// The chunks of code files: the kind of a chunk, its lines, the token vector, and the new identity of an embedding model, see ADR 0003.
/// </summary>
/// <remarks>
/// <para>
/// The kind is <c>prose</c> for the chunks of documentation, <c>code</c> for source code and <c>config</c> for data files.
/// The documentation search keeps the chunks of the kind <c>prose</c> only. The code search keeps the other two kinds.
/// </para>
/// <para>
/// The column <c>code_tsv</c> uses the configuration <c>simple</c> over identifiers that the application splits, see <c>IdentifierTokens</c>.
/// Only code chunks fill it, so its GIN index is partial and also tells the kind.
/// </para>
/// <para>
/// The HNSW index of a model stays whole. The kind sits on the table <c>chunk</c>, and an index predicate cannot read another table.
/// Both searches filter by kind after the walk of the index, and the iterative scan of pgvector walks on until enough rows pass.
/// </para>
/// <para>
/// The query template leaves the identity of a model, because it changes no vector of a document. The migration merges the models that differed only by it.
/// The merged model keeps the vectors of the others where it has none.
/// </para>
/// </remarks>
[Migration(9, "The chunks of code files")]
public sealed class CodeChunkSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            ALTER TABLE chunk ADD COLUMN kind text NOT NULL DEFAULT 'prose' CHECK (kind IN ('prose', 'code', 'config'));
            ALTER TABLE chunk ADD COLUMN line_start integer;
            ALTER TABLE chunk ADD COLUMN line_end integer;
            ALTER TABLE chunk ADD COLUMN code_tsv tsvector;
            CREATE INDEX chunk_code_tsv ON chunk USING gin (code_tsv) WHERE code_tsv IS NOT NULL;

            DO $$
            DECLARE
                model record;
                existing record;
            BEGIN
                FOR model IN
                    SELECT model_id, min(model_id) OVER (PARTITION BY name, dims, document_template) AS keep_id FROM embedding_model
                LOOP
                    IF model.model_id <> model.keep_id THEN
                        EXECUTE format('DROP INDEX IF EXISTS chunk_embedding_hnsw_m%s', model.model_id);
                        INSERT INTO chunk_embedding (chunk_id, model_id, embedding)
                        SELECT chunk_id, model.keep_id, embedding FROM chunk_embedding WHERE model_id = model.model_id
                        ON CONFLICT DO NOTHING;
                        DELETE FROM embedding_model WHERE model_id = model.model_id;
                    END IF;
                END LOOP;

                FOR existing IN SELECT conname FROM pg_constraint WHERE conrelid = 'embedding_model'::regclass AND contype = 'u'
                LOOP
                    EXECUTE format('ALTER TABLE embedding_model DROP CONSTRAINT %I', existing.conname);
                END LOOP;
            END
            $$;
            ALTER TABLE embedding_model DROP COLUMN query_template;
            ALTER TABLE embedding_model ADD CONSTRAINT embedding_model_identity UNIQUE (name, dims, document_template);
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            ALTER TABLE embedding_model DROP CONSTRAINT embedding_model_identity;
            ALTER TABLE embedding_model ADD COLUMN query_template text NOT NULL DEFAULT '';
            ALTER TABLE embedding_model ADD UNIQUE (name, dims, query_template, document_template);

            DELETE FROM chunk WHERE kind <> 'prose';
            DROP INDEX chunk_code_tsv;
            ALTER TABLE chunk DROP COLUMN code_tsv;
            ALTER TABLE chunk DROP COLUMN line_end;
            ALTER TABLE chunk DROP COLUMN line_start;
            ALTER TABLE chunk DROP COLUMN kind;
            """);
    }
}
