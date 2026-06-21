# sdlc

An issue-driven SDLC skill for Claude Code. Every unit of work is a GitHub issue. Parallel agent sessions work in isolated git worktrees. GitHub labels carry all workflow routing.

## Install

```bash
apm install nigel-hall-codes/sdlc
```

Or pin a version:

```bash
apm install nigel-hall-codes/sdlc#v0.1.0
```

After install, invoke with `/sdlc` in any Claude Code session.

## Concepts

**Two workflow families:**

| Family | Types | Purpose |
|---|---|---|
| Figuring-it-out | `idea` `sysdesign` `techdesign` | Analysis artifacts, no code changes |
| Leaf | `fix` `feat` `refactor` `docs` `chore` | Code — conventional commit types |

**Labels carry routing** (three dimensions + two links):
```
sdlc:workflow:feat        # which workflow
sdlc:feat:in-progress     # current step
persona:fullstackcoder    # assigned persona
discovered-from:#12       # child → parent provenance
blocker:#7                # dependency
```

**Every work session is a git worktree** — agents work in parallel without touching each other's branches.

## Subcommands

```
/sdlc capture <title>        Create and classify a GitHub issue
/sdlc work #N                Open a work session in a git worktree
/sdlc analyze #N             Run current-phase analysis, write artifact
/sdlc plan #N                Fan out child issues from a completed idea
/sdlc review #N [--merge]    Produce findings; optionally merge and close
/sdlc summarize [#N ...]     Read-only summary (omit for all open issues)
/sdlc close #N               Remove worktree and close the issue
```

## Lifecycle: figuring-it-out

```
idea
  /sdlc capture "idea: explore battle scoring redesign"
  → #12 created [analyzing]

  /sdlc work #12          ← enter session on sdlc/#12-idea
  /sdlc analyze #12       ← runs analysis, writes sdlc/#12/analysis.md, you say "yes"
  /sdlc analyze #12       ← runs prior-art, writes sdlc/#12/prior-art.md, you say "yes"
  /sdlc analyze #12       ← writes sdlc/#12/plan.md, you say "yes" → step: done
  /sdlc plan #12          ← fans out child issues #13, #14, #15

sysdesign / techdesign (same but two steps)
  /sdlc work #13
  /sdlc analyze #13       ← writes sdlc/#13/design.md, you say "yes" → done
```

## Lifecycle: leaf

```
/sdlc capture "fix: null crash when battle ends"
→ #20 created [captured, fullstackcoder]

/sdlc work #20            ← worktree created, step → scoped, then in-progress
  ... build the fix ...
/sdlc review #20          ← findings produced
/sdlc review #20 --merge  ← merge + close + worktree removed
```

## Persona routing

The skill scans `.claude/agents/` first. If it finds agents whose descriptions match the workflow, it uses them. Falls back to generic inline personas when no match:

| Workflow | Generic fallback |
|---|---|
| `idea` | `product-owner` |
| `sysdesign` | `systems-architect` |
| `techdesign` `fix` `feat` `docs` `chore` | `fullstackcoder` |
| `refactor` | `techdesigner` |

## Requirements

- `gh` CLI authenticated (`gh auth login`)
- Git repository with a remote
- Claude Code with `Bash`, `Read`, `Write`, `Edit`, `Agent` tool permissions
