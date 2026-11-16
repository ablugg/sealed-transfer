# Sealed Transfer

Send a file through a link. The file is encrypted in the sender's browser before upload and decrypted in
the recipient's browser after download, so the server only ever stores ciphertext.

- **Front end:** Angular 22, TypeScript, signals, Signal Forms, Vitest
- **API:** ASP.NET Core 10 (minimal APIs), C#
- **Data:** PostgreSQL through EF Core 10; encrypted chunks on local disk
- **Crypto:** WebCrypto only: HKDF-SHA-256 and AES-256-GCM in a chunked STREAM construction
- **CI:** GitHub Actions — build, test, CodeQL SAST, Trivy container scan, CycloneDX SBOMs

"Sealed Transfer" is a working name.

## What works today

Link-mode sharing, end to end:

1. Pick a file, an expiry (1 hour, 1 day or 7 days) and an optional download limit.
2. The browser validates the file (magic-byte and extension checks), encrypts it in 1 MiB chunks and uploads.
3. You get a link of the form `/t/{id}#{key}`. The key after `#` is never sent to the server.
4. The recipient opens the link. The key is removed from the address bar, the file name and size are
   decrypted and shown, and one click downloads and decrypts the file.

On the server: expiry and download limits, owner tokens for upload and revoke, an audit trail, a background
job that deletes expired transfers, rate limiting on transfer creation, strict CSP and SRI on all pages.

## How it works

```
file secret (32 random bytes, lives in the share link)
  └─ HKDF-SHA-256 ─┬─ content key  → AES-256-GCM over each 1 MiB chunk
                   └─ metadata key → AES-256-GCM over { name, type, size }

chunk nonce (12 bytes) = random prefix (7) | chunk index (4) | last-chunk flag (1)
```

Because the index and the last-chunk flag are part of each nonce, a chunk that is moved, repeated or dropped
fails authentication. The details are in [`web/src/app/crypto/file-crypto.ts`](web/src/app/crypto/file-crypto.ts),
and the limits of the design are in [`docs/threat-model.md`](docs/threat-model.md).

| Project | Role |
| --- | --- |
| `src/SealedTransfer.Core` | Domain model and rules. No dependencies, fully unit-tested. |
| `src/SealedTransfer.Infrastructure` | EF Core for PostgreSQL, and the file-system blob store. |
| `src/SealedTransfer.Api` | HTTP endpoints, download tokens, cleanup job, rate limiting. |
| `tests/SealedTransfer.Tests` | xUnit tests for the domain model and the transfer service. |
| `tests/SealedTransfer.IntegrationTests` | API integration tests (WebApplicationFactory + InMemory EF). |
| `web` | The Angular app, including Playwright e2e tests. |

### API

| Method and path | Purpose | Auth |
| --- | --- | --- |
| `POST /api/transfers` | Create a transfer; returns its id and an owner token | None (rate-limited) |
| `PUT /api/transfers/{id}/chunks/{index}` | Upload one encrypted chunk, in order | `X-Owner-Token` |
| `POST /api/transfers/{id}/complete` | Mark the upload finished | `X-Owner-Token` |
| `GET /api/transfers/{id}` | Public details needed to decrypt | None |
| `POST /api/transfers/{id}/downloads` | Count a download; returns a 15-minute download token | None |
| `GET /api/transfers/{id}/chunks/{index}` | Fetch one encrypted chunk | `X-Download-Token` |
| `DELETE /api/transfers/{id}` | Revoke and delete the ciphertext | `X-Owner-Token` |

## Run it locally

You need the .NET 10 SDK (preview), Node 22 and Docker.

**1. Start PostgreSQL**

```bash
docker compose up -d
```

The default password is in `docker-compose.yml`. Override it with `POSTGRES_PASSWORD` in a `.env` file.

**2. Give the API its connection string.** It is kept in user secrets, outside the repository.

```bash
cd src/SealedTransfer.Api
dotnet user-secrets set "ConnectionStrings:SealedTransfer" \
  "Host=localhost;Port=5432;Database=sealedtransfer;Username=sealed;Password=SealedTr4nsfer_2026"
cd ../..
```

**3. Run the API** on http://localhost:5181. Migrations run automatically on startup.

```bash
dotnet run --project src/SealedTransfer.Api
```

**4. Run the web app** on http://localhost:4200. The dev server proxies `/api` to the API.

```bash
cd web
npm install
npm start
```

## Tests

```bash
# .NET unit + integration tests
dotnet test

# Angular / crypto unit tests (Vitest)
cd web && npx vitest run

# Playwright e2e (requires API + Angular running, or use the auto-start config)
cd web && npx playwright test
```

## Roadmap

| # | Milestone | Status |
| --- | --- | --- |
| 1 | Project structure and link-mode sharing | Done |
| 2 | Run locally: PostgreSQL in Docker, EF Core migration | Done |
| 3 | Revoke button, "my transfers" list, password mode | Done |
| 4 | Upload checks (magic bytes, size) and page lockdown (CSP, SRI) | Done |
| 5 | API integration tests and Playwright end-to-end tests | Done |
| 6 | Auth0 sign-in, organizations, roles | Next |
| 7 | Audit log search and admin dashboard | |
| 8 | Railway deployment | Done |
| 9 | Security pipeline: SAST, dependency scanning, SBOM, signed images, actions pinned by commit | Done |
| 10 | Registered recipients (ECDH) and recovery codes | |

## Known limits of version 1

- Files are limited to 200 MB, because the browser holds a downloaded file in memory before saving it.
- An interrupted upload cannot be resumed.
- There are no accounts; whoever holds the owner token (kept in the sender's browser tab) can revoke.
- See [`docs/threat-model.md`](docs/threat-model.md) for the security limits.
