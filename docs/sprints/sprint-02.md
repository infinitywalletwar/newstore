# Sprint 2

**Dates:** 2026-11-06 to 2026-11-19

**Goal:** Phase 1a: the k6 test with 1,000 buyers and 10 copies sells exactly 10, in CI.

Sprint 1 ended early, on 2026-11-05, because its goal was reached. Its review and retro are still to be written.

## Plan

| Issue | Task |
|---|---|
| #34 | Hygiene |
| #35 | Shared groundwork |
| #36 | Inventory, the last copy |
| #37 | Messaging: the outbox and Kafka |
| #38 | Catalog reads: channels, sample videos, search and the cache |

#34 and #35 run one after the other. #36, #37 and #38 are independent and run in parallel, which is why the work-in-progress limit went from two to three (README, How we work).

## Review

## Retro

### What I learned

### What was hard

### Open questions

### Changes to the agent after Phase 1a (Claude)

Phase 1a's five tasks and the migrations follow-up cost about $9.50 of agent time at API prices (Sonnet 5.5). Task 2, the shared groundwork, and Task 3, Inventory, were the largest at $2.79 and $2.34; the three parallel tasks ran 19 to 25 minutes each and never touched each other's files. Every task needed exactly one fix round, and almost every finding was in the explanation, not the code: two walkthroughs named a test or a property that the diff did not contain (Tasks 1 and 2); two links pointed at the right site but the wrong page or section (Task 4's PostgreSQL page, Task 3's EF Core claim); one ADR consequence was wrong under Kafka's default `auto.create.topics.enable` (Task 4); one ADR said "Docker Desktop" where the Mac runs OrbStack (Task 3). The code findings were a weakened assertion and a health check registered as an instance, so the container never disposed it (Task 5).

Changed in `~/.claude/agents/code-writer.md`: before writing the walkthrough, grep the diff for every test, type, member and file it names; a link points at the section that states the claim, and the report quotes that sentence; facts about the Mac come from a command such as `docker info`, never from memory.

One lesson was not the agent's. Merging `main` into the Inventory branch on GitHub resolved the conflict in `NewStore.slnx` by dropping `tests/Messaging.Tests`, and CI stayed green, because `dotnet test --solution` runs only the projects the solution lists. The main session now diffs every shared file for removed lines after such a merge, and a follow-up adds a CI step that compares the `.csproj` files on disk with the solution.
