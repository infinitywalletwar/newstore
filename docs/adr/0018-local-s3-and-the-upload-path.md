# 0018. Local S3 and the upload path: Floci, presigned multipart, part URLs signed for the browser's host

## Status

Accepted

## Date

2026-11-14

## Context

Phase 2 lets a creator upload a video of up to 2 GiB. The bytes must not pass through the API (V-1, and Kestrel's request limits stay as they are), so the browser sends them straight to S3 with presigned URLs. Production is real S3 (Phase 6). On the Mac and in CI we need something that behaves like S3 in every place this phase relies on: multipart upload, presigned URLs with signature checks, CORS with a readable `ETag`, a bucket policy that makes one prefix public, range reads and download file names.

The open question was whether a young emulator can be trusted with that. So the spike wrote each reliance as a contract test (`tests/Media.Tests/EmulatorContractTests.cs`), ran it against the pinned image, and measured the emulator and FFmpeg on the Mac.

## Decision

- **Floci 2.4.0 as the local S3**, behind AWSSDK.S3 4.1.0 (`ServiceURL`, `ForcePathStyle`), with signature checks on (`FLOCI_SERVICES_S3_ENFORCE_AUTH=true`). Compose runs it in `persistent` storage mode with a volume; the tests run the same image through `Testcontainers.Floci`.
- **Presigned multipart upload, 64 MiB parts** (5 MiB in tests). Media creates the upload and signs one URL per part. The browser sends each part, reads its `ETag` (CORS `ExposeHeaders: ETag`) and calls complete.
- **Part URLs are signed for the host the browser uses.** A second S3 client, configured with the public address (`http://localhost:4566`), signs the URLs and never sends a request. The first client, configured with the address inside the network (`floci:4566`), makes the server-side calls. SigV4 signs the `host` header, so a URL signed for one host fails on another (check 2 below).
- **After a reload the creator picks the file again.** Media lists the uploaded parts with `ListParts`, the browser sends only the missing ones.

### Which way Task 2 completes an upload

AWS says: "Do not use the result of this listing when sending a *complete multipart upload* request. Instead, maintain your own list of the part numbers that you specified when uploading parts and the corresponding ETag values that Amazon S3 returns." ([multipart upload overview, "Multipart upload listings"](https://docs.aws.amazon.com/AmazonS3/latest/userguide/mpuoverview.html)).

Floci does complete an upload from listed ETags (check 5, passes). Task 2 still follows the plan's design: the browser sends the ETags it has read, and after a reload `GET /uploads/{id}` returns the parts that `ListParts` lists, with their ETags, so the browser can send them back to complete. That is the path AWS warns about for the reloaded part of the list. We accept it for two reasons: the listing is the only record of parts uploaded before the reload, and an alternative (the browser reports each ETag to Media, which stores it in an `upload_parts` table) adds an endpoint and a table to do what the listing already does. The cost: Floci passing is not proof for S3. Real S3 is first tried in Phase 6; if it refuses, Task 2's fallback (the `upload_parts` table, described in the plan's Task 2 Step 3) is ready and is the only change needed. This is the largest unverified point of this ADR.

## Alternatives considered

- **LocalStack.** Its single image now needs an auth token: the project's announcement says access to the latest releases "will require authenticated access using a LocalStack for AWS account and an auth token" ([blog, 2026-03-05](https://blog.localstack.cloud/localstack-single-image-next-steps/), [auth token](https://docs.localstack.cloud/aws/getting-started/auth-token/)). The GitHub repository `localstack/localstack` is archived (`gh api repos/localstack/localstack --jq .archived` returned `true` on 2026-11-14). It stays the fallback if Floci fails a check, and that choice needs the owner's account.
- **MinIO.** The repository was archived on 2026-04-25 and "We will no longer provide pre-compiled binary releases for the community version" ([github.com/minio/minio](https://github.com/minio/minio)).
- **SeaweedFS.** Not checked for arm64 or event notifications.
- **One presigned PUT per file.** Simpler, but a dropped connection at 1.9 GiB restarts the whole upload: no resume.
- **Bytes through the API.** Against V-1 and the Kestrel limits we keep.
- **S3 event notifications instead of the browser calling complete.** Notifications "are designed to be delivered at least once" and go only to SNS, SQS, Lambda or EventBridge ([Event Notifications](https://docs.aws.amazon.com/AmazonS3/latest/userguide/EventNotifications.html)). That adds a queue and a consumer to a local stack and does not replace complete: the object does not exist until complete is called.

## What the spike found

All checks ran against `floci/floci:2.4.0` on 2026-11-14 with `dotnet test --project tests/Media.Tests`. No knob from the plan was needed: the default SDK checksum setting (`WHEN_SUPPORTED`) works, and `persistent` storage works in the test container.

| Check (test) | Result |
|---|---|
| Part URLs signed for `localhost`, three parts (5, 5, 2 MiB), complete with the PUT ETags, `HeadObject` says 12 MiB (`Part_urls_signed_for_the_public_host_upload_a_multipart_file`) | passes |
| A URL signed for `127.0.0.1` and sent to `localhost` (`Url_signed_for_another_host_is_refused`) | 403 |
| A URL used 4 seconds after a 2-second lifetime (`Expired_url_is_refused`) | 403 |
| CORS preflight from `http://localhost:8100` for a part PUT; the PUT exposes `ETag`; a preflight from `http://evil.test` gets no `Access-Control-Allow-Origin` (`Cors_preflight_allows_a_part_put_and_exposes_the_etag`) | passes |
| Complete with the ETags that `ListParts` returned (`Listed_etags_complete_the_upload`) | passes: the object is 12 MiB |
| A part URL signed with `Content-Length` (`Signed_content_length_fixes_the_part_size`) | passes: `X-Amz-SignedHeaders` is `content-length;host`, and a PUT one byte shorter is 403 |
| Anonymous read of `hls/x/master.m3u8` and of `originals/x/y` with the bucket policy (`Anonymous_reads_only_the_hls_prefix`) | 200 and 403 |
| Anonymous `Range: bytes=0-99` on an `hls/` object (`Range_read_is_206`) | 206, `Content-Range: bytes 0-99/1000`, the first 100 bytes |
| Presigned GET with `ResponseHeaderOverrides.ContentDisposition` (`Download_url_sets_the_file_name`) | the same `Content-Disposition` comes back |
| `ListParts` after `AbortMultipartUpload` (`Abort_removes_the_upload`) | `NoSuchUpload` |
| 16 random 64 MiB parts through part URLs, complete (`Floci_takes_a_1_GiB_multipart_upload`, explicit) | passes: 1 GiB; 12 to 24 s in the three runs of this spike |

The signed `Content-Length` result means Media can sign each part's exact size (every part but the last is `PartSize`, the last is the remainder), and S3 itself refuses a part of another size. The size is still checked after complete (Task 2), because a client could use another upload.

As a check that the tests can fail, the first run was repeated with `FLOCI_SERVICES_S3_ENFORCE_AUTH=false`: three tests failed (the wrong host, the anonymous read of `originals/`, the signed content length) because everything was allowed.

| Measurement | Result |
|---|---|
| Floci image architecture (`docker image inspect floci/floci:2.4.0 --format '{{.Architecture}}'`) | `arm64` |
| Floci idle memory in Compose (`docker stats --no-stream`, 20 s after healthy) | 29 MiB of 512 MiB |
| Floci under the 1 GiB upload, container limited to 512m (as in Compose) | the test passes; `oom_kill 0`; anonymous memory at most 66 MiB (sampled each second from `memory.stat`); `memory.peak` 512 MiB, because the written file data sits in the page cache (about 450 MiB of `file`), which the kernel reclaims at the limit |
| The same upload with no limit | `memory.peak` at least 1.06 GiB (last sample before the container went), all of it cache |
| The worker's base image (`mcr.microsoft.com/dotnet/aspnet:10.0.15`) | Ubuntu 24.04.5 LTS |
| FFmpeg from `apt-get install --no-install-recommends ffmpeg` in it (arm64) | `ffmpeg version 6.1.1-3ubuntu5` (package `7:6.1.1-3ubuntu5`); `configuration` has `--enable-gpl` and `--enable-libx264` |
| ffprobe, HLS and thumbnail commands of the plan on three `lavfi` samples (12 s: 1280x720 with sine audio; 1280x720 without audio; 720x1280 with audio) | `master.m3u8` with two `EXT-X-STREAM-INF`: 640x360 and 1280x720 (202x360 and 406x720 for the portrait one); the silent one has no `mp4a` in `CODECS`; each has two segments per variant and a thumbnail 360 pixels high (640x360, 202x360); ffprobe reports demuxer `mov,mp4,m4a,3gp,3g2,mj2` |
| A concat script (`ffconcat version 1.0`, `file '/media/landscape.mp4'`) given to ffprobe and to ffmpeg with `-format_whitelist mov,matroska` | both print `Format not on whitelist 'mov,matroska'` |
| The 1 GB sample, the plan's command unchanged (`-t 240`, `ultrafast`, 36M), made in a container with `--cpus 4 --memory 2g` | 1,085,914,917 bytes (at least 1 GiB, 1,073,741,824), 55.7 s (`time`); cgroup peak 1.04 GiB, cache included |
| HLS encode of that file (240 s, 1080p30, 36 Mbit/s H.264), `--memory 2g --cpus 4`, `-preset medium`, `-g 180` | 2 min 48 s (168.3 s), 0.70 of the video's length; `memory.peak` 862 MiB, of which anonymous memory was under 1 MiB at the end and 150 MiB was cache; 41 segments per variant, 116 MB of output |

Decisions the numbers settle:

- **`Media:Processing:X264Preset` is `medium`**, x264's default. The 1 GB encode took 2 min 48 s, far under the 10 minutes after which the plan asks for a second run with `veryfast`.
- **Floci keeps its 512m limit.** The plan says to raise it when `memory.peak` passes 70% of the limit. It does (512 MiB, 100%), but the limit is reached by page cache, not by the process: anonymous memory stays at 66 MiB, nothing is killed and the 1 GiB upload passes under the limit. A larger limit would only let the cache grow. If a later phase sees `oom_kill` above 0 or a failed upload, raise it then.
- **The worker keeps `2g` and `cpus: "4"`:** the encode's anonymous memory is small, and the cgroup peak (cache included) was 862 MiB.
- **The 1 GB sample's command is the plan's, unchanged.** It was made in a container, not with FFmpeg on the Mac: the owner's rule is that FFmpeg runs only in containers. Its timing is from Ubuntu's FFmpeg 6.1.1 on arm64 with four CPUs, which is what the worker gets.

## Consequences

- **An upload in progress does not survive a Floci restart.** Floci's S3 page says: "Multipart uploads in progress do not survive a restart" ([Floci S3 at 2.4.0](https://github.com/floci-io/floci/blob/2.4.0/docs/services/s3.md)). I did not restart Floci to check it. A creator whose local stack restarted starts the upload again; production S3 has no such limit.
- **Abandoned uploads.** On S3 the lifecycle rule `AbortIncompleteMultipartUpload` comes with Terraform in Phase 6. Until then Media aborts uploads it marks `Expired` (Task 2).
- **The contract tests guard upgrades.** Floci is young (the repository was created 2026-02-18, `gh api repos/floci-io/floci --jq .created_at`) and releases twice a month. These tests are the evidence we have, not its claims, and a Dependabot bump of the image runs them. Two earlier Floci bugs that would have broken this design, bucket CORS never applied and a signature-checked preflight, are fixed in 2.4.0; `Cors_preflight_allows_a_part_put_and_exposes_the_etag` pins both.
- **What an emulator cannot prove:** that real S3 behaves the same. The listed-ETags check is the one where AWS's documentation and Floci point different ways; Phase 6 tries it on S3.
- **Ubuntu's FFmpeg is old** (6.1.1) and its security fixes arrive through Ubuntu Pro (`esm-apps`), per the plan; ADR 0020 records what that means for a worker that parses untrusted files.
- **Two host names for one port are how a test fakes the browser's address.** `S3Server.InternalUrl` is `127.0.0.1`, `PublicUrl` is `localhost`; in Compose the pair is `floci:4566` and `localhost:4566`.
