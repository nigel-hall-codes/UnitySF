# plan — Fan out child issues

**Usage:** `/sdlc plan #N`

Reads the accumulated analysis from the parent issue's worktree, generates child GitHub issues, and links them. Each child starts its own lifecycle with a `discovered-from:#N` label.

## Steps

### 1. Read the parent issue

```bash
gh issue view N --json number,title,labels,body,url
```

Verify the issue is an `idea` workflow and has a `plan.md` artifact (step should be `done` or `planning`).

If `plan.md` doesn't exist:
```
No plan.md found for #N. Run /sdlc analyze #N first to complete the planning phase.
```

### 2. Read all analysis artifacts

```bash
cat worktrees/sdlc-N-<slug>/sdlc/#N/analysis.md 2>/dev/null
cat worktrees/sdlc-N-<slug>/sdlc/#N/prior-art.md 2>/dev/null
cat worktrees/sdlc-N-<slug>/sdlc/#N/plan.md
```

The `plan.md` should contain a proposed work breakdown. Parse it for child issue definitions:
- Title (conventional commit form where appropriate)
- Workflow type (`sysdesign`, `techdesign`, `fix`, `feat`, `refactor`, `docs`, `chore`)
- Persona (resolved from `../personas.md`)
- Any blockers between children (by index or title)

### 3. Present plan for confirmation

```
Child issues to create from #N: <parent-title>

  1. sysdesign: design the X system          [systems-architect]
  2. techdesign: spec the Y implementation   [fullstackcoder]
     blocked-by: 1
  3. feat: implement Z                       [fullstackcoder]
     blocked-by: 2
  4. fix: remove legacy W                    [fullstackcoder]

Create all? (yes / no / edit N)
```

Allow the user to edit individual entries before creating.

### 4. Create child issues

For each child, in order:

```bash
gh issue create \
  --title "<child-title>" \
  --label "sdlc:workflow:<type>,sdlc:<type>:captured,persona:<persona>,discovered-from:#N"
```

Capture each child issue number as it's created (`#M1`, `#M2`, …).

### 5. Apply blocker labels

For any child with a blocker relationship:
```bash
gh issue edit M_blocked \
  --add-label "blocker:#M_blocks"
```

### 6. Add spawned back-links to parent

```bash
gh issue edit N \
  --add-label "spawned:#M1,spawned:#M2,spawned:#M3"
```

### 7. Print summary

```
Fanned out #N into N child issues:

  #M1: sysdesign: design the X system          [captured]
  #M2: techdesign: spec the Y implementation   [captured] blocked-by #M1
  #M3: feat: implement Z                       [captured] blocked-by #M2
  #M4: fix: remove legacy W                    [captured]

Each child has its own branch and worktree when /sdlc work #M is called.
Parent #N remains open as the context document.
```

## Notes

- Each child post-plan gets its own branch (`sdlc/#M-<slug>`) — no more shared branch
- The parent idea issue stays open; it's the context document, not a task
- If the user runs `plan` before `analyze` has produced a `plan.md`, refuse with a clear message
- If some child issues already exist (re-running plan), detect and skip duplicates by title matching
