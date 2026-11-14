#!/usr/bin/env bash
# Prints strong random values for docker/.env.prod, one KEY=value per line (never written to a file: redirect or paste them yourself).
#
#   scripts/gen-secrets.sh                      # print
#   scripts/gen-secrets.sh >> docker/.env.prod  # append (then remove the blank KEY= lines they replace)
#
# Values are letters and digits only (base64 with + / = removed), so they are safe in a connection string and in a shell. The admin password
# hash is not random: make it from the founder's password with scripts/admin-hash.sh.
set -euo pipefail

command -v openssl >/dev/null 2>&1 || { echo "openssl is required" >&2; exit 1; }

# $1 = how many characters
random() {
    local length="$1" value=""
    while [ "${#value}" -lt "$length" ]; do
        value="$value$(openssl rand -base64 48 | tr -d '=+/\n')"
    done
    printf '%s' "${value:0:$length}"
}

echo "# Generated $(date -u +%Y-%m-%dT%H:%M:%SZ). Keep these secret."
echo "JWT_SIGNING_KEY=$(random 64)"
echo "INTERNAL_API_KEY=$(random 48)"
echo "VISION_INTERNAL_KEY=$(random 32)"
echo "OUTBOUND_IP_SALT=$(random 32)"
echo "POSTGRES_PASSWORD=$(random 32)"
echo "MINIO_ROOT_PASSWORD=$(random 32)"
echo "BACKUP_PASSPHRASE=$(random 48)"
