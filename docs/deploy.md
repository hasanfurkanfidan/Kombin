# Deploying Kombin on a VPS

One Ubuntu server runs the whole stack with Docker Compose: the API, PostgreSQL (pgvector), MinIO (photos), the vision service and Caddy, the
only thing the internet can reach. This is the step-by-step for a fresh server, the backup drill and the update procedure. A last section
describes the temporary variant for a closed beta, with the stack on a development PC behind a tunnel.

What the files do: [`docker/compose.yaml`](../docker/compose.yaml) is the stack; [`docker/compose.prod.yaml`](../docker/compose.prod.yaml)
is the production override (nothing published but 80 and 443, Production mode, required secrets, restarts, memory limits, volumes);
[`docker/caddy/Caddyfile`](../docker/caddy/Caddyfile) is the proxy; [`docker/.env.prod.example`](../docker/.env.prod.example) explains
every variable; `scripts/gen-secrets.sh`, `scripts/backup.sh` and `scripts/restore.sh` do what their names say.

## 0. Before the server

- **A domain** you control. You need two names pointing at the server's public address (A, and AAAA if it has IPv6): `api.<domain>` and
  `storage.<domain>`. Caddy gets the certificates from Let's Encrypt on the first start, which only works once both resolve.
- **A server** in an EU region (the privacy texts say so). 4 vCPU, **8 GB RAM** and 80 GB disk is comfortable: the vision service alone uses
  ~1.7 GB idle and ~2.9 GB at peak (its own README, "Memory"), the database and API a few hundred MB each. Ubuntu 24.04 LTS.
- **Your own address** for the admin panel: `/admin` answers only the addresses you list (`ADMIN_ALLOWED_IPS`). A home address that changes
  needs a VPN or Cloudflare Access instead (see the Caddyfile).
- **An S3-compatible bucket for backups** (Cloudflare R2 is cheap and has no egress fees), with its own access key, plus a machine of yours
  to keep the backup *private* key on (section 7).
- The two repositories: `kombin-backend` and `kombin-vision` **side by side** (compose builds the vision service from `../kombin-vision`).

## 1. The server: a user, SSH, firewall

Log in as root once (your provider's console or the first key), then:

```bash
adduser --disabled-password --gecos "" kombin
usermod -aG sudo kombin
mkdir -p /home/kombin/.ssh && cp ~/.ssh/authorized_keys /home/kombin/.ssh/ && chown -R kombin:kombin /home/kombin/.ssh && chmod 700 /home/kombin/.ssh
echo 'kombin ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/kombin   # or keep the password prompt: give the user a password with `passwd kombin`
```

From your own machine, check that `ssh kombin@<server>` works **before** closing the root session, then harden SSH (keys only, no root):

```bash
sudo tee /etc/ssh/sshd_config.d/99-kombin.conf >/dev/null <<'EOF'
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
PubkeyAuthentication yes
MaxAuthTries 3
X11Forwarding no
EOF
sudo sshd -t && sudo systemctl reload ssh
```

Firewall: only SSH, HTTP (Let's Encrypt and the redirect) and HTTPS. Docker publishes ports around ufw's rules, which is why the compose
override publishes nothing else: do not add `ports:` of your own.

```bash
sudo ufw default deny incoming && sudo ufw default allow outgoing
sudo ufw allow 22/tcp && sudo ufw allow 80/tcp && sudo ufw allow 443/tcp && sudo ufw allow 443/udp
sudo ufw enable && sudo ufw status verbose
sudo apt-get update && sudo apt-get install -y unattended-upgrades fail2ban
sudo dpkg-reconfigure -plow unattended-upgrades
```

## 2. Docker and the tools the scripts use

```bash
sudo apt-get install -y ca-certificates curl gnupg git openssl gzip age awscli
sudo install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg | sudo gpg --dearmor -o /etc/apt/keyrings/docker.gpg
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
sudo apt-get update && sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo usermod -aG docker kombin   # log out and in again
docker compose version           # 2.24 or newer: the override uses `!reset`
```

## 3. The code

```bash
sudo mkdir -p /srv/kombin && sudo chown kombin:kombin /srv/kombin && cd /srv/kombin
git clone <your remote>/kombin-backend.git
git clone <your remote>/kombin-vision.git
cd kombin-backend
```

## 4. Configuration

```bash
cp docker/.env.prod.example docker/.env.prod && chmod 600 docker/.env.prod
scripts/gen-secrets.sh        # prints JWT_SIGNING_KEY, INTERNAL_API_KEY, VISION_INTERNAL_KEY, OUTBOUND_IP_SALT, POSTGRES_PASSWORD, ...
scripts/admin-hash.sh         # asks for the founder's password (12+ characters) and prints ADMIN_PASSWORD_HASH
```

Paste the generated values into `docker/.env.prod` (replace the blank lines) and fill in the rest: `DOMAIN`, `ACME_EMAIL`,
`ADMIN_ALLOWED_IPS`, `STORAGE_PUBLIC_URL=https://storage.<domain>`, `ADMIN_EMAIL`, `ADMIN_PASSWORD_HASH`, `LEGAL_CONTROLLER_NAME`,
`LEGAL_CONTACT_EMAIL`, `LEGAL_ADDRESS`, `BOT_CONTACT` and the Google and Apple client IDs. Every variable is explained in the file. The API
**refuses to start** in Production with a missing value, a development default or a short secret, and prints every problem it found; compose
refuses to start when a required variable is missing at all.

A shortcut for every command below, so the long flags are typed once:

```bash
alias kc='docker compose --env-file docker/.env.prod --file docker/compose.yaml --file docker/compose.prod.yaml'
kc config --quiet && echo "configuration is complete"
```

## 5. First start

The API never migrates the database outside Development, and the runtime image has no `dotnet ef`; the image's own `migrate` command does it.

```bash
kc up --build --detach --wait db minio minio-init       # the data stores first
kc run --rm --no-deps api migrate                       # prints "Applied N migration(s)"
kc up --build --detach --wait                           # api, vision (a minute of model loading), caddy
kc ps
```

Check it from your own machine (replace the domain):

```bash
curl -s https://api.example.com/health                         # Healthy
curl -s https://api.example.com/api/v1/legal                   # { version, privacyUrl, termsUrl, deleteAccountUrl }
curl -s -o /dev/null -w '%{http_code}\n' https://api.example.com/openapi/v1.json   # 404: not exposed in Production
curl -s -o /dev/null -w '%{http_code}\n' https://api.example.com/hangfire          # 404
curl -s -o /dev/null -w '%{http_code}\n' https://api.example.com/admin/login       # 200 from an address in ADMIN_ALLOWED_IPS, 404 from any other
```

Open `https://api.example.com/legal/privacy` and read it: the controller details come from the three `LEGAL_*` variables. Sign in to
`/admin/users` once and check the audit page: it is how you know the admin allow-list and the data-protection keys work (`kc restart api`
must not sign you out).

The compose project is named `kombin`, so its volumes are `kombin_kombin-pgdata` (database), `kombin_kombin-minio-data` (photos),
`kombin_kombin-keys` (data-protection keys) and `kombin_kombin-caddy-data` (certificates). The database and the photo store are what
the backups cover; losing the keys only signs the founder out, and losing the certificates only costs a re-issue.

Workers (`kombin-workers`) that run on the same server join the compose network and call `http://api:8080/api/v1/internal/...` with the
internal key, so they never pass through Caddy. Workers on another machine must be listed in `INTERNAL_ALLOWED_IPS`.

## 6. Mobile app settings

Production builds of the app use `EXPO_PUBLIC_API_URL=https://api.<domain>`. The storage address the API puts into presigned upload URLs and image
URLs is `STORAGE_PUBLIC_URL`; the app needs no setting for it. The Google and Apple client IDs in `docker/.env.prod` must be the production ones.

## 7. Backups

The backup is `pg_dump` (custom format) → gzip → encrypted → your bucket, nightly, 7 days kept. It covers the **database**: accounts, posts, the
wardrobe rows. The photos in MinIO are not in it; they are replaceable by the user and covered by volume snapshots at your provider if you buy
them. (Using Cloudflare R2 as the object store instead of MinIO moves that problem to R2: see "Using R2 for photos" below.)

**Keys.** On **your own machine** (not the server) make an age key pair and keep the private key somewhere safe and off the server; without it a
backup cannot be read, and with it on the server an intruder could read them all:

```bash
age-keygen -o kombin-backup.key        # prints "Public key: age1..."
```

Put the public key in `docker/.env.prod` as `BACKUP_AGE_RECIPIENT=age1...`. (Without `age`, `BACKUP_PASSPHRASE` encrypts with openssl instead;
`gen-secrets.sh` prints one.) Add the bucket: `BACKUP_S3_BUCKET`, `BACKUP_S3_ENDPOINT` (R2: `https://<account>.r2.cloudflarestorage.com`),
`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_DEFAULT_REGION=auto`. Give the bucket a lifecycle rule that expires objects after 8 days as
a second guard; the script itself deletes dumps older than `BACKUP_RETENTION_DAYS` (7).

**Run it once by hand, then schedule it:**

```bash
scripts/backup.sh                     # "wrote backups/kombin-<time>.dump.gz.age ... uploaded to s3://..."
crontab -e                            # then add:
0 3 * * *  cd /srv/kombin/kombin-backend && scripts/backup.sh >> /var/log/kombin-backup.log 2>&1
```

(`sudo touch /var/log/kombin-backup.log && sudo chown kombin /var/log/kombin-backup.log` first.) A dump older than the retention is deleted
locally and in the bucket, so a deletion request is fully gone from backups within 7 days after the 7-day window: the privacy texts promise exactly that.

**The restore drill** (do it after setting backups up and every few months): restore into a scratch database next to the live one, compare the row
counts, drop it. Nothing touches the live database.

```bash
scripts/restore.sh backups/kombin-20261114T030000Z.dump.gz.age --compare     # BACKUP_AGE_IDENTITY=/path/to/kombin-backup.key
scripts/restore.sh s3://my-bucket/kombin/kombin-20261114T030000Z.dump.gz.age --compare
```

`--compare` counts the rows of every table in both databases (the Hangfire job tables excluded, they change constantly) and exits 2 with the
differing tables when they do not match; differences are normal only if the app wrote between the backup and the drill. Right after a backup on
a quiet server they match. `--keep` leaves the scratch database for you to inspect.

**Disaster recovery**: provision a server (sections 1 to 4), fetch the newest dump and your age private key, then bring up only the database, replace its empty
database with the restored one and start everything:

```bash
kc up --detach --wait db
kc exec -T db sh -c 'psql -U "$POSTGRES_USER" -d postgres -c "DROP DATABASE \"$POSTGRES_DB\""'
BACKUP_AGE_IDENTITY=/path/to/kombin-backup.key scripts/restore.sh s3://my-bucket/kombin/<newest>.dump.gz.age --database kombin --keep
kc up --build --detach --wait minio minio-init                # empty photo store: the photos are not in the dump
kc run --rm --no-deps api migrate && kc up --build --detach --wait
```

### Using R2 for photos instead of MinIO

Set `Storage__Endpoint` (`https://<account>.r2.cloudflarestorage.com`), `Storage__Bucket`, the access keys and a public domain on the bucket
(`Storage__PublicBaseUrl`, `Storage__PresignEndpoint`) in the API's environment, apply the same public-read rule for `*/cutout.*` and
`*/thumb.*` as `docker/minio/public-read.json` (R2 has no bucket policies: put a Worker or a custom domain with a rule in front that serves only
those names) and drop the `minio`, `minio-init` services and the `storage.` site of the Caddyfile. Not done by default because the public-read-by-name rule is the privacy guarantee
for private photos and has to be rebuilt carefully.

## 8. Updating

```bash
cd /srv/kombin/kombin-backend
scripts/backup.sh                                  # always: migrations cannot be undone by `git checkout`
git pull && git -C ../kombin-vision pull
kc build                                           # builds the new images while the old ones keep serving
kc run --rm --no-deps api migrate                  # applies new migrations (the old API keeps working on additive ones)
kc up --detach --wait                              # recreates what changed; Caddy keeps its certificates
curl -s https://api.example.com/health
```

Rollback: `git checkout <previous tag>` + `kc up --build -d`, plus a restore of the pre-update backup if a migration changed data that the old
version cannot read (migrations here are additive; check `src/Kombin.Api/Persistence/Migrations` in the diff). A new `LEGAL_VERSION` is how a
changed legal text is announced (`GET /api/v1/legal`).

## 9. Running it

- Logs: `kc logs -f api` (JSON files rotate at 5 x 10 MB per container). The API logs correlation IDs, never emails or tokens. Caddy's access log
  holds client addresses; it is on the same rotation (a privacy touchpoint: `docs/privacy-notes.md`).
- Disk: `docker system df`; `kombin_kombin-minio-data` and `kombin_kombin-pgdata` grow. Prune old images after updates: `docker image prune -f`.
- Certificates renew by themselves; `kc logs caddy | grep -i certificate`.
- Rotating a secret: edit `docker/.env.prod`, `kc up -d`. `JWT_SIGNING_KEY` signs everybody out; `OUTBOUND_IP_SALT` makes old click hashes
  incomparable with new ones; `INTERNAL_API_KEY` must be changed in the workers as well; `POSTGRES_PASSWORD` is only read when the data
  directory is first created, so changing it later means `ALTER USER` in the database first.
- The hourly jobs (account deletion, post purge, notifications) run inside the API container: `Hangfire__Enabled` stays true.
- Account deletion requests that cannot be done in the app (no access to the device) arrive by e-mail: `/admin/users` → find the user →
  **Delete now**. It is audited.

## 10. Temporary public access from the dev PC with a tunnel (closed beta)

Before the server exists, testers can use the stack that runs on the development PC: a tunnel gives it public HTTPS addresses. The stack stays the
development one (`docker/compose.yaml` and `docker/.env`, Development environment, seed data, no Caddy); only the addresses the phones use change.
The phone talks to two things: the API and the object storage (it uploads photos straight to MinIO with presigned URLs), so you need **two tunnels**.

**What changes**

| Where | Value | Set to |
|---|---|---|
| mobile app (`kombin-frontend`, EAS or dev build) | `EXPO_PUBLIC_API_URL` | the API tunnel's https address, e.g. `https://kombin-api.example.dev` |
| `docker/.env` | `STORAGE_PUBLIC_URL` | the storage tunnel's https address (no trailing slash); restart the API: `docker compose --file docker/compose.yaml up -d api` |
| `docker/.env` | `PROXY_TLS_TERMINATED` | `true` (the tunnel ends TLS and says so in `X-Forwarded-Proto`; the default `Proxy:KnownNetworks` covers the Docker gateway the tunnel agent connects from) |
| `docker/.env` | `LEGAL_PUBLIC_BASE_URL` | the API tunnel's address, so `GET /api/v1/legal` returns links that work from a phone |
| `docker/.env` | `ADMIN_EMAIL` / `ADMIN_PASSWORD_HASH` | **leave blank for the beta**: the tunnel makes `/admin` public to anyone who finds it (nobody can sign in with an empty list). Use the dev PC's `localhost:8081/admin`. |

**ngrok** (free plan: one static domain, so use it for the API and a second account or a paid domain for storage; or run both tunnels from one agent config):

```bash
ngrok http 8081 --url=kombin-api.ngrok.app         # the API (API_PORT in docker/.env)
ngrok http 9000 --url=kombin-files.ngrok.app       # MinIO's S3 port (MINIO_PORT)
```

**Cloudflare Tunnel** (stable names need a domain on Cloudflare; `cloudflared tunnel --url` without one gives a random `trycloudflare.com` address
that changes at every start, which changes `STORAGE_PUBLIC_URL` and invalidates the URLs of already uploaded images): in the Zero Trust dashboard
create one tunnel with two public hostnames, `kombin-api.<domain>` → `http://localhost:8081` and `kombin-files.<domain>` → `http://localhost:9000`,
and run `cloudflared tunnel run --token <token>`. Add an Access policy on `kombin-api.<domain>/admin*` if you ever enable the admin panel through it.

Things to know: the rate limits are off in the development stack (`RATE_LIMITS_ENABLED=false`), so a public beta has no brake; turn them on with
`RATE_LIMITS_ENABLED=true` (all testers behind one NAT then share a bucket only if the tunnel does not pass the real address; ngrok and Cloudflare do).
The dev stack's `/hangfire` dashboard is open, and the tunnel exposes it: set `HANGFIRE_ENABLED=false` for the beta if that matters more than
notifications, or keep the dashboard private by tunnelling to the Caddy-less stack only for the two paths you need (Cloudflare "path" rules).
The tunnel dies when the PC sleeps; use `scripts` / Docker "keep awake" settings during a test session.
