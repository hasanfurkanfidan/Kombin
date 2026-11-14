#!/usr/bin/env bash
# Prints the password hash for Admin:Users (ADMIN_PASSWORD_HASH in docker/.env).
#   scripts/admin-hash.sh              asks for the password (no echo); or pipe it: printf '%s' "$PASSWORD" | scripts/admin-hash.sh
# The password must be at least 12 characters. It is never printed or stored.
set -euo pipefail
cd "$(dirname "$0")/.."
exec dotnet run --project src/Kombin.Api --verbosity quiet -- hash-password "$@"
