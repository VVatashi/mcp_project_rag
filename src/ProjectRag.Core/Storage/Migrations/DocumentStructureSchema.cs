using FluentMigrator;

namespace ProjectRag.Core.Storage.Migrations;

/// <summary>
/// The structure of the documents: sections, anchors, the links of the chunks, and the section, the pages and the overlap of each chunk.
/// </summary>
/// <remarks>
/// A link stores its target as a path and an anchor, not as a key. The target document may come later in the same run, or it may change on its own.
/// The queries resolve a link through the table <c>anchor</c> to the chunk that holds the anchor.
/// </remarks>
[Migration(2, "The structure of the documents")]
public sealed class DocumentStructureSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            -- The version of the readers and of the options of the chunker, for example "2;1800;3000;300".
            ALTER TABLE document ALTER COLUMN parser_version TYPE text USING parser_version::text;

            -- A section of a document: the text from a heading to the next heading of the same or a higher level.
            -- The section with the ordinal 0 holds the text before the first heading.
            CREATE TABLE section (
                section_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                document_id  bigint NOT NULL REFERENCES document ON DELETE CASCADE,
                ordinal      integer NOT NULL,
                parent_id    bigint REFERENCES section ON DELETE CASCADE,
                level        smallint NOT NULL,
                number       text,              -- the number at the start of the heading: 8.14.2
                title        text NOT NULL,
                anchor       text,
                path         text NOT NULL,     -- Title > Section > Subsection
                char_start   integer NOT NULL,
                char_end     integer NOT NULL,
                page_start   integer,
                page_end     integer,
                UNIQUE (document_id, ordinal)
            );
            CREATE INDEX section_number ON section (document_id, number);

            -- The places that links point at: the identifiers of the headings and the anchors of the importers (tables, formulas, link targets of a PDF file).
            CREATE TABLE anchor (
                document_id  bigint NOT NULL REFERENCES document ON DELETE CASCADE,
                name         text NOT NULL,
                char_offset  integer NOT NULL,
                PRIMARY KEY (document_id, name)
            );

            ALTER TABLE chunk
                ADD COLUMN section_id bigint REFERENCES section ON DELETE SET NULL,
                ADD COLUMN page_start integer,
                ADD COLUMN page_end integer,
                ADD COLUMN overlap_chars integer NOT NULL DEFAULT 0;
            CREATE INDEX chunk_section ON chunk (section_id);

            -- A link from a chunk to an anchor of the same document (target_path is NULL) or of another document of the source.
            CREATE TABLE chunk_link (
                chunk_id       bigint NOT NULL REFERENCES chunk ON DELETE CASCADE,
                ordinal        integer NOT NULL,
                kind           text NOT NULL CHECK (kind IN ('link', 'mention')),
                label          text NOT NULL,
                target_path    text,
                target_anchor  text,
                char_start     integer NOT NULL,
                PRIMARY KEY (chunk_id, ordinal)
            );
            CREATE INDEX chunk_link_anchor ON chunk_link (target_anchor);
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            DROP TABLE IF EXISTS chunk_link;
            ALTER TABLE chunk DROP COLUMN IF EXISTS section_id, DROP COLUMN IF EXISTS page_start, DROP COLUMN IF EXISTS page_end, DROP COLUMN IF EXISTS overlap_chars;
            DROP TABLE IF EXISTS anchor;
            DROP TABLE IF EXISTS section;
            ALTER TABLE document ALTER COLUMN parser_version TYPE integer USING 0;
            """);
    }
}
