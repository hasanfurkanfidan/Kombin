#!/usr/bin/env bash
# Encrypted PostgreSQL backup: pg_dump (custom format) -> gzip -> age or openssl -> an S3-compatible bucket (Cloudflare R2), keeping 7 days.
# Run it from cron on the server (docs/deploy.md) as the user that runs docker, from any directory:
#
#   0 3 * * *  /srv/kombin/scripts/backup.sh >> /var/log/kombin-backup.log 2>&1
#
# Settings come from the environment or, for what is not set there, from docker/.env.prod (see docker/.env.prod.example, "Backups"):
#   BACKUP_AGE_RECIPIENT | BACKUP_PASSPHRASE   how to encrypt (one is required: a dump is never written unencrypted)
#   BACKUP_S3_BUCKET, BACKUP_S3_ENDPOINT, BACKUP_S3_PREFIX, AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY, AWS_DEFAULT_REGION   where to put it
#   BACKUP_DIR (default ./backups), BACKUP_RETENTION_DAYS (default 7)
#   BACKUP_TARGET=local   keep the dump in BACKUP_DIR only (rehearsals); default: the bucket when BACKUP_S3_BUCKET is set, else local
#   KOMBIN_COMPOSE        the docker compose command (default: the production files with docker/.env.prod when it exists)
# Needs: docker compose, gzip, and age or openssl; the aws CLI for a bucket.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

die() { echo "backup: $*" >&2; exit 1; }
log() { echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) backup: $*"; }

# Reads KEY from docker/.env.prod when it is not in the environment (no sourcing: the file holds $ and + that a shell would mangle).
env_default() {
    local key="$1" value=""
    if [ -z "${!key:-}" ] && [ -f docker/.env.prod ]; then
        value="$(grep -E "^${key}=" docker/.env.prod | tail -n 1 | cut -d= -f2- | sed -E "s/^['\"]//; s/['\"]$//" || true)"
        [ -n "$value" ] && export "$key=$value"
    fi
    return 0
}
for key in BACKUP_AGE_RECIPIENT BACKUP_PASSPHRASE BACKUP_S3_BUCKET BACKUP_S3_ENDPOINT BACKUP_S3_PREFIX AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY \
           AWS_DEFAULT_REGION BACKUP_DIR BACKUP_RETENTION_DAYS; do
    env_default "$key"
done

if [ -z "${KOMBIN_COMPOSE:-}" ]; then
    if [ -f docker/.env.prod ]; then
        KOMBIN_COMPOSE="docker compose --env-file docker/.env.prod --file docker/compose.yaml --file docker/compose.prod.yaml"
    else
        KOMBIN_COMPOSE="docker compose --file docker/compose.yaml"
    fi
fi

BACKUP_DIR="${BACKUP_DIR:-./backups}"
RETENTION_DAYS="${BACKUP_RETENTION_DAYS:-7}"
PREFIX="${BACKUP_S3_PREFIX:-kombin/}"
case "$PREFIX" in */) ;; *) PREFIX="$PREFIX/" ;; esac
TARGET="${BACKUP_TARGET:-}"
if [ -z "$TARGET" ]; then
    if [ -n "${BACKUP_S3_BUCKET:-}" ]; then TARGET=s3; else TARGET=local; fi
fi

# --- encryption ------------------------------------------------------------------------------------------------------------------
if [ -n "${BACKUP_AGE_RECIPIENT:-}" ]; then
    command -v age >/dev/null 2>&1 || die "BACKUP_AGE_RECIPIENT is set but age is not installed (apt install age)"
    EXTENSION=age
    encrypt() { age --recipient "$BACKUP_AGE_RECIPIENT"; }
elif [ -n "${BACKUP_PASSPHRASE:-}" ]; then
    command -v openssl >/dev/null 2>&1 || die "openssl is required"
    EXTENSION=enc
    export BACKUP_PASSPHRASE
    encrypt() { openssl enc -aes-256-cbc -pbkdf2 -iter 600000 -salt -pass env:BACKUP_PASSPHRASE; }
else
    die "set BACKUP_AGE_RECIPIENT (age public key) or BACKUP_PASSPHRASE: a dump is never written unencrypted"
fi

[ "$TARGET" = s3 ] && { command -v aws >/dev/null 2>&1 || die "the aws CLI is required for an S3 target (apt install awscli)"; [ -n "${BACKUP_S3_BUCKET:-}" ] || die "BACKUP_S3_BUCKET is not set"; }

aws_s3() {
    if [ -n "${BACKUP_S3_ENDPOINT:-}" ]; then aws --endpoint-url "$BACKUP_S3_ENDPOINT" "$@"; else aws "$@"; fi
}

# --- dump ------------------------------------------------------------------------------------------------------------------------
mkdir -p "$BACKUP_DIR"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
NAME="kombin-${STAMP}.dump.gz.${EXTENSION}"
PARTIAL="$BACKUP_DIR/.partial-$NAME"
FINAL="$BACKUP_DIR/$NAME"
trap 'rm -f "$PARTIAL"' EXIT

log "dumping the database"
# --compress=0: gzip below does the compressing. pipefail makes a failing pg_dump fail the whole pipeline.
$KOMBIN_COMPOSE exec -T db sh -c 'exec pg_dump --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --format=custom --compress=0' \
    | gzip -c \
    | encrypt > "$PARTIAL"

SIZE="$(wc -c < "$PARTIAL" | tr -d ' ')"
[ "$SIZE" -gt 1024 ] || die "the dump is only $SIZE bytes: something went wrong"
mv "$PARTIAL" "$FINAL"
log "wrote $FINAL ($SIZE bytes, encrypted with ${EXTENSION})"

# --- upload and retention ---------------------------------------------------------------------------------------------------------
if [ "$TARGET" = s3 ]; then
    aws_s3 s3 cp "$FINAL" "s3://${BACKUP_S3_BUCKET}/${PREFIX}${NAME}" --only-show-errors
    log "uploaded to s3://${BACKUP_S3_BUCKET}/${PREFIX}${NAME}"

    CUTOFF="$(date -u -d "-${RETENTION_DAYS} days" +%Y-%m-%dT%H:%M:%S)"
    OLD="$(aws_s3 s3api list-objects-v2 --bucket "$BACKUP_S3_BUCKET" --prefix "$PREFIX" \
        --query "Contents[?LastModified<'${CUTOFF}'].Key" --output text 2>/dev/null || true)"
    for key in $OLD; do
        [ "$key" = None ] && continue
        case "$key" in *"kombin-"*".dump.gz."*) aws_s3 s3 rm "s3://${BACKUP_S3_BUCKET}/${key}" --only-show-errors; log "deleted old backup $key" ;; esac
    done

    # The staging copy is only a transit file once it is in the bucket; keep the last ${RETENTION_DAYS} days like the bucket does.
fi

find "$BACKUP_DIR" -maxdepth 1 -name 'kombin-*.dump.gz.*' -mtime "+${RETENTION_DAYS}" -print -delete | sed 's/^/deleted old local backup /' || true
log "done"
