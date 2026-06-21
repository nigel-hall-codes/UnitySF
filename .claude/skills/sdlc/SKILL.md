---
name: sdlc
description: >
  Issue-driven SDLC orchestrator. Activate for any /sdlc subcommand: capture, work,
  analyze, plan, review, summarize, close. Manages GitHub issues, git worktrees,
  and persona routing for parallel agent development.
allowed-tools:
  - Bash
  - Read
  - Write
  - Edit
  - Agent
---

# SDLC

Every unit of work is a GitHub issue. Two workflow families:

| Family | Workflows | Purpose |
|---|---|---|
| Figuring-it-out | `idea` `sysdesign` `techdesign` | Analysis artifacts only — no code changes |
| Leaf | `fix` `feat` `refactor` `docs` `chore` | Code changes — conventional commit types |

Labels carry all routing. Three orthogonal dimensions + two link types:
```
sdlc:workflow:<type>      # which workflow
sdlc:<workflow>:<step>    # current step
persona:<name>            # assigned persona
discovered-from:#N        # child → parent provenance
blocker:#N                # dependency
```

## First: preconditions

Before any subcommand that writes to GitHub or git, verify:
```bash
gh auth status 2>&1 | grep -q "Logged in" || echo "NOT_AUTHED"
git rev-parse --git-dir 2>/dev/null || echo "NOT_A_REPO"
```
Fail clearly with fix instructions if either check fails.

## Second: locate this skill's directory

Run this once at the start of every invocation — sub-files are resolved relative to it:
```bash
find . -maxdepth 6 -path "*/.claude/skills/sdlc" -type d 2>/dev/null | head -1
```
If empty (global install): `find ~ -maxdepth 8 -path "*/.claude/skills/sdlc" -type d 2>/dev/null | head -1`

Call this path `SKILL_DIR`.

## Dispatch

Read the subcommand file and follow its instructions precisely:

| Subcommand | File |
|---|---|
| `capture` | `$SKILL_DIR/subcommands/capture.md` |
| `work` | `$SKILL_DIR/subcommands/work.md` |
| `analyze` | `$SKILL_DIR/subcommands/analyze.md` |
| `plan` | `$SKILL_DIR/subcommands/plan.md` |
| `review` | `$SKILL_DIR/subcommands/review.md` |
| `summarize` | `$SKILL_DIR/subcommands/summarize.md` |
| `close` | `$SKILL_DIR/subcommands/close.md` |

For persona routing, also read `$SKILL_DIR/personas.md`.
For label rules, also read `$SKILL_DIR/labels.md`.

## No subcommand

If `/sdlc` is called with no subcommand or an unrecognized one, print:

```
Usage: /sdlc <subcommand> [args]

Subcommands:
  capture <title>        Create and classify a GitHub issue
  work #N                Open a work session in a git worktree
  analyze #N             Run current-phase analysis, write artifact
  plan #N                Fan out child issues from a completed idea
  review #N [--merge]    Produce findings; optionally merge and close
  summarize [#N ...]     Read-only summary (omit for all open issues)
  close #N               Remove worktree and close the issue

Workflows:
  Figuring-it-out: idea | sysdesign | techdesign
  Leaf (conventional commits): fix | feat | refactor | docs | chore
```
