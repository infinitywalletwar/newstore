# 0019. Who owns a video's state: Media owns the upload, Catalog owns what viewers see

## Status

Accepted

## Date

2026-11-14

## Context

A video has two lives. Before anyone can watch it, a file travels from the creator's browser to S3 and a worker turns it into HLS: that is an upload with parts, a job, a failure reason. After that it is a title in a list, a page and a player: that is what viewers see. Phase 2 adds the first life, and the README gives each life to a service (Media: uploads, jobs and files; Catalog: the details of a video).

So one video has a status in two services. Who owns which, and how do the two stay in step?

## Decision

- **Media owns the upload and its processing.** Its states are `Uploading`, `Processing`, `Published`, `Failed` and `Expired`. Only Media changes them.
- **Catalog owns what viewers see.** Its states are `Draft` and `Published`. A draft is created by Catalog (`POST /videos`) before Media can accept an upload for it: Media checks the owner and the status with `GET /videos/{id}/owner`, which the client `media` may call besides `inventory`.
- **One event joins them: `VideoPublished`**, sent by Media through the outbox (key: the video's id, topic `media.videos`) when processing succeeds. Catalog turns the draft into a published video with one `UPDATE ... WHERE id = @id AND status = 'Draft'`. Media's `Published` and Catalog's `Published` are two facts that the event keeps in step: Media's says "the files exist", Catalog's says "viewers may see it".
- **There is no `VideoFailed`.** A failed or expired upload changes nothing for viewers: the video stays a draft. The creator learns the reason from Media, which owns it, so Catalog would only store a copy that nobody reads.
- **The update is guarded by the status, not by a version.** In Phase 2 a video is published once, so "a draft becomes published" is the whole rule: a second `VideoPublished` (a different message id, for instance from a retried job) finds no draft and changes nothing. A video that does not exist is an error (the message is dead-lettered), because Catalog creates every video before Media can upload one.

## Alternatives considered

- **All the state in Catalog, with Media sending events.** One source of truth for lists. But Catalog would have to know the stages of processing (`Uploading`, `Processing`, ...) and the reasons for a failure, and Media would send an event for every change. Catalog's table would hold the job's progress, which is Media's data.
- **All the state in Media, with Catalog calling it.** No duplicated status. But every read of a list or a video page would depend on Media being up, which breaks "reads keep working" when a service is down (ADR 0009 keeps Catalog's reads alive without Redis; this would add a harder dependency).
- **A version in the event, as in ADR 0011 (`EditionSnapshot`).** Needed when a state moves both ways and events may arrive out of order (ADR 0008 says the relay can reorder one aggregate's events after a Kafka outage). Here the state moves one way, once, so the status is the version. When V-5 adds removal, a video will have more than one transition, and the guard has to change then.

## Consequences

- Catalog keeps working when Media is down: viewers read published videos, and a creator still creates drafts and reads `/me/videos`.
- A draft is a 404 on `GET /videos/{id}` for everyone, its creator included: the page exists only once there is something to show. The creator's view of a draft is `/me/videos`.
- The two `Published` statuses can differ for a moment, between Media's commit and Catalog's handler. A video that Media published and Catalog has not heard of is a draft until the event arrives; the outbox makes it arrive eventually, not instantly.
- The handler drops the cache tags `video:{id}` and `channel:{slug}` (ADR 0009). A page cached in the race between the update and the commit shows the old state until its 60 seconds pass.
- A `VideoPublished` for a video Catalog does not have goes to `media.videos.dlt` after three attempts. Nothing reads that topic yet (ADR 0008).
- The migration that adds the status marks every existing video `Published` and points it at the sample files, so a stack seeded in Phase 1 plays the sample without a reset.
- Rolling the migration back (`Down`) deletes every draft: `published_at` is required again, and a draft has none. A draft holds only a title and a description that Media has not touched, so we accept losing them for a rollback that is not planned.
