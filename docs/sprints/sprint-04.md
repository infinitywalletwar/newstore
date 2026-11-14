# Sprint 4

**Dates:** 2026-11-14 to 2026-11-27

**Goal:** Phase 2: an uploaded 1 GB video becomes playable and a broken file shows as Failed, in CI and on the Mac; the owner of a digital copy downloads the original.

Sprint 3 reached its goal on 2026-11-13, ahead of its end date, so this sprint starts the next day. Sprint 3's review and retro, with the Phase 1b mock interview, go in [`sprint-03.md`](sprint-03.md).

## Plan

The tasks of [the Phase 2 plan](../plans/2026-11-14-phase-2-video-pipeline.md), in four waves. A wave starts when the previous one has merged; up to three tasks run at once.

| Wave | Issue | Task |
|---|---|---|
| A | #115 | 1 The spike: local S3 and FFmpeg |
| B | #116 | 2 Media and resumable uploads |
| B | #117 | 3 Catalog: drafts and `VideoPublished` |
| B | #118 | 4 The gateway routes (merges after 2) |
| C | #119 | 5 The worker |
| C | #120 | 6 Downloads |
| C | #121 | 7 The web app |
| D | #122 | 8 Phase 2 done |

Task 1 is a spike: if the local S3 emulator fails one of its checks, the sprint stops there until the owner decides on the fallback.

### Changed after Sprint 3's retro

Every pull request's walkthrough has at least one Mermaid diagram of the flow it adds or changes, with a few sentences under it, as the owner asked at the retro. Each walkthrough still ends with three interview questions its code answers.

## Review

## Retro

### What I learned

### What was hard

### Open questions

### What the sprint built (Claude)

### How the agent worked (Claude)
