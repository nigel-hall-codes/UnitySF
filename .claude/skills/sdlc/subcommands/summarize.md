# summarize — Read-only issue summary

**Usage:**
- `/sdlc summarize #N` — single issue
- `/sdlc summarize #N #M #P` — specific set
- `/sdlc summarize` — all open SDLC issues

Read-only. Never modifies labels, branches, worktrees, or issues.

## Steps

### 1. Resolve the target issues

**Single or explicit list:**
```bash
gh issue view N --json number,title,labels,body,url,state
```

**All open SDLC issues:**
```bash
gh issue list --state open --limit 100 --json number,title,labels,url \
  | jq '[.[] | select(.labels | map(.name) | any(startswith("sdlc:workflow:")))]'
```

### 2. Extract fields per issue

From labels:
- `workflow_type` — from `sdlc:workflow:*`
- `current_step` — from `sdlc:<workflow>:*` (value after the workflow prefix)
- `persona` — from `persona:*`
- `blockers` — all `blocker:#*` values
- `children` — all `spawned:#*` values
- `parent` — `discovered-from:#*` value

### 3. Check worktree status

```bash
git worktree list --porcelain | grep "sdlc-N-"
```

Note which issues have an active worktree (`[active]`) vs. none (`[no worktree]`).

### 4. Print

**Single issue:**
```
#N: <title>
URL:      <url>
Workflow: <type> (<family>)
Step:     <current-step>
Persona:  <name>
Branch:   sdlc/#N-<slug> [active worktree | no worktree]
Blockers: #X, #Y  (or none)
Children: #M, #P  (or none)
Parent:   #K      (or none)

<issue body, truncated to 3 lines if long>
```

**Multiple issues — grouped by family then step:**
```
FIGURING-IT-OUT
───────────────────────────────────────────────────────
idea       #12  [analyzing]   Explore new battle mechanic
sysdesign  #15  [drafting]    Design IoU scoring system     ← from #12
techdesign #16  [captured]    Tech spec: IoU implementation ← from #12

LEAF
───────────────────────────────────────────────────────
feat       #18  [in-progress] feat: implement IoU scorer    ← from #16
fix        #20  [review]      fix: null crash on battle end
refactor   #22  [scoped]      refactor: extract BattleEngine
docs       #25  [captured]    docs: update scoring guide
```

Active worktrees are shown inline: append `*` to the issue number if a worktree exists.

**Nothing found:**
```
No open SDLC issues found. Run /sdlc capture <title> to start one.
```

## Notes

- Issues with no SDLC labels are skipped silently (they're not SDLC issues)
- Closed issues are not shown unless explicitly requested by number
- This subcommand never calls `git` mutating commands or `gh` edits
