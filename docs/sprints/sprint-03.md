# Sprint 3

**Dates:** 2026-11-10 to 2026-11-23

**Goal:** Phase 1b: a signed-in buyer buys a copy in the browser, and the last-copy k6 test passes through the gateway, both in CI.

Sprint 2 ended early, on 2026-11-09, because its goal was reached and the follow-ups were merged. Its review and retro, and the Phase 1a mock interview, followed on 2026-11-11 (see [`sprint-02.md`](sprint-02.md)).

## Plan

The tasks of [the Phase 1b plan](../plans/2026-11-09-phase-1b-checkout.md), in four waves. A wave starts when the previous one has merged; up to three tasks run at once.

| Wave | Issue | Task |
|---|---|---|
| A | #68 | 1 Messaging tells "not now" from "never" |
| A | #69 | 2 Sign-in groundwork |
| B | #70 | 3 Inventory for checkout |
| B | #71 | 4 Payments and FakePay |
| B | #72 | 5 The gateway as BFF |
| C | #73 | 6 Creators and channels |
| C | #74 | 7 Orders, the checkout saga |
| C | #75 | 8 The web app |
| D | #76 | 9 Phase 1b done |

### Changed after Sprint 2's retro

At the retro the owner asked for more weight on interview preparation: they learned most from the mock interview and haven't had time to read the code yet. The owner's interview notes (`docs/Interview Questions + AWS.pdf` in the roadmap workspace) also show that more than half of the real questions are C# basics that the roadmap doesn't cover, such as `virtual` versus `abstract`, value and reference types, and `IDisposable`. So this sprint changes in four ways:

1. **A question bank first.** `roadmap/interview-questions.md` lists every backend question from the interview notes, grouped by topic and sorted by how often it came up. Each has a short answer, a short example where it helps, and a link to the long topic.
2. **Short topics on the C# basics** that the roadmap doesn't cover yet: key ideas, one short example and the questions.
3. **Short mock interviews two or three times a week:** five questions, three new ones from the bank and two repeats of earlier gaps.
4. **Wave B waits for the question bank, then runs one pull request at a time.** Each walkthrough ends with three interview questions that its code answers. The review is answering them, using only the files the walkthrough names.

## Review

## Retro

### What I learned

### What was hard

### Open questions
