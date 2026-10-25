#!/bin/sh
set -eu

: "${POSTGRES_USER:?POSTGRES_USER is required}"
: "${POSTGRES_DB:?POSTGRES_DB is required}"
: "${PEERONQ_POSTGRES_MIGRATOR_PASSWORD:?PEERONQ_POSTGRES_MIGRATOR_PASSWORD is required}"
: "${PEERONQ_CLOUD_DB_PASSWORD:?PEERONQ_CLOUD_DB_PASSWORD is required}"
: "${PEERONQ_PRESENCE_DB_PASSWORD:?PEERONQ_PRESENCE_DB_PASSWORD is required}"
: "${PEERONQ_ADMIN_DB_PASSWORD:?PEERONQ_ADMIN_DB_PASSWORD is required}"
: "${PEERONQ_DOWNLOADS_DB_PASSWORD:?PEERONQ_DOWNLOADS_DB_PASSWORD is required}"
: "${PEERONQ_POSTGRES_BACKUP_PASSWORD:?PEERONQ_POSTGRES_BACKUP_PASSWORD is required}"
: "${PEERONQ_POSTGRES_RESTORE_PASSWORD:?PEERONQ_POSTGRES_RESTORE_PASSWORD is required}"

psql --set=ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  --set=database_name="$POSTGRES_DB" \
  --set=migrator_password="$PEERONQ_POSTGRES_MIGRATOR_PASSWORD" \
  --set=cloud_password="$PEERONQ_CLOUD_DB_PASSWORD" \
  --set=presence_password="$PEERONQ_PRESENCE_DB_PASSWORD" \
  --set=admin_password="$PEERONQ_ADMIN_DB_PASSWORD" \
  --set=downloads_password="$PEERONQ_DOWNLOADS_DB_PASSWORD" \
  --set=backup_password="$PEERONQ_POSTGRES_BACKUP_PASSWORD" \
  --set=restore_password="$PEERONQ_POSTGRES_RESTORE_PASSWORD" <<'EOSQL'
SELECT 'CREATE ROLE peeronq_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'peeronq_migrator') \gexec
SELECT 'CREATE ROLE peeronq_retention_executor NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'peeronq_retention_executor') \gexec
SELECT 'CREATE ROLE peeronq_cloud_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'peeronq_cloud_runtime') \gexec
SELECT 'CREATE ROLE peeronq_presence_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'peeronq_presence_runtime') \gexec
SELECT 'CREATE ROLE peeronq_admin_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'peeronq_admin_runtime') \gexec
SELECT 'CREATE ROLE peeronq_downloads_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'peeronq_downloads_runtime') \gexec
SELECT 'CREATE ROLE peeronq_backup LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'peeronq_backup') \gexec
SELECT 'CREATE ROLE peeronq_restore_operator LOGIN NOSUPERUSER CREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'peeronq_restore_operator') \gexec

ALTER ROLE peeronq_migrator PASSWORD :'migrator_password'; -- psql variable; secret-scan: allow-test-vector
ALTER ROLE peeronq_cloud_runtime PASSWORD :'cloud_password'; -- psql variable; secret-scan: allow-test-vector
ALTER ROLE peeronq_presence_runtime PASSWORD :'presence_password'; -- psql variable; secret-scan: allow-test-vector
ALTER ROLE peeronq_admin_runtime PASSWORD :'admin_password'; -- psql variable; secret-scan: allow-test-vector
ALTER ROLE peeronq_downloads_runtime PASSWORD :'downloads_password'; -- psql variable; secret-scan: allow-test-vector
ALTER ROLE peeronq_backup PASSWORD :'backup_password'; -- psql variable; secret-scan: allow-test-vector
ALTER ROLE peeronq_restore_operator PASSWORD :'restore_password'; -- psql variable; secret-scan: allow-test-vector

REVOKE peeronq_retention_executor FROM peeronq_cloud_runtime, peeronq_presence_runtime,
  peeronq_admin_runtime, peeronq_downloads_runtime, peeronq_backup;
GRANT peeronq_retention_executor TO peeronq_migrator;

REVOKE ALL ON DATABASE :"database_name" FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE :"database_name" TO peeronq_migrator;
GRANT CONNECT ON DATABASE :"database_name" TO peeronq_cloud_runtime, peeronq_presence_runtime,
  peeronq_admin_runtime, peeronq_downloads_runtime, peeronq_backup;

REVOKE ALL ON SCHEMA public FROM PUBLIC;
ALTER SCHEMA public OWNER TO peeronq_migrator;
GRANT USAGE, CREATE ON SCHEMA public TO peeronq_migrator;
GRANT USAGE ON SCHEMA public TO peeronq_cloud_runtime, peeronq_presence_runtime,
  peeronq_admin_runtime, peeronq_downloads_runtime, peeronq_backup;

ALTER DEFAULT PRIVILEGES FOR ROLE peeronq_migrator IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE peeronq_migrator IN SCHEMA public REVOKE ALL ON SEQUENCES FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE peeronq_migrator IN SCHEMA public REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
EOSQL
