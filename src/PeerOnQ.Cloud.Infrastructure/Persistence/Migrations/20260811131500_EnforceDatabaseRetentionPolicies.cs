using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeerOnQ.Cloud.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudDbContext))]
[Migration("20260811131500_EnforceDatabaseRetentionPolicies")]
public sealed class EnforceDatabaseRetentionPolicies : Migration
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

            CREATE TABLE public."RetentionPolicies" (
                "RecordType" text NOT NULL,
                "PolicyVersion" character varying(64) NOT NULL,
                "RetentionDays" integer NOT NULL,
                "Enabled" boolean NOT NULL,
                "LegalHold" boolean NOT NULL,
                "UpdatedAtUtc" timestamp with time zone NOT NULL,
                CONSTRAINT "PK_RetentionPolicies" PRIMARY KEY ("RecordType"),
                CONSTRAINT "CK_RetentionPolicies_RecordType" CHECK ("RecordType" IN ('AuditEvents', 'AlertEvents')),
                CONSTRAINT "CK_RetentionPolicies_Version" CHECK ("PolicyVersion" ~ '^[A-Za-z0-9._-]{1,64}$'),
                CONSTRAINT "CK_RetentionPolicies_Duration" CHECK (
                    ("RecordType" = 'AuditEvents' AND "RetentionDays" >= 365)
                    OR ("RecordType" = 'AlertEvents' AND "RetentionDays" >= 7))
            );

            INSERT INTO public."RetentionPolicies" (
                "RecordType", "PolicyVersion", "RetentionDays", "Enabled", "LegalHold", "UpdatedAtUtc")
            VALUES
                ('AuditEvents', 'phase6-v1', 365, false, false, clock_timestamp()),
                ('AlertEvents', 'phase6-v1', 365, true, false, clock_timestamp());

            REVOKE ALL ON TABLE public."RetentionPolicies" FROM PUBLIC;
            REVOKE ALL ON TABLE public."RetentionPolicies" FROM peeronq_retention_executor;
            GRANT SELECT ON TABLE public."RetentionPolicies" TO peeronq_retention_executor;
            GRANT SELECT, UPDATE, DELETE ON TABLE public."AuditEvents", public."AlertEvents"
                TO peeronq_retention_executor;
            GRANT INSERT ON TABLE public."RetentionBatchEvidence" TO peeronq_retention_executor;
            GRANT USAGE, CREATE ON SCHEMA public TO peeronq_retention_executor;

            -- The prior migration deliberately transferred function ownership to the NOLOGIN
            -- executor. Deployment migrators are NOINHERIT members, so replacement must make
            -- the ownership boundary explicit instead of relying on inherited privileges.
            SET ROLE peeronq_retention_executor;

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
                policy_version text;
                retention_days integer;
                policy_enabled boolean;
                policy_legal_hold boolean;
                executed_at_utc timestamp with time zone;
                cutoff_utc timestamp with time zone;
                deleted_count integer;
                oldest_record timestamp with time zone;
                newest_record timestamp with time zone;
                batch_digest text;
            BEGIN
                -- Compatibility parameters p_policy_version, p_cutoff_utc and p_executed_at_utc are
                -- intentionally untrusted. Policy, clock and cutoff are owned by the database.
                IF p_evidence_id IS NULL OR p_batch_size < 1 OR p_batch_size > 5000 THEN
                    RAISE EXCEPTION 'invalid audit retention request' USING ERRCODE = '22023';
                END IF;

                SELECT "PolicyVersion", "RetentionDays", "Enabled", "LegalHold"
                INTO policy_version, retention_days, policy_enabled, policy_legal_hold
                FROM public."RetentionPolicies"
                WHERE "RecordType" = 'AuditEvents';

                IF NOT FOUND OR retention_days < 365 THEN
                    RAISE EXCEPTION 'audit retention policy is missing or invalid' USING ERRCODE = '55000';
                END IF;
                IF NOT policy_enabled OR policy_legal_hold THEN
                    RETURN 0;
                END IF;

                executed_at_utc := clock_timestamp();
                cutoff_utc := executed_at_utc - (retention_days * interval '1 day');

                WITH candidates AS MATERIALIZED (
                    SELECT "Id", "TimestampUtc"
                    FROM public."AuditEvents"
                    WHERE "TimestampUtc" < cutoff_utc
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
                    VALUES (p_evidence_id, 'AuditEvents', policy_version, cutoff_utc, p_batch_size,
                        deleted_count, oldest_record, newest_record, batch_digest, executed_at_utc);
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
                policy_version text;
                retention_days integer;
                policy_enabled boolean;
                policy_legal_hold boolean;
                executed_at_utc timestamp with time zone;
                cutoff_utc timestamp with time zone;
                deleted_count integer;
                oldest_record timestamp with time zone;
                newest_record timestamp with time zone;
                batch_digest text;
            BEGIN
                -- Compatibility parameters p_policy_version, p_cutoff_utc and p_executed_at_utc are
                -- intentionally untrusted. Policy, clock and cutoff are owned by the database.
                IF p_evidence_id IS NULL OR p_batch_size < 1 OR p_batch_size > 5000 THEN
                    RAISE EXCEPTION 'invalid alert retention request' USING ERRCODE = '22023';
                END IF;

                SELECT "PolicyVersion", "RetentionDays", "Enabled", "LegalHold"
                INTO policy_version, retention_days, policy_enabled, policy_legal_hold
                FROM public."RetentionPolicies"
                WHERE "RecordType" = 'AlertEvents';

                IF NOT FOUND OR retention_days < 7 THEN
                    RAISE EXCEPTION 'alert retention policy is missing or invalid' USING ERRCODE = '55000';
                END IF;
                IF NOT policy_enabled OR policy_legal_hold THEN
                    RETURN 0;
                END IF;

                executed_at_utc := clock_timestamp();
                cutoff_utc := executed_at_utc - (retention_days * interval '1 day');

                WITH candidates AS MATERIALIZED (
                    SELECT "Id", "ResolvedAtUtc"
                    FROM public."AlertEvents"
                    WHERE "ResolvedAtUtc" IS NOT NULL AND "ResolvedAtUtc" < cutoff_utc
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
                    VALUES (p_evidence_id, 'AlertEvents', policy_version, cutoff_utc, p_batch_size,
                        deleted_count, oldest_record, newest_record, batch_digest, executed_at_utc);
                END IF;
                RETURN deleted_count;
            END;
            $peeronq$;

            REVOKE ALL ON FUNCTION peeronq_apply_audit_retention(uuid, text, timestamp with time zone, integer, timestamp with time zone) FROM PUBLIC;
            REVOKE ALL ON FUNCTION peeronq_apply_alert_retention(uuid, text, timestamp with time zone, integer, timestamp with time zone) FROM PUBLIC;
            RESET ROLE;
            REVOKE CREATE ON SCHEMA public FROM peeronq_retention_executor;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "PeerOnQ cloud migrations are forward-only. Restore a verified backup instead.");
}
