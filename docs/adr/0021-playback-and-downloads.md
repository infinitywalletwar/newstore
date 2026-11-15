# 0021. Playback and downloads: a public `hls/` prefix, and 15-minute links for owners

## Status

Accepted

## Date

2026-11-15

## Context

Phase 2 serves two kinds of file. Everyone may watch a published video, so its HLS files must be easy and cheap to read. Only the buyer of a digital copy may download the original, so it must stay private. Media also has to learn who owns a copy: Inventory assigns copies and announces each one with `CopyAssigned`.

## Decision

- **Playback reads a public prefix.** The bucket policy lets anyone `GetObject` under `hls/*` and nothing else (ADR 0018). Catalog builds the player's URLs from a base URL in configuration (`Catalog:MediaBaseUrl`). Today that is the emulator's address; in Phase 6 it becomes CloudFront's domain, with no code change.
- **A download is a presigned GET that lives 15 minutes** (`Media:Downloads:LinkLifetime`), signed for the host the browser uses. `POST /downloads` gives one link to the owner of a digital copy of a video that has a `Published` upload. Its `Content-Disposition` carries the upload's file name, made safe by one rule (`SafeFileName`) that the reply's `fileName` shares.
- **A link is a bearer.** Whoever holds it can use it until it expires. "Only the owner can download" means only the owner can get a link: the check happens when the link is issued, not when it is used.
- **Ownership comes from `CopyAssigned`, kept in Media's own `digital_copies` table.** The handler inserts a row for a `Digital` copy with `ON CONFLICT (owner_id, video_id) DO NOTHING` and ignores a `Dvd`. A buyer is recorded a few seconds after paying, as E-3's lag is. A buyer who asks in that gap gets a 403 `no-digital-copy` and asks again.
- **Phase 6's caveat:** AWS says that a presigned URL made with temporary credentials expires when they do, even if the URL was given a later time ([Expiration time for presigned URLs](https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-presigned-url.html#PresignedUrl-Expiration)). Media on a role will sign with temporary credentials, so a 15-minute link may die earlier; the role's session must outlast the link, or the link is shorter than it says.

## Alternatives considered

- **Playback through the gateway** (the bytes pass through ASP.NET Core). It could check a token on every segment. But every viewer's every segment would cost the API a connection and its bandwidth. A public prefix leaves the bytes to S3 and, later, a CDN.
- **A presigned GET per file.** Only the signed file would work, but an HLS playlist lists its segments by relative URI, and the standard resolves them against the playlist's own URI: "Any relative URI is considered to be relative to the URI of the Playlist that contains it" ([RFC 8216](https://www.rfc-editor.org/rfc/rfc8216)). A relative URI carries no signature, so Media would rewrite every playlist for each viewer.
- **Downloads streamed through Media** (a strict "only the owner" on every byte, and the owner's access can be cut off at once). The cost is the original's bytes (up to 2 GiB) through the API for every download. We chose the link: it is cheap and S3 serves the bytes.
- **Asking Inventory for ownership on each request.** ADR 0016 chose that call for editions, where a stale answer would let someone change what they must not. Here a stale answer costs a few seconds before a buyer's first download, and a call per request would tie downloads to Inventory being up. The event gives Media its own table and no runtime dependency.

## Consequences

- A leaked link works for up to 15 minutes for whoever holds it. A download that starts before the expiry finishes after it: "Amazon S3 checks the expiration date and time of a signed URL at the time of the HTTP request" (same page). A restarted download after the expiry fails.
- Revoking an owner's access (a refund) is not instant: links already issued live out their 15 minutes. No Phase 2 story revokes ownership.
- `digital_copies` lags Inventory, and `CopyAssigned` replays must stay within the inbox retention (AGENTS.md, Events).
