# Kombin backend

Backend for Kombin, a mobile app that learns a user's style, manages their wardrobe, suggests outfits, compares prices
across Turkish retailers and redirects to retailers through affiliate links. .NET 10 Minimal API, vertical slices,
PostgreSQL 18 via EF Core. See [CLAUDE.md](CLAUDE.md) for the architecture rules and the recipe for adding a use case.

Currently implemented: **Identity** (register, login, refresh with rotation, logout, current user).

## Requirements

- .NET SDK 10.0.401 (pinned in `global.json`)
- Docker (local PostgreSQL, integration tests)

## Run

```bash
cp docker/.env.example docker/.env      # then set JWT_SIGNING_KEY, e.g. `openssl rand -base64 48`
docker compose --file docker/compose.yaml up --build --detach --wait
curl -i http://localhost:8080/health
```

The compose stack runs the API in `Development` with `Database__MigrateOnStartup=true`, so the schema is created on first
start. OpenAPI document (Development only): `http://localhost:8080/openapi/v1.json`.

To run the API from the IDE or CLI against the compose database only:

```bash
docker compose --file docker/compose.yaml up --detach --wait db
dotnet run --project src/Kombin.Api
```

## Test

```bash
dotnet test --solution Kombin.slnx --configuration Release
```

Integration tests start their own PostgreSQL container with Testcontainers, so Docker must be running.

## API (all under `/api/v1`)

| Method | Route | Auth | Success | Errors |
|---|---|---|---|---|
| POST | `/auth/register` | - | 201 `{ accessToken, refreshToken, expiresIn }` | 400 validation, 409 `Identity.EmailTaken` |
| POST | `/auth/login` | - | 200 same shape | 400, 401 `Identity.InvalidCredentials` |
| POST | `/auth/refresh` | - | 200 same shape; old token revoked | 401 `Identity.InvalidRefreshToken` |
| POST | `/auth/logout` | Bearer | 204 | 401 |
| GET | `/me` | Bearer | 200 `{ id, email, displayName, createdAt }` | 401 |

Health: `/health/live` (process only), `/health` (also checks PostgreSQL).

Errors are `application/problem+json` with `errorCode` and `correlationId`; validation errors add an `errors` dictionary.
Send `X-Correlation-ID` (`^[A-Za-z0-9\-_.:]{1,64}$`) to choose your own correlation ID; otherwise one is generated. It is
always echoed in the response header.

## Decisions worth knowing

- **Password hashing:** `PasswordHasher<T>` from the framework (`Microsoft.AspNetCore.Identity`, PBKDF2-HMAC-SHA512,
  100k iterations, auto-rehash on login). No Identity tables, no `UserManager`.
- **Email:** stored trimmed and lower-cased with a unique index (no `citext` extension needed).
- **Refresh tokens:** 256-bit opaque random values, only their SHA-256 is stored. Rotation links the old token to its
  successor through `ReplacedByTokenId`. Presenting an already-revoked token revokes **all** active refresh tokens of
  that user (the whole account is signed out everywhere). Parallel refreshes of the same token are serialized by an
  optimistic concurrency token on PostgreSQL's `xmin`, so exactly one wins.
- **Logout** revokes only the caller's own token and is idempotent (an unknown token still returns 204).
- **Logging:** Serilog to the console, with the correlation ID on every event. `GlobalExceptionHandler` is the single place
  that logs an unhandled exception (once, at Error), so EF Core's own command/query/update error categories are muted in
  `appsettings.json`; otherwise one failing query would produce several Error lines.
- **Migrations** are never applied automatically outside `Development`; use `dotnet ef database update`.

## TODO

- Rate limiting on the auth endpoints (out of scope for the foundation task).
