# Workflow: idea

Explores a problem or opportunity before any code is written. Four sequential phases, all on one shared branch.

## Phase sequence

```
captured → analyzing → prior-art → planning → done
```

All phases share branch `sdlc/#N-idea`. Artifacts commit to `sdlc/#N/` in the worktree.
After `done`, run `/sdlc plan #N` to generate child issues — that's when each child gets its own branch.

## Phases

### analyzing
Understand the problem or opportunity. Define it precisely.
- What is this, exactly?
- Why does it matter? What goes wrong without it?
- What does success look like?
- What do we know? What don't we know?
- What are the key risks or dependencies?

Output: `sdlc/#N/analysis.md`

### prior-art
Research existing solutions, patterns, and precedents.
- What already exists in the codebase that's related?
- What patterns or abstractions are reusable?
- What external precedents (other products, open source) are relevant?
- What has been tried before in this project and why did it fail or succeed?

Output: `sdlc/#N/prior-art.md`

### planning
Define the work breakdown. This becomes the input to `/sdlc plan #N`.

For each piece of work:
- Title (conventional commit form where appropriate)
- Workflow type (`sysdesign`, `techdesign`, `fix`, `feat`, `refactor`, `docs`, `chore`)
- Persona
- Any blockers between items (explicit ordering)

Output: `sdlc/#N/plan.md`

## Persona

Default: `product-owner`
Project-aware: prefer agent whose description mentions `product`, `vision`, `creative`, `director`, `world`, `narrative`

## Branch

`sdlc/#N-idea` — shared across all four phases.
The parent issue stays open after `plan` fans out. It is the context document, not a task.

## Worktree

`worktrees/sdlc-N-<slug>/`
Analysis artifacts only. No code changes.
