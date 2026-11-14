#!/usr/bin/env bash
# Restores an encrypted backup of scripts/backup.sh into a SCRATCH database (never over the live one), and optionally compares row counts.
# The restore drill: do it once after setting backups up, and again now and then, so you know the backups work before you need them.
#
#   scripts/restore.sh backups/kombin-20261114T030000Z.dump.gz.enc --compare
#   scripts/restore.sh s3://my-bucket/kombin/kombin-20261114T030000Z.dump.gz.age --compare --keep
#
#   --compare   count the rows of every table in the live database and in the restored copy and report differences (the hangfire job tables are
#               left out: they change constantly). Differences are expected when the app wrote between the backup and now.
#   --keep      do not drop the scratch database afterwards (inspect it with psql; drop it yourself: DROP DATABASE "<name>")
#   --database  name of the scratch database (default kombin_restore_<time>)
#
# Decryption: BACKUP_PASSPHRASE for .enc files; for .age files BACKUP_AGE_IDENTITY = the path of the private key (age-keygen output), which
# lives off the server. S3 downloads need the aws CLI and BACKUP_S3_ENDPOINT / AWS_* like backup.sh. KOMBIN_COMPOSE as in backup.sh.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

die() { echo "restore: $*" >&2; exit 1; }
log() { echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) restore: $*"; }

SOURCE=""
COMPARE=0
KEEP=0
SCRATCH="kombin_restore_$(date -u +%Y%m%d%H%M%S)"
while [ "$#" -gt 0 ]; do
    case "$1" in
        --compare) COMPARE=1 ;;
        --keep) KEEP=1 ;;
        --database) shift; SCRATCH="${1:?--database needs a name}" ;;
        -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
        -*) die "unknown option $1" ;;
        *) [ -z "$SOURCE" ] || die "one backup file at a time"; SOURCE="$1" ;;
    esac
    shift
done
[ -n "$SOURCE" ] || die "usage: scripts/restore.sh <backup file | s3://bucket/key> [--compare] [--keep] [--database NAME]"
case "$SCRATCH" in *[!A-Za-z0-9_]*) die "the scratch database name may only contain letters, digits and _" ;; esac

if [ -z "${KOMBIN_COMPOSE:-}" ]; then
    if [ -f docker/.env.prod ]; then
        KOMBIN_COMPOSE="docker compose --env-file docker/.env.prod --file docker/compose.yaml --file docker/compose.prod.yaml"
    else
        KOMBIN_COMPOSE="docker compose --file docker/compose.yaml"
    fi
fi

WORK="$(mktemp -d)"
cleanup() {
    rm -rf "$WORK"
    if [ "$KEEP" = 0 ] && [ -n "${CREATED:-}" ]; then
        $KOMBIN_COMPOSE exec -T db sh -c 'psql --username "$POSTGRES_USER" --dbname postgres --quiet -c "DROP DATABASE IF EXISTS \"'"$SCRATCH"'\" WITH (FORCE)"' >/dev/null 2>&1 || true
        log "dropped the scratch database $SCRATCH"
    fi
}
trap cleanup EXIT

# --- fetch -----------------------------------------------------------------------------------------------------------------------
FILE="$SOURCE"
case "$SOURCE" in
    s3://*)
        command -v aws >/dev/null 2>&1 || die "the aws CLI is required for s3:// sources"
        FILE="$WORK/$(basename "$SOURCE")"
        if [ -n "${BACKUP_S3_ENDPOINT:-}" ]; then aws --endpoint-url "$BACKUP_S3_ENDPOINT" s3 cp "$SOURCE" "$FILE" --only-show-errors
        else aws s3 cp "$SOURCE" "$FILE" --only-show-errors; fi
        ;;
    *) [ -f "$FILE" ] || die "no such file: $FILE" ;;
esac

case "$FILE" in
    *.age)
        command -v age >/dev/null 2>&1 || die "age is required to decrypt a .age backup"
        [ -n "${BACKUP_AGE_IDENTITY:-}" ] || die "set BACKUP_AGE_IDENTITY to the path of the private key"
        decrypt() { age --decrypt --identity "$BACKUP_AGE_IDENTITY"; }
        ;;
    *.enc)
        [ -n "${BACKUP_PASSPHRASE:-}" ] || die "set BACKUP_PASSPHRASE"
        export BACKUP_PASSPHRASE
        decrypt() { openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -pass env:BACKUP_PASSPHRASE; }
        ;;
    *) die "the file name should end in .age or .enc (as backup.sh writes them)" ;;
esac

# --- restore into a scratch database ----------------------------------------------------------------------------------------------
log "creating the scratch database $SCRATCH"
$KOMBIN_COMPOSE exec -T db sh -c 'psql --username "$POSTGRES_USER" --dbname postgres --quiet -v ON_ERROR_STOP=1 -c "CREATE DATABASE \"'"$SCRATCH"'\""'
CREATED=1

log "restoring $(basename "$FILE")"
decrypt < "$FILE" | gunzip -c \
    | $KOMBIN_COMPOSE exec -T db sh -c 'exec pg_restore --username "$POSTGRES_USER" --dbname "'"$SCRATCH"'" --no-owner --no-privileges --exit-on-error'
log "restored"

# Exact row counts per table (count(*) through query_to_xml, so there is nothing to install), one "schema.table count" line each.
COUNT_SQL="SELECT table_schema || '.' || table_name || ' ' || (xpath('/row/c/text()', query_to_xml(format('SELECT count(*) AS c FROM %I.%I', table_schema, table_name), false, true, '')))[1]::text FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema', 'hangfire') ORDER BY 1"
counts() { $KOMBIN_COMPOSE exec -T db sh -c 'psql --username "$POSTGRES_USER" --dbname "$0" --no-align --tuples-only --command "$1"' "$1" "$COUNT_SQL"; }

RESTORED_TABLES="$(counts "$SCRATCH" | tr -d '\r' | wc -l | tr -d ' ')"
log "the restored copy has $RESTORED_TABLES tables"

if [ "$COMPARE" = 1 ]; then
    LIVE_DB="$($KOMBIN_COMPOSE exec -T db sh -c 'printf %s "$POSTGRES_DB"')"
    counts "$LIVE_DB" | tr -d '\r' > "$WORK/live.txt"
    counts "$SCRATCH" | tr -d '\r' > "$WORK/restored.txt"
    if diff -u "$WORK/live.txt" "$WORK/restored.txt" > "$WORK/diff.txt"; then
        log "row counts match in all $(wc -l < "$WORK/live.txt" | tr -d ' ') tables of $LIVE_DB"
    else
        echo "restore: row counts differ (live < > restored; expected only if the app wrote since the backup):"
        grep -E '^[+-][^+-]' "$WORK/diff.txt" || true
        exit 2
    fi
fi

[ "$KEEP" = 1 ] && log "kept the scratch database $SCRATCH (drop it when done: DROP DATABASE \"$SCRATCH\")"
exit 0
