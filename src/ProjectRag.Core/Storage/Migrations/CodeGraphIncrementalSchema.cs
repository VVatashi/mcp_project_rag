using FluentMigrator;

namespace ProjectRag.Core.Storage.Migrations;

/// <summary>
/// The files of the edges of the code graph and the counters of the incremental updates, see ADR 0003.
/// </summary>
/// <remarks>
/// <para>
/// A type edge, an external edge and a member link now belong to the file of their references. A patch of the graph replaces the rows of its files,
/// and the delete of a document removes its rows. The old rows hold the sums over all files and have no file.
/// So this migration clears the graph tables and the graph states, and every code source builds its graph again at the next run of the indexer.
/// The new columns are not null: the writer fills them in every row.
/// </para>
/// <para>
/// The state of a graph gets the time of the last update and the number of updates since the last full build.
/// </para>
/// </remarks>
[Migration(10, "The files of the edges of the code graph")]
public sealed class CodeGraphIncrementalSchema : Migration
{
    public override void Up()
    {
        Execute.Sql("""
            TRUNCATE TABLE code_diagnostic, code_member_link, code_external_edge, code_type_edge, code_reference, code_declaration, code_symbol,
                code_file_project, code_solution_project, code_solution, code_project_reference, code_project, code_graph_state;

            ALTER TABLE code_type_edge ADD COLUMN document_id bigint NOT NULL;
            ALTER TABLE code_external_edge ADD COLUMN document_id bigint NOT NULL;
            ALTER TABLE code_member_link ADD COLUMN document_id bigint NOT NULL;
            CREATE INDEX code_type_edge_document ON code_type_edge (document_id);
            CREATE INDEX code_external_edge_document ON code_external_edge (document_id);
            CREATE INDEX code_member_link_document ON code_member_link (document_id);

            ALTER TABLE code_graph_state ADD COLUMN updated_at timestamptz;
            ALTER TABLE code_graph_state ADD COLUMN update_count integer NOT NULL DEFAULT 0;
            ALTER TABLE code_graph_state ADD COLUMN update_duration_ms integer;

            CREATE OR REPLACE FUNCTION code_graph_delete_documents() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                DELETE FROM code_reference r USING old_rows o WHERE r.document_id = o.document_id;
                DELETE FROM code_declaration d USING old_rows o WHERE d.document_id = o.document_id;
                DELETE FROM code_type_edge e USING old_rows o WHERE e.document_id = o.document_id;
                DELETE FROM code_external_edge e USING old_rows o WHERE e.document_id = o.document_id;
                DELETE FROM code_member_link l USING old_rows o WHERE l.document_id = o.document_id;
                RETURN NULL;
            END
            $$;
            """);
    }

    public override void Down()
    {
        Execute.Sql("""
            CREATE OR REPLACE FUNCTION code_graph_delete_documents() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                DELETE FROM code_reference r USING old_rows o WHERE r.document_id = o.document_id;
                DELETE FROM code_declaration d USING old_rows o WHERE d.document_id = o.document_id;
                RETURN NULL;
            END
            $$;

            ALTER TABLE code_graph_state DROP COLUMN update_duration_ms;
            ALTER TABLE code_graph_state DROP COLUMN update_count;
            ALTER TABLE code_graph_state DROP COLUMN updated_at;

            DROP INDEX code_member_link_document;
            DROP INDEX code_external_edge_document;
            DROP INDEX code_type_edge_document;
            ALTER TABLE code_member_link DROP COLUMN document_id;
            ALTER TABLE code_external_edge DROP COLUMN document_id;
            ALTER TABLE code_type_edge DROP COLUMN document_id;
            """);
    }
}
