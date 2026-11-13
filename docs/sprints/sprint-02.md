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

The goal was reached on 2026-11-06. The rest of the sprint went to follow-ups from the reviews and to two more Phase 1a stories, E-2 and E-3:

| Issue | Task | Pull request |
|---|---|---|
| #48 | Silence analyzers in migrations in one place | #49 |
| #50 | CI misses a project dropped from the solution | #52 |
| #53 | Outbox and inbox rows are never deleted | #55 |
| #54 | Confirm versus sweep: lock order | #56 |
| #57 | E-2 in the API: the edition lock and the end of a sale | #58, #59 |
| – | E-3: the edition snapshot event and ADR 0011 | #62 |
| #60 | Inventory publishes edition snapshots (E-3) | #64 |
| #61 | Catalog keeps a copy of editions (E-3) | #63 |

## Review

The goal was reached on 2026-11-06: in CI, the k6 test with 1,000 buyers and 10 copies sells exactly 10. The follow-ups and E-2/E-3 merged by 2026-11-09, so the sprint ended early.

The Phase 1a mock interview ran on 2026-11-11: the five planned questions, then five more on database concurrency at the owner's request.

| Question | Result |
|---|---|
| 1. Concurrent updates of one resource | Named locks and transactions, but took the outbox for a concurrency tool. Retold optimistic and pessimistic locking after the explanation. |
| 2. Isolation levels | Didn't know them at first. Named all four by the end. |
| 3. Save, then publish to Kafka | The best answer: found the dual write problem and reached the core of the outbox. |
| 4. Idempotent consumer | The right idea ("skip a message id seen before"), but too short. |
| 5. Retrying a POST after a timeout | Didn't know. |
| Extra: optimistic or pessimistic, ACID, `NOLOCK`, deadlocks, write skew | Right intuition each time; missing the terms, the exact mechanisms and when to use which. |

The pattern across all ten: the owner reasons well about what breaks, but lacks the names, the exact mechanisms and the trade-offs. The answers to every question are already in newstore's code. The gaps per topic are tracked in the roadmap's `what-to-study.md`.

## Retro

### What I learned

So far, only from the mock interview.

### What was hard

I haven't looked at the code yet. The process is becoming more or less clear.

### Open questions

Basically everything is still open. I need to put more weight on interview preparation.

### What to change in Sprint 3

More short topics with short examples and questions, reviews, and mock interviews. Focus on interviews and what they can ask.

### Changes to the agent after Phase 1a (Claude)

Phase 1a's five tasks and the migrations follow-up cost about $9.50 of agent time at API prices (Sonnet 5.5). Task 2, the shared groundwork, and Task 3, Inventory, were the largest at $2.79 and $2.34; the three parallel tasks ran 19 to 25 minutes each and never touched each other's files. Every task needed exactly one fix round, and almost every finding was in the explanation, not the code: two walkthroughs named a test or a property that the diff did not contain (Tasks 1 and 2); two links pointed at the right site but the wrong page or section (Task 4's PostgreSQL page, Task 3's EF Core claim); one ADR consequence was wrong under Kafka's default `auto.create.topics.enable` (Task 4); one ADR said "Docker Desktop" where the Mac runs OrbStack (Task 3). The code findings were a weakened assertion and a health check registered as an instance, so the container never disposed it (Task 5).

Changed in `~/.claude/agents/code-writer.md`: before writing the walkthrough, grep the diff for every test, type, member and file it names; a link points at the section that states the claim, and the report quotes that sentence; facts about the Mac come from a command such as `docker info`, never from memory.

One lesson was not the agent's. Merging `main` into the Inventory branch on GitHub resolved the conflict in `NewStore.slnx` by dropping `tests/Messaging.Tests`, and CI stayed green, because `dotnet test --solution` runs only the projects the solution lists. The main session now diffs every shared file for removed lines after such a merge, and a follow-up adds a CI step that compares the `.csproj` files on disk with the solution.

### The follow-ups and E-2/E-3 (Claude)

The seven follow-up tasks cost about $3.90 of agent time, not counting the fix rounds of Tasks 8 and 9. This time the review caught a code bug: E-2's migration added the edition lock without locking editions that already had reservations. Two lessons were the main session's own:

- **A pull request merged before its fix round.** #43 and #58 were merged while a fix was still on its way, so each fix needed a new pull request (#44, #59). A merged migration can't be edited, because EF Core records applied migrations by name, so #59 adds a corrective migration. Now the main session writes "ready to merge" only once the fix round is pushed and CI is green.
- **A brief that skipped the images.** The E-3 briefs kept the agents off the Compose stack, so nobody built the images. CI's compose-smoke then failed: each Dockerfile restores from the project files it copies, and the new `NewStore.Contracts` project wasn't among them. `AGENTS.md` now says to run `docker compose build <service>` whenever a project reference changes. That command builds images without starting the stack.
