#!/usr/bin/env bash
# Backfills the Cache-Control header of the public images (cutout.*, thumb.*) that were stored in the development MinIO before the API set it on
# upload (task B8). New uploads carry it already; the production proxy (docker/caddy/Caddyfile) covers old objects there. Idempotent, and it only
# rewrites metadata (the bytes, the keys and the policy stay): `mc cp --attr` onto the same key.
#
#   scripts/minio-cache-control.sh [minio-container]      # default: kombin-minio-1 (docker compose --file docker/compose.yaml)
#
# Wardrobe images get a year, immutable (their key is final once the item is Ready); a league copy a day (it is the picture of one day).
# Private objects (originals, post images, try-on renders) are left alone: the API's presigned links carry `private, no-store` themselves.
set -euo pipefail

container="${1:-kombin-minio-1}"
user="${MINIO_ROOT_USER:-kombin}"
password="${MINIO_ROOT_PASSWORD:-kombin_dev_minio_password}"

# MSYS_NO_PATHCONV: Git Bash on Windows would otherwise rewrite the /tmp path inside the container.
MSYS_NO_PATHCONV=1 docker exec -e "MC_HOST_local=http://${user}:${password}@127.0.0.1:9000" "$container" sh -c '
set -eu
mc="mc --config-dir /tmp/mc"
long="public, max-age=31536000, immutable"
day="public, max-age=86400, immutable"
done=0
for key in $($mc ls --recursive local/kombin | awk "{print \$NF}" | grep -E "/(cutout|thumb)\.[A-Za-z0-9]+$"); do
  case "$key" in
    league/*) value="$day" ;;
    *) value="$long" ;;
  esac
  $mc cp --quiet --attr "Cache-Control=$value" "local/kombin/$key" "local/kombin/$key" >/dev/null
  done=$((done + 1))
done
echo "Cache-Control set on $done public images."
'
