#!/usr/bin/env bash
# Sets the Cache-Control header of the public images (cutout.*, thumb.*) in the development MinIO to what the API stores today, `public,
# max-age=86400` (task B9; task B8 had written a year, immutable, for the wardrobe: run this once to replace it). New uploads carry it already; the
# production proxy (docker/caddy/Caddyfile) covers old objects there. Idempotent, and it only rewrites metadata (the bytes, the keys and the
# policy stay): `mc cp --attr` onto the same key.
#
#   scripts/minio-cache-control.sh [minio-container]      # default: kombin-minio-1 (docker compose --file docker/compose.yaml)
#
# Every public image gets one day, never immutable and never longer: a deleted account's or item's photo must leave every cache within a day.
# Private objects (originals, post images, try-on renders) are left alone: the API's presigned links carry `private, no-store` themselves.
set -euo pipefail

container="${1:-kombin-minio-1}"
user="${MINIO_ROOT_USER:-kombin}"
password="${MINIO_ROOT_PASSWORD:-kombin_dev_minio_password}"

# MSYS_NO_PATHCONV: Git Bash on Windows would otherwise rewrite the /tmp path inside the container.
MSYS_NO_PATHCONV=1 docker exec -e "MC_HOST_local=http://${user}:${password}@127.0.0.1:9000" "$container" sh -c '
set -eu
mc="mc --config-dir /tmp/mc"
value="public, max-age=86400"
done=0
for key in $($mc ls --recursive local/kombin | awk "{print \$NF}" | grep -E "/(cutout|thumb)\.[A-Za-z0-9]+$"); do
  $mc cp --quiet --attr "Cache-Control=$value" "local/kombin/$key" "local/kombin/$key" >/dev/null
  done=$((done + 1))
done
echo "Cache-Control set on $done public images."
'
