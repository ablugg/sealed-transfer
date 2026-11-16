# Threat model

Status: first draft for version 1 (link-mode sharing, no accounts). Extend it with each milestone.

## What is being protected

| Asset | Where it exists | Who must not get it |
| --- | --- | --- |
| File contents, name, type and size | Plaintext only in the sender's and recipient's browsers | The server, the storage provider, anyone on the network |
| File secret (32 bytes) | The sender's browser, the share link, the recipient's browser | Everyone except the people the sender gives the link to |
| Owner token | The sender's browser; the server keeps only its SHA-256 hash | Anyone but the sender |
| Download token | The recipient's browser for 15 minutes | Anyone not downloading that transfer |

## Trust boundaries

1. **Browser to API.** The API receives ciphertext, a 12-byte public header, and counts and sizes. It never receives a key.
2. **API to storage.** PostgreSQL and the blob store hold only what the API holds.
3. **Browser to identity provider.** Auth0 tokens are validated server-side; the API never sees or stores passwords.
4. **Static host to browser.** The browser trusts the JavaScript it is served. This is the weakest boundary (see limit 1).

## What the design defends against

| Threat | Defence |
| --- | --- |
| Server or storage breach exposes files | Files and metadata are AES-256-GCM ciphertext; keys are never sent to the server |
| Server alters, reorders, drops or truncates chunks | Chunk index and last-chunk flag are in each nonce; the header is bound as associated data; the total size is checked against authenticated metadata |
| Ciphertext from one transfer replayed into another | Each file has its own random secret and nonce prefix |
| Someone other than the sender uploads to, completes or revokes a transfer | 256-bit owner token, stored hashed, compared in constant time |
| Download limit bypassed by racing requests | Optimistic concurrency on the transfer row; the limit is re-checked after a conflict |
| Unauthorized transfer creation | Optional Auth0 sign-in; when configured, the API validates JWT bearer tokens from Auth0 |
| Oversized or malformed uploads exhaust storage | Declared size capped, each chunk capped, bytes counted while streaming, creation rate-limited |
| Key leaks through the address bar, history or Referer header | Key is removed from the URL on load; no-referrer policy on the page and the API |
| Path traversal through storage keys | Blob paths are built only from a GUID and an integer |

## Known limits

1. **The server delivers the JavaScript.** Whoever controls the static host can serve code that leaks keys. Done: strict Content Security Policy on HTML responses, Subresource Integrity on all scripts and styles. Planned: a separate origin for the front end, signed build provenance. Not fully removable in a web app.
2. **No server-side malware scanning.** The server cannot read files. Done: client-side magic-byte validation and blocked-extension list before encryption. Planned: verified senders, report-and-takedown.
3. **Anyone holding the link can decrypt.** Mitigated by expiry, download limits and revoke. Planned: password mode, emailed one-time codes, registered recipients with key pairs.
4. **Losing the link means losing the file.** Accepted for link mode.
5. **The server learns metadata it needs to operate:** approximate file size, upload and download times, and client IP addresses.
6. **Rate limiting uses the connection address.** Behind a reverse proxy it must be configured to use forwarded headers, or every client shares one limit.
7. **Download tokens depend on the Data Protection key ring.** In production the key ring must be persisted and protected (for example in Azure Key Vault), or tokens stop working after a restart.

## Out of scope for version 1

A compromised sender or recipient device, a malicious browser extension, and traffic analysis.
