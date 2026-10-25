using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeerOnQ.Cloud.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudDbContext))]
[Migration("20260811130500_HardenGovernedRetentionExecution")]
public sealed class HardenGovernedRetentionExecution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DO $peeronq$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'peeronq_retention_executor') THEN
                    RAISE EXCEPTION 'required NOLOGIN role peeronq_retention_executor is not provisioned'
                        USING ERRCODE = '42704';
                END IF;
            END;
            $peeronq$;

            GRANT USAGE, CREATE ON SCHEMA public TO peeronq_retention_executor;
            GRANT SELECT, UPDATE, DELETE ON TABLE "AuditEvents", "AlertEvents" TO peeronq_retention_executor;
            GRANT INSERT ON TABLE "RetentionBatchEvidence" TO peeronq_retention_executor;

            CREATE OR REPLACE FUNCTION peeronq_apply_audit_retention(
                p_evidence_id uuid,
                p_policy_version text,
                p_cutoff_utc timestamp with time zone,
                p_batch_size integer,
                p_executed_at_utc timestamp with time zone)
            RETURNS integer
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, public
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
                    FROM public."AuditEvents"
                    WHERE "TimestampUtc" < p_cutoff_utc
                    ORDER BY "TimestampUtc", "Id"
                    FOR UPDATE SKIP LOCKED
                    LIMIT p_batch_size
                ), deleted AS (
                    DELETE FROM public."AuditEvents" AS target
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
                    INSERT INTO public."RetentionBatchEvidence" (
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
            SECURITY DEFINER
            SET search_path = pg_catalog, public
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
                    FROM public."AlertEvents"
                    WHERE "ResolvedAtUtc" IS NOT NULL AND "ResolvedAtUtc" < p_cutoff_utc
                    ORDER BY "ResolvedAtUtc", "Id"
                    FOR UPDATE SKIP LOCKED
                    LIMIT p_batch_size
                ), deleted AS (
                    DELETE FROM public."AlertEvents" AS target
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
                    INSERT INTO public."RetentionBatchEvidence" (
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
            ALTER FUNCTION peeronq_apply_audit_retention(uuid, text, timestamp with time zone, integer, timestamp with time zone)
                OWNER TO peeronq_retention_executor;
            ALTER FUNCTION peeronq_apply_alert_retention(uuid, text, timestamp with time zone, integer, timestamp with time zone)
                OWNER TO peeronq_retention_executor;
            REVOKE CREATE ON SCHEMA public FROM peeronq_retention_executor;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "PeerOnQ cloud migrations are forward-only. Restore a verified backup instead.");
}
