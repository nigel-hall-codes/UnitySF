# Workflow: techdesign

Produces a technical design or implementation spec — the how, not the what. Bridges a system design to concrete implementation issues.

## Phase sequence

```
captured → drafting → done
```

## Phase

### drafting
- State the specific technical problem being solved
- Specify the implementation approach: which files change, which APIs, what's the data model
- Define the interface contract that implementation issues must satisfy
- List edge cases and error conditions to handle
- Provide acceptance criteria that a `fix` or `feat` issue can verify against
- Note performance, security, or platform constraints

Output: `sdlc/#N/design.md`

## Persona

Default: `fullstackcoder`
Project-aware: prefer agent whose description mentions `engineering`, `iOS`, `Swift`, `mobile`, `backend`, `platform`, `implementation`

## Branch

`sdlc/#N-techdesign` — own branch (typically post-plan, child of a `sysdesign` or `idea` issue).
Usually carries `discovered-from:#K` label.

## Worktree

`worktrees/sdlc-N-<slug>/`
Design artifacts only. No code changes.

## Relationship to other workflows

A `techdesign` issue is the specification. The `fix` or `feat` issues that implement it
reference this issue's design doc as their acceptance criteria.
