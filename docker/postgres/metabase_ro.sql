-- Read-only PostgreSQL role for Metabase (operators' dashboards). Mounted into the db container's /docker-entrypoint-initdb.d, where
-- PostgreSQL runs it once, when the data directory is created. For a volume that already exists, run it by hand once:
--
--   docker compose --file docker/compose.yaml exec -T db sh -c 'psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB"' < docker/postgres/metabase_ro.sql
--
-- It is safe to run again. The password below is a development placeholder: the compose database listens on loopback only, but change it
-- (ALTER ROLE metabase_ro PASSWORD '...') before pointing Metabase at anything that holds real data.
--
-- What the role can read: the analytics, engagement and catalog schemas, and nothing else. identity (password hashes, refresh tokens),
-- notification (push tokens), profile (measurements) and hangfire stay out of reach. To widen it, add the schema to the list below.

DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'metabase_ro') THEN
        CREATE ROLE metabase_ro LOGIN PASSWORD 'metabase_ro_dev_password' NOSUPERUSER NOCREATEDB NOCREATEROLE;
    END IF;
END
$$;

-- Even a statement that asks for a read-write transaction is refused.
ALTER ROLE metabase_ro SET default_transaction_read_only = on;

DO $$
DECLARE
    schema_name text;
BEGIN
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO metabase_ro', current_database());

    FOREACH schema_name IN ARRAY ARRAY['analytics', 'engagement', 'catalog']
    LOOP
        -- On a fresh database the schemas do not exist yet (the API's migrations create them, and tolerate them being there already).
        EXECUTE format('CREATE SCHEMA IF NOT EXISTS %I', schema_name);
        EXECUTE format('GRANT USAGE ON SCHEMA %I TO metabase_ro', schema_name);
        EXECUTE format('GRANT SELECT ON ALL TABLES IN SCHEMA %I TO metabase_ro', schema_name);

        -- Tables the migrations create later, as the user running this script (the owner of the schemas).
        EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA %I GRANT SELECT ON TABLES TO metabase_ro', current_user, schema_name);
    END LOOP;
END
$$;
