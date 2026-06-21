# work — Open a work session

**Usage:** `/sdlc work #N`

## Steps

### 1. Read the issue

```bash
gh issue view N --json number,title,labels,body,url
```

Extract:
- `workflow_type` from `sdlc:workflow:*` label
- `current_step` from `sdlc:<workflow>:*` label
- `persona_name` from `persona:*` label

### 2. Resolve the persona

Read `../personas.md`. Adopt the resolved persona for this session.
If the persona label is stale or missing, resolve fresh and update the label.

### 3. Determine branch and worktree

Slug = title → lowercase → replace spaces/special chars with `-` → max 40 chars.

| Condition | Branch | Worktree |
|---|---|---|
| Figuring-it-out (any phase) | `sdlc/#N-<workflow>` | `worktrees/sdlc-N-<slug>/` |
| Leaf workflow | `sdlc/#N-<slug>` | `worktrees/sdlc-N-<slug>/` |

Figuring-it-out issues share one branch for all their pre-plan phases.

### 4. Create or attach the worktree

```bash
git worktree list --porcelain
```

**If worktree already exists at expected path:** enter it directly.

**If branch exists but no worktree:**
```bash
git worktree add worktrees/sdlc-N-<slug> sdlc/#N-<branch-suffix>
```

**If branch is new:**
```bash
git worktree add -b sdlc/#N-<branch-suffix> worktrees/sdlc-N-<slug>
```

### 5. Confirm scope

Display:
```
Issue #N: <title>
Workflow: <type> | Step: <current-step> | Persona: <persona>
Branch:   <branch>
Worktree: worktrees/sdlc-N-<slug>/

Description:
<issue body, or "(none)" if empty>

Confirm scope? (yes / no)
```

On no: ask what to change. Do not proceed until confirmed.

### 6. Advance step and enter session

**Figuring-it-out (idea, sysdesign, techdesign):**
- If step is `captured`: advance to the first analysis step (`analyzing` for idea, `drafting` for sysdesign/techdesign)
  ```bash
  gh issue edit N \
    --remove-label "sdlc:<workflow>:captured" \
    --add-label "sdlc:<workflow>:<first-step>"
  ```
- Artifacts go in `sdlc/#N/` within the worktree — no code changes
- Work here is exploratory: thinking, writing, discussing
- Run `/sdlc analyze #N` to formalize a phase output when ready

**Leaf (fix, feat, refactor, docs, chore):**
- If step is `captured`: advance to `scoped`
- If step is `scoped`: advance to `in-progress` when the user starts building
- All work happens inside the worktree
- Commit convention: `<type>(<scope>): <description> (#N)`
- When done: advance step to `review`, open a PR, tell user to run `/sdlc review #N`

```bash
# Advance leaf to in-progress when work begins
gh issue edit N \
  --remove-label "sdlc:<workflow>:scoped" \
  --add-label "sdlc:<workflow>:in-progress"
```

### 7. PR instructions (leaf, when work is complete)

```bash
cd worktrees/sdlc-N-<slug>
git push -u origin sdlc/#N-<slug>
gh pr create \
  --title "<conventional-commit-title> (#N)" \
  --body "Closes #N" \
  --label "sdlc:workflow:<type>"
```

Then:
```bash
gh issue edit N \
  --remove-label "sdlc:<workflow>:in-progress" \
  --add-label "sdlc:<workflow>:review"
```

Tell user: "PR opened. Run /sdlc review #N when ready."
