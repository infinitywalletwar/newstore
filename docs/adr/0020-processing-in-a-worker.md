# 0020. Processing in a worker: a second container of Media, a jobs table with a lease, Ubuntu's FFmpeg

## Status

Accepted

## Date

2026-11-15

## Context

V-2 asks that a creator's upload becomes HLS at 360p and 720p with a thumbnail, in the background, and that the work "survives a service restart". Phase 2's "done when" adds that a broken file shows as Failed. Converting a 1 GB file takes minutes (2 min 48 s on 4 CPUs, ADR 0018), and FFmpeg parses files that strangers upload.

So three questions: where FFmpeg runs, how a job survives a process that dies halfway, and which FFmpeg we trust with untrusted input.

## Decision

- **A second container of the Media project, `media-worker`.** The same image build (the Dockerfile's `worker` stage adds FFmpeg to the API's runtime image) runs with `Media:Role` set to `Worker`: no endpoints but health, no port, `memory: 2g` and `cpus: "4"`, and `stop_grace_period: 30s`.
- **A jobs table with a lease, in Media's database.** Complete writes a Pending job in the same transaction as the upload's move to Processing (Task 2). The worker claims the oldest due job in one statement:

  ```sql
  UPDATE processing_jobs SET status = 'Running', attempts = attempts + 1, locked_by = @worker,
         locked_until = @now + @lease, updated_at = @now
  WHERE id = (SELECT id FROM processing_jobs
              WHERE (status = 'Pending' AND next_run_at <= @now) OR (status = 'Running' AND locked_until < @now)
              ORDER BY next_run_at LIMIT 1 FOR UPDATE SKIP LOCKED)
  RETURNING *;
  ```

  `SKIP LOCKED` lets a second worker's subquery pass over the row the first has locked instead of waiting for it; PostgreSQL's documentation names it for this use, "to avoid lock contention with multiple consumers accessing a queue-like table" ([SELECT, "The Locking Clause"](https://www.postgresql.org/docs/current/sql-select.html#SQL-FOR-UPDATE-SHARE)). The lease is 2 minutes and the worker renews it every 30 s. Every later write (renew, retry, fail, release, succeed) is guarded by `locked_by = @worker AND status = 'Running'`, so a worker whose lease another worker took changes nothing. The claim counts the attempt, so a job that kills its worker every time runs out of attempts.
- **The failure rule** is AGENTS.md's for consumers: retry what may pass, fail what cannot.
  - The file is the problem (ffprobe cannot read it, the demuxer is not MP4/MOV or Matroska/WebM, no video stream, no duration, FFmpeg fails): the job and the upload fail at once, with the reason the creator sees.
  - Anything else (S3, the network, the database) is retried after `30 s × 2^(attempts − 1)`, at most 10 minutes, and fails with `retries-exhausted` at the third attempt.
  - A job over 60 minutes fails with `timed-out`. A worker that is stopping kills FFmpeg and releases the job without using an attempt. A worker that is killed outright leaves the job to its lease.
  - The outcome writes (succeed, fail, retry, release) have a 10 s budget of their own, not the stop token, so a stop that lands after the encode still commits it.
- **Success is one transaction:** the job Succeeded, the upload Published with its paths and duration, and `VideoPublished` in the outbox (ADR 0008). `master.m3u8` is uploaded last, so the public master exists only once everything it names does.
- **FFmpeg is Ubuntu 24.04's package** (`apt-get install ffmpeg` on `mcr.microsoft.com/dotnet/aspnet:10.0.15`, which is Ubuntu 24.04), not pinned, because the archive replaces old versions. CI checks what it got: `ffmpeg -version` starts with `ffmpeg version 6.1.1-3ubuntu5` and its configuration has `--enable-libx264`.

## Alternatives considered

- **A `BackgroundService` in the API container.** One container fewer, but FFmpeg would be a child process in the API's container and share its limit: Docker's `--memory` is "the maximum amount of memory the container can use", and "By default, if an out-of-memory (OOM) error occurs, the kernel kills processes in a container" ([Resource constraints](https://docs.docker.com/engine/containers/resource_constraints/)). The API has 512m; ADR 0018's 1 GB encode peaked at 862 MiB and used 4 CPUs for almost 3 minutes. Uploads and status reads would wait behind encodes. The worker is the same code in another role, so this costs one Compose service, not another project.
- **A container per job.** Each encode isolated and sized on its own. Starting containers from Media needs the Docker socket or an orchestrator. The socket gives root on the host: the daemon "requires `root` privileges", and "only trusted users should be allowed to control your Docker daemon" ([Docker security, "Docker daemon attack surface"](https://docs.docker.com/engine/security/#docker-daemon-attack-surface)), which is the wrong thing to hand a process that parses uploads. An orchestrator does it properly: a Kubernetes Job "creates one or more Pods and will continue to retry execution of the Pods until a specified number of them successfully terminate" ([Jobs](https://kubernetes.io/docs/concepts/workloads/controllers/job/)). That fits Phase 5, when the system runs on Kubernetes (kind on the Mac); the table and the lease stay useful then, because something still has to track each job's status.
- **A Kafka "jobs" topic.** Kafka is already in the stack, and partitions spread work. But the job and the upload's status would no longer commit together: complete writes PostgreSQL, the topic is another system, and that gap is exactly what the outbox exists to close (ADR 0008). A long job also fits badly under `max.poll.interval.ms`: "If this interval is exceeded the consumer is considered failed and the group will rebalance" (librdkafka, quoted in ADR 0008); the default is 5 minutes, and an encode can take longer. A job's status, attempts, pause and error are plain columns in a table, next to the upload whose status the creator reads. Kafka carries only the outgoing `VideoPublished`.
- **Per-variant checkpoints** (roadmap topic 02, "Идемпотентность, повторы, чекпоинты"): record 360p as done, then 720p, and skip finished steps after a restart. Here both variants come from one FFmpeg run, which decodes the source once; splitting them into two runs would decode it twice to save a retry that the lease already makes rare. A retry redoes the whole job and overwrites the same keys, which is safe. Revisit if a job grows steps that are expensive on their own.

## FFmpeg from Ubuntu's archive

The image's FFmpeg is `6.1.1-3ubuntu5` (package `7:6.1.1-3ubuntu5`), built with `--enable-gpl` and `--enable-libx264` (ADR 0018, and `docker run --rm --entrypoint ffmpeg newstore/media-worker:local -version` in this task). Two consequences:

- **The binary is GPL.** FFmpeg "incorporates several optional parts and optimizations that are covered by the GNU General Public License", and "If those parts get used the GPL applies to all of FFmpeg" ([ffmpeg.org/legal](https://ffmpeg.org/legal.html)), and the same page's checklist names libx264 as a GPL library. GPL v2's conditions attach to copying, distribution and modification: "The act of running the Program is not restricted" ([GPL v2, section 0](https://www.gnu.org/licenses/old-licenses/gpl-2.0.txt)). The worker image is built on the Mac and in CI and is not published, so this asks nothing of us yet. Publishing it (Phase 5 or 6) is distribution, which section 3 allows only with the corresponding source or an offer of it.
- **Its security fixes come only through Ubuntu Pro.** The latest notice, [USN-8915-1](https://ubuntu.com/security/notices/USN-8915-1) (2026-10-12, "FFmpeg vulnerabilities"), lists `7:6.1.1-3ubuntu5+esm15` for 24.04, "Fix available with Ubuntu Pro via ESM Apps"; the public archive keeps `3ubuntu5`. The worker parses files strangers upload, so the exposure is limited around FFmpeg rather than by it:
  - it runs as a non-root user (`USER $APP_UID`; `id` in the image prints `uid=1654(app)`), with no published port, `memory: 2g` and `cpus: "4"`, and the 60-minute job timeout;
  - every path FFmpeg sees is built from ids (`{WorkDirectory}/jobs/{jobId}/source`), never from the uploaded file's name, and arguments go through `ProcessStartInfo.ArgumentList` with no shell;
  - ffprobe and ffmpeg read with `-protocol_whitelist file -format_whitelist mov,matroska`, so a playlist or a concat script that names other files is refused before anything opens them.

  The next step, when the image is deployed anywhere: Ubuntu Pro, which "is and always will be free for personal use on up to 5 physical machines" ([ubuntu.com/pro](https://ubuntu.com/pro)) but needs a token in the image build, or a pinned upstream FFmpeg build whose version CI checks the same way.

## Timings and memory

| What | Result |
|---|---|
| 1 GB, 240 s 1080p30 source, both variants, `-preset medium`, `--cpus 4 --memory 2g` (ADR 0018) | 2 min 48 s; `memory.peak` 862 MiB |
| 60 s 1280x720 source, the same command, `--cpus 4` (this task, `docker run` of the worker image) | 8.0 s; first segment after 1.06 s |
| `media-worker` idle, after `docker compose up -d --wait` (`docker stats --no-stream`) | 103.5 MiB of 2 GiB |
| `media-worker` running a real job on that 60 s source (claim to Succeeded) | `docker stats` at most 555.4 MiB of 2 GiB, CPU about 400%; the container's `memory.peak` 767 MiB (264 MiB before the job) |
| `media-migrate` (migrations, the seed's 10 s sample encode, 48 rows) | `docker stats` at most 519.8 MiB of 1 GiB; `memory.peak` at least 712 MiB (the last read before it exited) |
| The 1 GB test (`A_1_GB_upload_becomes_playable`, Task 8) on the Mac: Homebrew FFmpeg 9.0.2 on the host, no CPU limit, Floci in a container, 64 MiB parts, four at a time; a 1,086,110,188-byte sample (the same command as ADR 0018) | the sample 15.5 s; 17 parts uploaded 2.3 s; complete 0.6 s; processing to Published 1 min 2 s (one run, `--output Detailed`); the test passed in 1 min 20 s. The worker here is the test host's own, not the 2g, 4-CPU container, so this is not comparable to the 2 min 48 s above. The CI run's times follow after the first nightly run. |
| The worker image against the API image (`docker image ls`) | 1.09 GB against 543 MB |

`memory.peak` is the cgroup's own maximum and counts page cache and anonymous memory together (ADR 0018, "How the Floci memory numbers were taken"); `docker stats` is sampled, about once a second, so it can miss a short peak. Both figures stay under the limits. `media-migrate`'s `docker stats` figure is above the 512m every other one-shot job has, which is why it has 1g.

## Consequences

- **A job survives a restart, and a dead worker's job is taken over** once its lease runs out: tests `Expired_lease_is_taken_over_by_another_worker` and `Stopping_the_worker_releases_the_job`. In Compose, `docker compose stop media-worker` during an encode released the job in about a second, and `docker compose kill media-worker` left it Running until the lease ran out, when the restarted worker took it as attempt 2.
- **The cost of a dead worker is up to one lease** (2 minutes) before another worker takes its job. A shorter lease takes over sooner but renews more often, and a live worker could lose its job during a database pause longer than three renewals.
- **Work can run twice.** A worker that loses its lease and keeps encoding writes the same keys as its successor and then changes nothing in the database, because every write is guarded. Uploads to S3 are overwrites of the same objects, so the result is the same.
- **One more image to build** (1.09 GB, mostly FFmpeg and its libraries), and `media-migrate` moved to it because the seed encodes the sample.
- **FFmpeg's version is checked, not pinned.** When Ubuntu's archive changes the package, `compose-smoke` fails and the change is read before anything else moves.
- **A killed FFmpeg reads as a bad file.** Any non-zero exit of the encode is `encode-failed`, which is permanent, so an FFmpeg that the out-of-memory killer stops (a signal exit: `Process.ExitCode` was 137 for a child killed with SIGKILL, run on the Mac) fails the upload as if the file were broken. Next step: treat a signal exit as a transient failure, so it is retried.
- **A storage outage longer than the retry budget fails an upload for good.** Three attempts with 30 s and then 60 s pauses end after about 90 s of S3 or database trouble, as `retries-exhausted`, and the creator uploads again. There is no command to re-queue the job yet. Next step: an operator command, or an admin endpoint, that puts the job back to `Pending` and the upload back to `Processing` in one transaction.
- **Compose does not restart `media-worker`:** no long-running service in `compose.yaml` has a `restart:` policy, so it follows the same rule; a worker that dies stays down until `docker compose up -d`. Phase 5's Deployment restarts it.
- **Tests need FFmpeg on the machine:** `brew install ffmpeg` on the Mac (Homebrew's 9.0.2 here), Ubuntu's package in CI.
