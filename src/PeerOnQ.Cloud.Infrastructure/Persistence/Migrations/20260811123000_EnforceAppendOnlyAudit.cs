using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeerOnQ.Cloud.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudDbContext))]
[Migration("20260811123000_EnforceAppendOnlyAudit")]
public partial class EnforceAppendOnlyAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION peeronq_reject_append_only_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $peeronq$
            BEGIN
                RAISE EXCEPTION 'append-only table % cannot be updated or deleted', TG_TABLE_NAME
                    USING ERRCODE = '42501';
            END;
            $peeronq$;

            CREATE TRIGGER "TR_AuditEvents_AppendOnly"
            BEFORE UPDATE OR DELETE ON "AuditEvents"
            FOR EACH ROW EXECUTE FUNCTION peeronq_reject_append_only_mutation();

            CREATE TRIGGER "TR_DiagnosticAccessEvents_AppendOnly"
            BEFORE UPDATE OR DELETE ON "DiagnosticAccessEvents"
            FOR EACH ROW EXECUTE FUNCTION peeronq_reject_append_only_mutation();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "Append-only audit enforcement is forward-only. Apply a reviewed compensating migration instead of weakening audit integrity.");
    }
}
