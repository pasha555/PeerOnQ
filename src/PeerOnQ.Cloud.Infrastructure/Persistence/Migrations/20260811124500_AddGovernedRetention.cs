using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeerOnQ.Cloud.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGovernedRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RetentionBatchEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CutoffUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RequestedBatchSize = table.Column<int>(type: "integer", nullable: false),
                    DeletedCount = table.Column<int>(type: "integer", nullable: false),
                    OldestRecordAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NewestRecordAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BatchDigestSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExecutedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionBatchEvidence", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RetentionBatchEvidence_ExecutedAtUtc",
                table: "RetentionBatchEvidence",
                column: "ExecutedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RetentionBatchEvidence_RecordType_ExecutedAtUtc",
                table: "RetentionBatchEvidence",
                columns: new[] { "RecordType", "ExecutedAtUtc" });

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION peeronq_reject_append_only_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $peeronq$
                DECLARE
                    operation_context text;
                BEGIN
                    GET DIAGNOSTICS operation_context = PG_CONTEXT;
                    IF TG_TABLE_NAME = 'AuditEvents'
                       AND TG_OP = 'DELETE'
                       AND position('peeronq_apply_audit_retention' in operation_context) > 0 THEN
                        RETURN OLD;
                    END IF;
                    RAISE EXCEPTION 'append-only table % cannot be updated or deleted', TG_TABLE_NAME
                        USING ERRCODE = '42501';
                END;
                $peeronq$;

                CREATE TRIGGER "TR_RetentionBatchEvidence_AppendOnly"
                BEFORE UPDATE OR DELETE ON "RetentionBatchEvidence"
                FOR EACH ROW EXECUTE FUNCTION peeronq_reject_append_only_mutation();

                CREATE OR REPLACE FUNCTION peeronq_reject_individual_alert_delete()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $peeronq$
                DECLARE
                    operation_context text;
                BEGIN
                    GET DIAGNOSTICS operation_context = PG_CONTEXT;
                    IF position('peeronq_apply_alert_retention' in operation_context) > 0 THEN
                        RETURN OLD;
                    END IF;
                    RAISE EXCEPTION 'alert records can only be deleted by a governed retention batch'
                        USING ERRCODE = '42501';
                END;
                $peeronq$;

                CREATE TRIGGER "TR_AlertEvents_GovernedDelete"
                BEFORE DELETE ON "AlertEvents"
                FOR EACH ROW EXECUTE FUNCTION peeronq_reject_individual_alert_delete();

                CREATE OR REPLACE FUNCTION peeronq_apply_audit_retention(
                    p_evidence_id uuid,
                    p_policy_version text,
                    p_cutoff_utc timestamp with time zone,
                    p_batch_size integer,
                    p_executed_at_utc timestamp with time zone)
                RETURNS integer
                LANGUAGE plpgsql
                SECURITY INVOKER
                AS $peeronq$
                DECLARE
                    deleted_count integer;
                    oldest_record timestamp with time zone;
                    newest_record timestamp with time zone;
                    batch_digest text;
                BEGIN
                    IF p_evidence_id IS NULL
                       OR p_policy_version IS NULL
                       OR p_policy_version !~ '^[A-Za-z0-9._-]{1,64}$'
                       OR p_batch_size < 1 OR p_batch_size > 5000
                       OR p_cutoff_utc IS NULL OR p_executed_at_utc IS NULL
                       OR p_cutoff_utc > p_executed_at_utc - interval '365 days' THEN
                        RAISE EXCEPTION 'invalid audit retention policy' USING ERRCODE = '22023';
                    END IF;

                    WITH candidates AS MATERIALIZED (
                        SELECT "Id", "TimestampUtc"
                        FROM "AuditEvents"
                        WHERE "TimestampUtc" < p_cutoff_utc
                        ORDER BY "TimestampUtc", "Id"
                        FOR UPDATE SKIP LOCKED
                        LIMIT p_batch_size
                    ), deleted AS (
                        DELETE FROM "AuditEvents" AS target
                        USING candidates
                        WHERE target."Id" = candidates."Id"
                        RETURNING target."Id", target."TimestampUtc"
                    )
                    SELECT count(*)::integer,
                           min("TimestampUtc"),
                           max("TimestampUtc"),
                           coalesce(encode(sha256(convert_to(string_agg("Id"::text, ',' ORDER BY "Id"::text), 'UTF8')), 'hex'), '')
                    INTO deleted_count, oldest_record, newest_record, batch_digest
                    FROM deleted;

                    IF deleted_count > 0 THEN
                        INSERT INTO "RetentionBatchEvidence" (
                            "Id", "RecordType", "PolicyVersion", "CutoffUtc", "RequestedBatchSize",
                            "DeletedCount", "OldestRecordAtUtc", "NewestRecordAtUtc", "BatchDigestSha256", "ExecutedAtUtc")
                        VALUES (p_evidence_id, 'AuditEvents', p_policy_version, p_cutoff_utc, p_batch_size,
                            deleted_count, oldest_record, newest_record, batch_digest, p_executed_at_utc);
                    END IF;
                    RETURN deleted_count;
                END;
                $peeronq$;

                CREATE OR REPLACE FUNCTION peeronq_apply_alert_retention(
                    p_evidence_id uuid,
                    p_policy_version text,
                    p_cutoff_utc timestamp with time zone,
                    p_batch_size integer,
                    p_executed_at_utc timestamp with time zone)
                RETURNS integer
                LANGUAGE plpgsql
                SECURITY INVOKER
                AS $peeronq$
                DECLARE
                    deleted_count integer;
                    oldest_record timestamp with time zone;
                    newest_record timestamp with time zone;
                    batch_digest text;
                BEGIN
                    IF p_evidence_id IS NULL
                       OR p_policy_version IS NULL
                       OR p_policy_version !~ '^[A-Za-z0-9._-]{1,64}$'
                       OR p_batch_size < 1 OR p_batch_size > 5000
                       OR p_cutoff_utc IS NULL OR p_executed_at_utc IS NULL
                       OR p_cutoff_utc > p_executed_at_utc - interval '7 days' THEN
                        RAISE EXCEPTION 'invalid alert retention policy' USING ERRCODE = '22023';
                    END IF;

                    WITH candidates AS MATERIALIZED (
                        SELECT "Id", "ResolvedAtUtc"
                        FROM "AlertEvents"
                        WHERE "ResolvedAtUtc" IS NOT NULL AND "ResolvedAtUtc" < p_cutoff_utc
                        ORDER BY "ResolvedAtUtc", "Id"
                        FOR UPDATE SKIP LOCKED
                        LIMIT p_batch_size
                    ), deleted AS (
                        DELETE FROM "AlertEvents" AS target
                        USING candidates
                        WHERE target."Id" = candidates."Id"
                        RETURNING target."Id", target."ResolvedAtUtc"
                    )
                    SELECT count(*)::integer,
                           min("ResolvedAtUtc"),
                           max("ResolvedAtUtc"),
                           coalesce(encode(sha256(convert_to(string_agg("Id"::text, ',' ORDER BY "Id"::text), 'UTF8')), 'hex'), '')
                    INTO deleted_count, oldest_record, newest_record, batch_digest
                    FROM deleted;

                    IF deleted_count > 0 THEN
                        INSERT INTO "RetentionBatchEvidence" (
                            "Id", "RecordType", "PolicyVersion", "CutoffUtc", "RequestedBatchSize",
                            "DeletedCount", "OldestRecordAtUtc", "NewestRecordAtUtc", "BatchDigestSha256", "ExecutedAtUtc")
                        VALUES (p_evidence_id, 'AlertEvents', p_policy_version, p_cutoff_utc, p_batch_size,
                            deleted_count, oldest_record, newest_record, batch_digest, p_executed_at_utc);
                    END IF;
                    RETURN deleted_count;
                END;
                $peeronq$;

                REVOKE ALL ON FUNCTION peeronq_apply_audit_retention(uuid, text, timestamp with time zone, integer, timestamp with time zone) FROM PUBLIC;
                REVOKE ALL ON FUNCTION peeronq_apply_alert_retention(uuid, text, timestamp with time zone, integer, timestamp with time zone) FROM PUBLIC;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Governed retention is forward-only. Apply a reviewed compensating migration rather than removing immutable retention evidence.");
        }
    }
}
