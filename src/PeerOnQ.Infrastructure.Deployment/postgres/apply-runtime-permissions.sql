\set ON_ERROR_STOP on

INSERT INTO public."RetentionPolicies" (
  "RecordType", "PolicyVersion", "RetentionDays", "Enabled", "LegalHold", "UpdatedAtUtc")
VALUES
  ('AuditEvents', :'retention_policy_version', :audit_retention_days,
    :audit_retention_enabled::boolean, :audit_legal_hold::boolean, clock_timestamp()),
  ('AlertEvents', :'retention_policy_version', :alert_retention_days,
    :alert_retention_enabled::boolean, :alert_legal_hold::boolean, clock_timestamp())
ON CONFLICT ("RecordType") DO UPDATE SET
  "PolicyVersion" = EXCLUDED."PolicyVersion",
  "RetentionDays" = EXCLUDED."RetentionDays",
  "Enabled" = EXCLUDED."Enabled",
  "LegalHold" = EXCLUDED."LegalHold",
  "UpdatedAtUtc" = clock_timestamp();

REVOKE ALL ON ALL TABLES IN SCHEMA public FROM
  peeronq_cloud_runtime, peeronq_presence_runtime, peeronq_admin_runtime,
  peeronq_downloads_runtime, peeronq_backup;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM
  peeronq_cloud_runtime, peeronq_presence_runtime, peeronq_admin_runtime,
  peeronq_downloads_runtime, peeronq_backup;
-- Revoke stale grants on functions owned by the migrator. The two retention functions are owned by
-- the isolated executor role and are handled under SET ROLE below.
DO $revoke$
DECLARE
  routine regprocedure;
BEGIN
  FOR routine IN
    SELECT function.oid::regprocedure
    FROM pg_proc AS function
    JOIN pg_namespace AS namespace ON namespace.oid = function.pronamespace
    WHERE namespace.nspname = 'public'
      AND function.proowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
  LOOP
    EXECUTE format(
      'REVOKE ALL ON FUNCTION %s FROM peeronq_cloud_runtime, peeronq_presence_runtime, peeronq_admin_runtime, peeronq_downloads_runtime, peeronq_backup',
      routine);
  END LOOP;
END
$revoke$;

-- Readiness compares the applied migration set without granting schema mutation rights.
GRANT SELECT ON TABLE "__EFMigrationsHistory" TO
  peeronq_cloud_runtime, peeronq_presence_runtime, peeronq_admin_runtime,
  peeronq_downloads_runtime;

GRANT SELECT, INSERT, UPDATE ON TABLE
  "Devices", "Installations", "RemoteSessions", "DiagnosticBundles"
TO peeronq_cloud_runtime;
GRANT SELECT, INSERT ON TABLE "SessionFailures", "UpdateEvents" TO peeronq_cloud_runtime;
GRANT SELECT ON TABLE "AppReleases" TO peeronq_cloud_runtime;
GRANT SELECT, DELETE ON TABLE
  "DevicePresenceHistory", "RemoteSessions", "ServiceHealthSnapshots", "AdminSessions"
TO peeronq_cloud_runtime;
GRANT SELECT, UPDATE ON TABLE "DownloadEvents" TO peeronq_cloud_runtime;
GRANT SELECT, INSERT, UPDATE ON TABLE
  "CustomerAccounts", "CustomerSessions", "CustomerAccountTokens", "CustomerRecoveryCodes",
  "Organizations", "OrganizationMemberships", "Teams", "TeamMemberships",
  "OrganizationInvitations", "OrganizationPolicies", "CustomerTrustedDevices", "AccountDataRequests"
TO peeronq_cloud_runtime;
GRANT DELETE ON TABLE "CustomerRecoveryCodes", "TeamMemberships" TO peeronq_cloud_runtime;
GRANT SELECT, INSERT ON TABLE "CustomerSecurityEvents" TO peeronq_cloud_runtime;
-- Retention execution is fail-closed. Re-running this controller after any policy/hold change
-- ensures a disabled or legally held class cannot be deleted by a compromised Cloud runtime.
SET ROLE peeronq_retention_executor;
REVOKE EXECUTE ON FUNCTION peeronq_apply_audit_retention(
  uuid, text, timestamp with time zone, integer, timestamp with time zone) FROM peeronq_cloud_runtime;
REVOKE EXECUTE ON FUNCTION peeronq_apply_alert_retention(
  uuid, text, timestamp with time zone, integer, timestamp with time zone) FROM peeronq_cloud_runtime;
\if :audit_retention_enabled
  \if :audit_legal_hold
  \else
    GRANT EXECUTE ON FUNCTION peeronq_apply_audit_retention(
      uuid, text, timestamp with time zone, integer, timestamp with time zone) TO peeronq_cloud_runtime;
  \endif
\endif
\if :alert_retention_enabled
  \if :alert_legal_hold
  \else
    GRANT EXECUTE ON FUNCTION peeronq_apply_alert_retention(
      uuid, text, timestamp with time zone, integer, timestamp with time zone) TO peeronq_cloud_runtime;
  \endif
\endif
RESET ROLE;

GRANT SELECT ON TABLE "Devices", "Installations" TO peeronq_presence_runtime;
GRANT INSERT ON TABLE "DevicePresenceHistory" TO peeronq_presence_runtime;

GRANT SELECT ON TABLE
  "Devices", "Installations", "DevicePresenceHistory", "RemoteSessions", "SessionFailures",
  "DownloadEvents", "AppReleases", "UpdateEvents", "DiagnosticBundles", "DiagnosticAccessEvents",
  "AdminUsers", "AdminRoles", "AdminUserRoles", "AdminRecoveryCodes", "AdminSessions",
  "AuditEvents", "InfrastructureRegions", "ServiceHealthSnapshots", "AlertEvents"
TO peeronq_admin_runtime;
GRANT INSERT, UPDATE ON TABLE "AdminUsers", "AdminRoles", "AdminSessions", "AlertEvents"
TO peeronq_admin_runtime;
GRANT INSERT ON TABLE "InfrastructureRegions", "ServiceHealthSnapshots"
TO peeronq_admin_runtime;
GRANT UPDATE ON TABLE "Devices", "Installations", "AppReleases" TO peeronq_admin_runtime;
GRANT INSERT ON TABLE "AdminUserRoles", "DiagnosticAccessEvents", "AuditEvents"
TO peeronq_admin_runtime;
GRANT INSERT, UPDATE ON TABLE "AdminRecoveryCodes" TO peeronq_admin_runtime;

GRANT SELECT ON TABLE "AppReleases", "DownloadEvents" TO peeronq_downloads_runtime;
GRANT INSERT, UPDATE ON TABLE "DownloadEvents" TO peeronq_downloads_runtime;

GRANT SELECT ON ALL TABLES IN SCHEMA public TO peeronq_backup;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO peeronq_backup;

DO $verify$
DECLARE
  runtime_role text;
BEGIN
  FOREACH runtime_role IN ARRAY ARRAY[
    'peeronq_cloud_runtime', 'peeronq_presence_runtime', 'peeronq_admin_runtime',
    'peeronq_downloads_runtime', 'peeronq_backup'
  ] LOOP
    IF EXISTS (
      SELECT FROM pg_roles
      WHERE rolname = runtime_role
        AND (rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls)
    ) THEN
      RAISE EXCEPTION 'unsafe attributes detected for role %', runtime_role;
    END IF;
    IF EXISTS (
      SELECT FROM pg_class AS relation
      JOIN pg_namespace AS namespace ON namespace.oid = relation.relnamespace
      JOIN pg_roles AS owner_role ON owner_role.oid = relation.relowner
      WHERE namespace.nspname = 'public'
        AND relation.relkind IN ('r', 'p', 'S', 'v', 'm')
        AND owner_role.rolname = runtime_role
    ) THEN
      RAISE EXCEPTION 'runtime role % owns a public relation', runtime_role;
    END IF;
    IF runtime_role <> 'peeronq_backup' AND (
       has_table_privilege(runtime_role, '"RetentionPolicies"', 'SELECT')
       OR has_table_privilege(runtime_role, '"RetentionPolicies"', 'INSERT')
       OR has_table_privilege(runtime_role, '"RetentionPolicies"', 'UPDATE')
       OR has_table_privilege(runtime_role, '"RetentionPolicies"', 'DELETE')) THEN
      RAISE EXCEPTION 'runtime role % can access retention policy state', runtime_role;
    END IF;
  END LOOP;

  IF EXISTS (
    SELECT FROM pg_roles
    WHERE rolname = 'peeronq_retention_executor'
      AND (rolcanlogin OR rolsuper OR rolcreaterole OR rolcreatedb OR rolreplication OR rolbypassrls)
  ) OR NOT pg_has_role('peeronq_migrator', 'peeronq_retention_executor', 'MEMBER')
    OR pg_has_role('peeronq_cloud_runtime', 'peeronq_retention_executor', 'MEMBER') THEN
    RAISE EXCEPTION 'unsafe governed-retention role configuration';
  END IF;

  IF NOT has_table_privilege('peeronq_retention_executor', '"AuditEvents"', 'SELECT')
     OR NOT has_table_privilege('peeronq_retention_executor', '"AuditEvents"', 'UPDATE')
     OR NOT has_table_privilege('peeronq_retention_executor', '"AuditEvents"', 'DELETE')
     OR NOT has_table_privilege('peeronq_retention_executor', '"AlertEvents"', 'SELECT')
     OR NOT has_table_privilege('peeronq_retention_executor', '"AlertEvents"', 'UPDATE')
     OR NOT has_table_privilege('peeronq_retention_executor', '"AlertEvents"', 'DELETE')
     OR NOT has_table_privilege('peeronq_retention_executor', '"RetentionBatchEvidence"', 'INSERT')
     OR NOT has_table_privilege('peeronq_retention_executor', '"RetentionPolicies"', 'SELECT')
     OR has_schema_privilege('peeronq_retention_executor', 'public', 'CREATE') THEN
    RAISE EXCEPTION 'governed-retention owner privileges are incomplete';
  END IF;

  IF EXISTS (
    SELECT FROM pg_class AS relation
    JOIN pg_namespace AS namespace ON namespace.oid = relation.relnamespace
    JOIN pg_roles AS owner_role ON owner_role.oid = relation.relowner
    WHERE namespace.nspname = 'public'
      AND relation.relkind IN ('r', 'p', 'S', 'v', 'm')
      AND owner_role.rolname <> 'peeronq_migrator'
  ) THEN
    RAISE EXCEPTION 'public relations must be owned by peeronq_migrator';
  END IF;

  IF has_table_privilege('peeronq_admin_runtime', '"AuditEvents"', 'UPDATE')
     OR has_table_privilege('peeronq_admin_runtime', '"AuditEvents"', 'DELETE')
     OR has_table_privilege('peeronq_admin_runtime', '"InfrastructureRegions"', 'UPDATE')
     OR has_table_privilege('peeronq_admin_runtime', '"InfrastructureRegions"', 'DELETE')
     OR has_table_privilege('peeronq_admin_runtime', '"ServiceHealthSnapshots"', 'UPDATE')
     OR has_table_privilege('peeronq_admin_runtime', '"ServiceHealthSnapshots"', 'DELETE')
     OR has_table_privilege('peeronq_presence_runtime', '"AdminUsers"', 'SELECT')
     OR has_table_privilege('peeronq_downloads_runtime', '"AdminUsers"', 'SELECT')
     OR has_table_privilege('peeronq_cloud_runtime', '"AuditEvents"', 'SELECT')
     OR has_table_privilege('peeronq_cloud_runtime', '"AuditEvents"', 'DELETE')
     OR has_table_privilege('peeronq_cloud_runtime', '"AlertEvents"', 'UPDATE')
     OR has_table_privilege('peeronq_cloud_runtime', '"RetentionBatchEvidence"', 'INSERT')
     OR has_table_privilege('peeronq_cloud_runtime', '"CustomerSecurityEvents"', 'UPDATE')
     OR has_table_privilege('peeronq_cloud_runtime', '"CustomerSecurityEvents"', 'DELETE')
     OR has_table_privilege('peeronq_admin_runtime', '"CustomerAccounts"', 'SELECT')
     OR has_function_privilege(
       'peeronq_admin_runtime',
       'peeronq_apply_audit_retention(uuid,text,timestamp with time zone,integer,timestamp with time zone)',
       'EXECUTE') THEN
    RAISE EXCEPTION 'cross-service privilege boundary verification failed';
  END IF;

  IF NOT has_table_privilege('peeronq_presence_runtime', '"Devices"', 'SELECT')
     OR NOT has_table_privilege('peeronq_presence_runtime', '"Installations"', 'SELECT')
     OR NOT has_table_privilege('peeronq_admin_runtime', '"InfrastructureRegions"', 'INSERT')
     OR NOT has_table_privilege('peeronq_admin_runtime', '"ServiceHealthSnapshots"', 'INSERT')
     OR NOT has_table_privilege('peeronq_cloud_runtime', '"CustomerAccounts"', 'SELECT')
     OR NOT has_table_privilege('peeronq_cloud_runtime', '"CustomerAccounts"', 'INSERT')
     OR NOT has_table_privilege('peeronq_cloud_runtime', '"CustomerSecurityEvents"', 'INSERT') THEN
    RAISE EXCEPTION 'required runtime privilege verification failed';
  END IF;

  IF EXISTS (
    SELECT 1
    FROM pg_proc AS function
    JOIN pg_namespace AS namespace ON namespace.oid = function.pronamespace
    JOIN pg_roles AS owner_role ON owner_role.oid = function.proowner
    WHERE namespace.nspname = 'public'
      AND function.proname IN ('peeronq_apply_audit_retention', 'peeronq_apply_alert_retention')
      AND (owner_role.rolname <> 'peeronq_retention_executor'
        OR NOT function.prosecdef
        OR NOT coalesce(function.proconfig, ARRAY[]::text[]) @> ARRAY['search_path=pg_catalog, public'])
  ) OR (
    SELECT count(*)
    FROM pg_proc AS function
    JOIN pg_namespace AS namespace ON namespace.oid = function.pronamespace
    WHERE namespace.nspname = 'public'
      AND function.proname IN ('peeronq_apply_audit_retention', 'peeronq_apply_alert_retention')
  ) <> 2 THEN
    RAISE EXCEPTION 'governed-retention function hardening verification failed';
  END IF;
END
$verify$;

SELECT count(*) = 2 AND bool_and(
  ("RecordType" = 'AuditEvents'
    AND "PolicyVersion" = :'retention_policy_version'
    AND "RetentionDays" = :audit_retention_days
    AND "Enabled" = :audit_retention_enabled::boolean
    AND "LegalHold" = :audit_legal_hold::boolean)
  OR
  ("RecordType" = 'AlertEvents'
    AND "PolicyVersion" = :'retention_policy_version'
    AND "RetentionDays" = :alert_retention_days
    AND "Enabled" = :alert_retention_enabled::boolean
    AND "LegalHold" = :alert_legal_hold::boolean)
) AS retention_policy_matches
FROM public."RetentionPolicies"
\gset
\if :retention_policy_matches
\else
  \echo 'database retention policy does not match deployment policy'
  \quit 3
\endif

\if :audit_retention_enabled
  \if :audit_legal_hold
    DO $policy$ BEGIN
      IF has_function_privilege('peeronq_cloud_runtime',
        'peeronq_apply_audit_retention(uuid,text,timestamp with time zone,integer,timestamp with time zone)',
        'EXECUTE') THEN RAISE EXCEPTION 'audit retention execute must be revoked under legal hold'; END IF;
    END $policy$;
  \else
    DO $policy$ BEGIN
      IF NOT has_function_privilege('peeronq_cloud_runtime',
        'peeronq_apply_audit_retention(uuid,text,timestamp with time zone,integer,timestamp with time zone)',
        'EXECUTE') THEN RAISE EXCEPTION 'enabled audit retention execute is missing'; END IF;
    END $policy$;
  \endif
\else
  DO $policy$ BEGIN
    IF has_function_privilege('peeronq_cloud_runtime',
      'peeronq_apply_audit_retention(uuid,text,timestamp with time zone,integer,timestamp with time zone)',
      'EXECUTE') THEN RAISE EXCEPTION 'disabled audit retention execute must be revoked'; END IF;
  END $policy$;
\endif

\if :alert_retention_enabled
  \if :alert_legal_hold
    DO $policy$ BEGIN
      IF has_function_privilege('peeronq_cloud_runtime',
        'peeronq_apply_alert_retention(uuid,text,timestamp with time zone,integer,timestamp with time zone)',
        'EXECUTE') THEN RAISE EXCEPTION 'alert retention execute must be revoked under legal hold'; END IF;
    END $policy$;
  \else
    DO $policy$ BEGIN
      IF NOT has_function_privilege('peeronq_cloud_runtime',
        'peeronq_apply_alert_retention(uuid,text,timestamp with time zone,integer,timestamp with time zone)',
        'EXECUTE') THEN RAISE EXCEPTION 'enabled alert retention execute is missing'; END IF;
    END $policy$;
  \endif
\else
  DO $policy$ BEGIN
    IF has_function_privilege('peeronq_cloud_runtime',
      'peeronq_apply_alert_retention(uuid,text,timestamp with time zone,integer,timestamp with time zone)',
      'EXECUTE') THEN RAISE EXCEPTION 'disabled alert retention execute must be revoked'; END IF;
  END $policy$;
\endif
