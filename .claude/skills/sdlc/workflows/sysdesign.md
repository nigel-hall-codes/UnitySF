# Workflow: sysdesign

Produces a system design document — architecture, component boundaries, data flows, interface contracts. No code changes.

## Phase sequence

```
captured → drafting → done
```

## Phase

### drafting
- Define the system being designed and its boundaries
- Describe component responsibilities and how they communicate
- Specify data flows and state transitions
- Name every significant tradeoff and the decision made
- Identify what is deliberately out of scope
- Include diagrams in prose/ASCII where helpful

Output: `sdlc/#N/design.md`

## Persona

Default: `systems-architect`
Project-aware: prefer agent whose description mentions `systems`, `architecture`, `design`, `mechanic`, `balance`, `structure`

## Branch

`sdlc/#N-sysdesign` — own branch (typically post-plan, child of an `idea` issue).
Usually carries `discovered-from:#K` label pointing to the parent idea.

## Worktree

`worktrees/sdlc-N-<slug>/`
Design artifacts only. No code changes.

## Relationship to other workflows

`sysdesign` typically precedes `techdesign` issues that implement its components.
A `sysdesign` issue spawned from an `idea` should produce `techdesign` children via `/sdlc plan #N`
if it is complex enough to need further decomposition.
