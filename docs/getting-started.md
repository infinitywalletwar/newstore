# Start or resume a phase

This is the checklist for starting a newstore phase in a fresh Claude Code session, or picking one up where it stopped. It gives the order and who does what. The steps of each task are in that phase's plan in [`plans/`](plans/). The rules every task follows are in [`AGENTS.md`](../AGENTS.md).

| Phase | Plan | Sprint note |
|---|---|---|
| 0 Foundation (done 2026-11-05) | [`2026-11-03-phase-0-foundation.md`](plans/2026-11-03-phase-0-foundation.md) | [`sprint-01.md`](sprints/sprint-01.md) |
| 1a Buy the last copy (done 2026-11-07; the mock interview is still to come) | [`2026-11-06-phase-1a-last-copy.md`](plans/2026-11-06-phase-1a-last-copy.md) | [`sprint-02.md`](sprints/sprint-02.md) |
| 1b Checkout (in progress) | [`2026-11-09-phase-1b-checkout.md`](plans/2026-11-09-phase-1b-checkout.md) | [`sprint-03.md`](sprints/sprint-03.md) |

## Already decided

- Execution: the `code-writer` agent in `~/.claude/agents/code-writer.md` builds each task. The main session coordinates, reviews the agent's work, and asks you before anything leaves the Mac. From Phase 1a on, independent tasks run in parallel, each agent in its own git worktree and branch.
- The repository is public: `infinitywalletwar/newstore`. The commit identity is set for this repository only.
- You merge every pull request. Claude never merges, closes or deletes for you.

## 1. Check the Mac

| Check | Command | Expected |
|---|---|---|
| .NET 10 SDK | `dotnet --list-sdks` | a line starting with `10.0.403` |
| Docker is running, on arm64 | `docker version --format '{{.Server.Arch}}'` | `arm64` (Docker runs on OrbStack) |
| gh account and scopes | `gh auth status` | account `infinitywalletwar`, with `project`, `repo` and `workflow` among the scopes |
| Tools | `command -v jq curl nc` | three paths |
| Node, for the Angular app (from Phase 1b) | `node --version` | `v26.` followed by any version; Angular 22.2 supports `>=26.0.0` |

If gh is missing a scope, Claude runs `gh auth refresh -h github.com -s <scope>` in the background and passes on the one-time code it prints. Approve the code at https://github.com/login/device while the browser is signed in as `infinitywalletwar`. Typing `! gh auth refresh` in the prompt fails, because the command needs an interactive terminal.

## 2. Read before building

The roadmap's learning loop says to read the stage's topics before the phase starts. They're in `~/Dev/stdy/roadmap/`. If you'd rather read as you go, read each task's topics before you review its pull request.

**Phase 1b.** Its Stage 1 topics are written: 08 Sagas, 09 OAuth 2.0, OIDC and JWT, 10 Async/await in depth, 11 Authorization policies, 12 API gateway with YARP and 13 Resilience. The Phase 1b plan maps them to its tasks.

**Phase 1a** read Stage 1 topics 01 to 07 and Stage 4's caching with HybridCache. **Phase 0** read Stage 0 topics 01 to 09. Each plan's task headings say which topic goes with which task.

## 3. Start the session

Start Claude Code in `~/Dev/stdy`, not in `example/newstore`, because Claude's memory for this project belongs to that folder. Then paste one of:

```text
Start Phase 1b of newstore. Follow example/newstore/docs/getting-started.md.
```

```text
Continue Phase 1b of newstore from where we stopped.
```

## 4. Who does what

| Step | Claude | You |
|---|---|---|
| The plan | Writes the phase's plan and opens a pull request for it. | Read it, ask about anything unclear, and merge it. |
| Each task | Builds it on its own branch, keeps its card on the board current, and opens a pull request with the walkthrough. | Read the pull request, ask about anything unclear, run it if you like, and merge it once CI is green. For the hard parts, make the small change the walkthrough suggests. |
| Parallel tasks | Runs them at the same time in separate worktrees. Only one of them may use the Compose stack on the Mac; the plan says which. | Expect several pull requests at once. Merge them one at a time; Claude merges `main` into the others after each. |
| End of phase | Gives a five-question mock interview on the phase's topics, then runs its own retro on how the agent worked. | Answer in your own words, then fill in Review and Retro in the sprint note. |

Serial tasks start only after you merge the previous one. A phase is done when the checks in the plan's last section, "… is done when", all pass.

## Follow the work

The working files live in `example/newstore/.superpowers/sdd/<plan name>/`. Git ignores that folder, and Claude deletes it when the phase is done.

| Where | What it shows |
|---|---|
| The agent's row below the prompt | The agent working live. Select the row with ↑ or ↓ and press Enter to read its transcript; Esc goes back. |
| `task-<N>-report.md` | The task's steps, each added when it passes, with its commit. Test output and concerns follow below the list. |
| `task-<N>-pr.md` | The walkthrough that becomes the pull request description. |
| `progress.md` | One line per event across the phase: a task started, its review result, its pull request opened, merged, and every ruling Claude made on its own. Claude keeps it. |
| `~/.claude/scripts/agent-usage` | What each agent run spent: requests, tokens, how much came from the cache, the cost at API prices, and the largest tool outputs. It works while the agent is still running. When a task finishes, Claude adds its one-line summary (`--line`) to `progress.md`. |
| The plan's checkboxes, the pull requests and the board | The record that stays after the folder is deleted. |

At the end of the phase, `agent-usage --type code-writer` lists every run. Read it next to the review findings in `progress.md` to decide what to change in `code-writer.md` or `AGENTS.md` before the next phase, and record the changes in the sprint retro.

## Pick up where we stopped

If you only closed Claude Code, run `claude --continue` in `~/Dev/stdy`. That reopens the session, and Claude can resume the same agent with its history.

In a new session, again started in `~/Dev/stdy`, paste the "Continue" prompt above. Claude finds the place from these:

- **`gh pr list --repo infinitywalletwar/newstore`:** an open pull request means that task is waiting for your review.
- **`progress.md` and the task reports:** the last lines of `progress.md` name the tasks in flight, and each task's report lists the steps already done. A new agent continues from the first step that isn't.
- **The plan's checkboxes:** each task's pull request ticks its own steps.
- **The board:** the cards in In progress or Review are the current tasks.
- **Worktrees:** `git worktree list` shows a parallel task's checkout if its agent was interrupted.
