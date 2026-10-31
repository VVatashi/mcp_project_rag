using FluentMigrator;

namespace ProjectRag.Core.Storage.Migrations;

/// <summary>
/// The first schema: sources, documents, chunks, embedding models, embeddings and index runs.
/// </summary>
/// <remarks>
/// <para>
/// The schema follows the database of the project rag (ADR 0046 there). A chunk is the unit of the search.
/// Its text has a full text vector and an embedding of each model.
/// </para>
/// <para>
/// The column <c>chunk_embedding.embedding</c> has no dimension, so it holds the vectors of any model.
/// Each model gets its own partial HNSW index over <c>halfvec</c>, see <see cref="Embeddings.EmbeddingStore"/>.
/// </para>
/// </remarks>
[Migration(1, "The initial schema")]
public sealed class InitialSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            CREATE EXTENSION IF NOT EXISTS vector;
            CREATE EXTENSION IF NOT EXISTS pg_trgm;

            -- A source is a folder on the host with documents of one project.
            CREATE TABLE source (
                source_id      integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name           text NOT NULL UNIQUE CHECK (name ~ '^[a-z0-9][a-z0-9._-]{0,63}$'),
                description    text NOT NULL DEFAULT '',
                root_path      text NOT NULL,
                include_globs  text[] NOT NULL,
                exclude_globs  text[] NOT NULL DEFAULT '{}',
                created_at     timestamptz NOT NULL DEFAULT now(),
                indexed_at     timestamptz
            );

            -- A document is one file of a source. The chunks point into its content.
            -- For a PDF file the content is the extracted text with the bookmarks as Markdown headings.
            CREATE TABLE document (
                document_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                source_id       integer NOT NULL REFERENCES source ON DELETE CASCADE,
                path            text NOT NULL,          -- relative to the root of the source, with forward slashes
                title           text NOT NULL,
                content         text NOT NULL,
                content_hash    text NOT NULL,          -- SHA-256 of the file bytes
                parser_version  integer NOT NULL,       -- a new version of the reader or the chunker reads the file again
                size_bytes      bigint NOT NULL,
                modified_at     timestamptz NOT NULL,   -- the time of the last write of the file
                indexed_at      timestamptz NOT NULL DEFAULT now(),
                UNIQUE (source_id, path)
            );
            CREATE INDEX document_path_trgm ON document USING gin (path gin_trgm_ops);
            CREATE INDEX document_title_trgm ON document USING gin (title gin_trgm_ops);

            -- A chunk is a part of a section. The heading holds the path of the section: "Title > Section > Subsection".
            -- The embedding model sees the heading and the body, and body_hash is the hash of that text.
            -- The configuration russian stems Cyrillic words with the Russian stemmer and Latin words with the English stemmer.
            CREATE TABLE chunk (
                chunk_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                document_id  bigint NOT NULL REFERENCES document ON DELETE CASCADE,
                ordinal      integer NOT NULL,
                heading      text NOT NULL,
                body         text NOT NULL,
                char_start   integer NOT NULL,
                char_end     integer NOT NULL,
                body_hash    text NOT NULL,
                tsv          tsvector GENERATED ALWAYS AS (
                                 setweight(to_tsvector('russian'::regconfig, heading), 'A')
                                 || setweight(to_tsvector('russian'::regconfig, body), 'B')) STORED
            );
            CREATE INDEX chunk_document ON chunk (document_id, ordinal);
            CREATE INDEX chunk_body_hash ON chunk (body_hash);
            CREATE INDEX chunk_tsv ON chunk USING gin (tsv);

            -- An embedding model. The search must embed the query with the model of the stored vectors.
            -- The identity holds what changes a vector: the name of the weights, the dimensions and the templates.
            -- The probe guards the rest: the application embeds probe_text and compares the result with probe_embedding.
            CREATE TABLE embedding_model (
                model_id           smallint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name               text NOT NULL,
                dims               integer NOT NULL CHECK (dims BETWEEN 1 AND 4000),
                query_template     text NOT NULL,
                document_template  text NOT NULL,
                probe_text         text NOT NULL,
                probe_embedding    vector NOT NULL,
                created_at         timestamptz NOT NULL DEFAULT now(),
                CHECK (vector_dims(probe_embedding) = dims),
                UNIQUE (name, dims, query_template, document_template)
            );

            CREATE TABLE chunk_embedding (
                chunk_id   bigint NOT NULL REFERENCES chunk ON DELETE CASCADE,
                model_id   smallint NOT NULL REFERENCES embedding_model ON DELETE CASCADE,
                embedding  vector NOT NULL,
                PRIMARY KEY (model_id, chunk_id)
            );
            CREATE INDEX chunk_embedding_chunk ON chunk_embedding (chunk_id);

            -- A vector of the wrong size would break the index of its model.
            CREATE FUNCTION chunk_embedding_check_dims() RETURNS trigger
            LANGUAGE plpgsql AS $$
            DECLARE
                expected integer;
            BEGIN
                SELECT dims INTO STRICT expected FROM embedding_model WHERE model_id = NEW.model_id;
                IF vector_dims(NEW.embedding) <> expected THEN
                    RAISE EXCEPTION 'The embedding has % dimensions, the model % expects %', vector_dims(NEW.embedding), NEW.model_id, expected;
                END IF;
                RETURN NEW;
            END $$;

            CREATE TRIGGER chunk_embedding_dims BEFORE INSERT OR UPDATE ON chunk_embedding
            FOR EACH ROW EXECUTE FUNCTION chunk_embedding_check_dims();

            -- One row for each run of the indexer over a source.
            CREATE TABLE index_run (
                run_id               bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                source_id            integer NOT NULL REFERENCES source ON DELETE CASCADE,
                status               text NOT NULL CHECK (status IN ('running', 'succeeded', 'failed', 'canceled')),
                started_at           timestamptz NOT NULL DEFAULT now(),
                finished_at          timestamptz,
                files_seen           integer NOT NULL DEFAULT 0,
                documents_added      integer NOT NULL DEFAULT 0,
                documents_updated    integer NOT NULL DEFAULT 0,
                documents_removed    integer NOT NULL DEFAULT 0,
                documents_unchanged  integer NOT NULL DEFAULT 0,
                documents_failed     integer NOT NULL DEFAULT 0,
                chunks_reused        integer NOT NULL DEFAULT 0,
                chunks_embedded      integer NOT NULL DEFAULT 0,
                warning              text,
                error                text
            );
            CREATE INDEX index_run_source ON index_run (source_id, run_id DESC);
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TABLE IF EXISTS index_run;
            DROP TABLE IF EXISTS chunk_embedding;
            DROP FUNCTION IF EXISTS chunk_embedding_check_dims();
            DROP TABLE IF EXISTS embedding_model;
            DROP TABLE IF EXISTS chunk;
            DROP TABLE IF EXISTS document;
            DROP TABLE IF EXISTS source;
            """);
    }
}
