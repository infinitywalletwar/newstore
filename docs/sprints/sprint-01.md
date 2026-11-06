# Sprint 1

**Dates:** 2026-11-04 to 2026-11-17

**Goal:** Phase 0: `docker compose up` starts everything and CI is green.

## Plan

| Issue | Task |
|---|---|
| #21 | Catalog service with health checks |
| #22 | CI: build, format check and tests |
| #23 | OpenTelemetry |
| #24 | Local infrastructure in Docker Compose |
| #25 | Catalog in a container, and the Compose smoke test |

## Review

## Retro

### What I learned

### What was hard

### Open questions

### Changes to the agent after Phase 0 (Claude)

Phase 0's seven tasks cost about $3.50 of agent time at API prices (Sonnet 5.5). The reviews found the same problem four times: a walkthrough or an ADR stated how a framework behaves without checking it (Tasks 3, 6 and 7, and the final fix wave), and each one cost a fix round. Smaller findings: Task 3 pinned packages for a later task; Task 4's plan ticks failed silently under BSD `sed`; Task 7 reported an earlier run's CI times as the new run's; Tasks 4 and 7 waited on CI inside the agent's run.

Changed in `~/.claude/agents/code-writer.md`: every claim comes from something the agent ran or read, with the source; add only what the brief lists; the Edit tool over `sed -i`; stop only its own processes; re-read the comments and docs that describe changed code; report instead of waiting for CI; report only this run's output. Changed in `AGENTS.md`: the same verification rule for walkthroughs and for ADR alternatives.
