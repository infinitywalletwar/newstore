# Start Phase 0 from scratch

This is the checklist for starting newstore's Phase 0 in a fresh Claude Code session and taking it to the end of sprint 1. It gives the order and who does what. The steps of each task are in the [Phase 0 plan](plans/2026-11-03-phase-0-foundation.md). The rules every task follows are in [`AGENTS.md`](../AGENTS.md).

## Already decided

On 2026-11-04 the owner:

- approved the Phase 0 plan as written, including Kafka, Keycloak and Redis in Task 6
- chose subagent execution: the `code-writer` agent in `~/.claude/agents/code-writer.md` builds each task, and the main session coordinates, reviews its work and asks you before anything leaves the Mac
- chose a public repository, `infinitywalletwar/newstore`
- chose the commit identity that Task 1 sets for this repository

## 1. Check the Mac

| Check | Command | Expected |
|---|---|---|
| .NET 10 SDK | `dotnet --list-sdks` | a line starting with `10.0.403` |
| Docker is running, on arm64 | `docker version --format '{{.Server.Arch}}'` | `arm64` (Docker runs on OrbStack) |
| gh account and scopes | `gh auth status` | account `infinitywalletwar`, with `project`, `repo` and `workflow` among the scopes |
| Tools | `command -v jq curl nc` | three paths |

All four checks passed on 2026-11-04.

If gh is missing a scope, Claude runs `gh auth refresh -h github.com -s <scope>` in the background and passes on the one-time code it prints. Approve the code at https://github.com/login/device while the browser is signed in as `infinitywalletwar`. Typing `! gh auth refresh` in the prompt fails, because the command needs an interactive terminal.

## 2. Read before building

The roadmap's learning loop says to read the Stage 0 topics before the phase starts. They're in `~/Dev/stdy/roadmap/stage-0-foundation/`. If you'd rather read as you go, read each task's topics before you review its pull request.

| Task | Read first |
|---|---|
| 1 Repository skeleton | 01 Solution structure |
| 2 Backlog, board and first sprint | "How we work" in `README.md`. The roadmap's Agile topic isn't written yet. |
| 3 Catalog service with health checks | 02 Hosting and middleware, 03 Dependency injection, 04 Configuration and options, 05 Health checks, 09 Integration testing |
| 4 CI: build, format check and tests | 08 CI with GitHub Actions |
| 5 OpenTelemetry | 06 Logging and OpenTelemetry |
| 6 Local infrastructure in Docker Compose | 07 Docker and Compose |
| 7 Catalog in a container, and the Compose smoke test | 07 Docker and Compose, 08 CI with GitHub Actions |

## 3. Start the session

Start Claude Code in `~/Dev/stdy`, not in `example/newstore`, because Claude's memory for this project belongs to that folder. Then paste:

```text
Start Phase 0 of newstore. Follow example/newstore/docs/getting-started.md. Build Task 1, and ask me before running gh repo create.
```

## 4. Who does what

| Task | Claude | You |
|---|---|---|
| 1 Repository skeleton | Builds it locally on `main` and explains it in chat, because there's no repository to open a pull request in yet. | Say go, and Claude runs `gh repo create newstore --public`. |
| 2 Backlog, board and first sprint | Creates the labels, the 25 issues and the project board, then opens a pull request with the sprint note. | In the browser, switch the project to the Board layout, set a limit of 2 on In progress, and turn on the "Item closed" and "Pull request merged" workflows. Then review and merge the pull request. |
| 3 to 7 | Builds each task on its own branch, keeps its card on the board current, and opens a pull request with the walkthrough. | Read the pull request, ask about anything unclear, run it if you like, and merge it once CI is green. |
| After Task 7 | Gives a five-question mock interview on Stage 0. | Answer in your own words, then fill in Review and Retro in `docs/sprints/sprint-01.md`. |

Claude starts the next task only after you merge the previous one. Phase 0 is done when the checks in the plan's last section, "Phase 0 is done when", all pass.

## Follow the work

The working files live in `example/newstore/.superpowers/sdd/2026-11-03-phase-0-foundation/`. Git ignores that folder, and Claude deletes it when the phase is done.

| Where | What it shows |
|---|---|
| The agent's row below the prompt | The agent working live. Select the row with ↑ or ↓ and press Enter to read its transcript; Esc goes back. |
| `task-<N>-report.md` | The current task's steps, each added when it passes, with its commit. Test output and concerns follow below the list. |
| `task-<N>-pr.md` | The walkthrough that becomes the pull request description. |
| `progress.md` | One line per event across the phase: a task started, its review result, its pull request opened, merged. Claude keeps it. |
| `~/.claude/scripts/agent-usage` | What each agent run spent: requests, tokens, how much came from the cache, the cost at API prices, and the largest tool outputs. It works while the agent is still running. When a task finishes, Claude adds its one-line summary (`--line`) to `progress.md`. |
| The plan's checkboxes, the pull requests and the board | The record that stays after the folder is deleted. |

At the end of the phase, `agent-usage --type code-writer` lists every run. Read it next to the review findings in `progress.md` to decide what to change in `code-writer.md` or `AGENTS.md` before Phase 1, and record the changes in the sprint retro.

## Pick up where we stopped

If you only closed Claude Code, run `claude --continue` in `~/Dev/stdy`. That reopens this session, and Claude can resume the same agent with its history.



In a new session, again started in `~/Dev/stdy`, paste:

```text
Continue Phase 0 of newstore from where we stopped.
```

Claude finds the place from these:

- **Before the GitHub repository exists:** `git -C example/newstore log --oneline` shows whether Task 1's commit is there.
- **Afterwards:** `gh pr list --repo infinitywalletwar/newstore`. An open pull request means that task is waiting for your review.
- **`progress.md` and the task reports:** the last line of `progress.md` names the task in flight, and that task's report lists the steps already done. A new agent continues from the first step that isn't.
- **The plan's checkboxes:** each task's pull request ticks its own steps.
- **The board:** the card in In progress or Review is the current task.
