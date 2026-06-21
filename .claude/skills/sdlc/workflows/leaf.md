# Workflow: Leaf Issues

Leaf issues are where code changes happen. Five types following conventional commits.

## Types

| Type | When to use | Persona |
|---|---|---|
| `fix` | Corrects a bug or broken behavior | `fullstackcoder` |
| `feat` | Adds new functionality | `fullstackcoder` |
| `refactor` | Restructures code without changing behavior | `techdesigner` |
| `docs` | Documentation-only changes | `fullstackcoder` |
| `chore` | Build, dependencies, CI, tooling | `fullstackcoder` |

`refactor` gets `techdesigner` because restructuring requires understanding what the code is *trying to be*, not just what it does. The blast radius matters.

## Phase sequence (all leaf types)

```
captured → scoped → in-progress → review → done
```

### scoped
Work has been read and scope confirmed by the user in `/sdlc work`. No code yet.
`work` auto-advances `captured → scoped` on first session entry.

### in-progress
Active development in the worktree.
`work` advances `scoped → in-progress` when the user starts building.

### review
Development complete, PR open.
Run `/sdlc review #N` to produce findings.
Step advances to `review` when the PR is opened.

### done
Merged and closed. Step advances on merge (via `review --merge` or `close`).

## Branch and worktree

Branch: `sdlc/#N-<slug>` (own branch — not shared with other issues)
Worktree: `worktrees/sdlc-N-<slug>/`

## Commit conventions

All commits in the worktree reference the issue:
```
<type>(<scope>): <description> (#N)
```

Examples:
```
fix(battle): resolve null crash when monster HP reaches zero (#42)
feat(scoring): add IoU partial-match bonus tier (#38)
refactor(monster): extract BattleEngine from MonsterSession (#51)
docs(scoring): document IoU formula and thresholds (#55)
chore(deps): bump Gemini SDK to 2.1.0 (#60)
```

## PR title convention

PR title matches the primary commit: `<type>(<scope>): <description> (#N)`
PR body: `Closes #N`

## Project-aware persona overrides

Scan `.claude/agents/` (see `../personas.md`):
- `fix`, `feat`, `docs`, `chore`: prefer agent mentioning `engineering`, `code`, `developer`, `iOS`, `Swift`
- `refactor`: prefer agent mentioning `architecture`, `engineering`, `design`, `structure`
