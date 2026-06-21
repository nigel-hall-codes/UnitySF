# close — Tear down a completed issue

**Usage:** `/sdlc close #N`

Removes the worktree, deletes the local branch (if merged), closes the GitHub issue, and marks it done. Use after a successful `review --merge`, or to abandon an issue.

## Steps

### 1. Read the issue

```bash
gh issue view N --json number,title,labels,state,url
```

If already closed:
```
Issue #N is already closed. Remove any remaining worktree? (yes / no)
```

### 2. Find the worktree

```bash
git worktree list --porcelain | grep -A2 "sdlc-N-"
```

Note the worktree path and associated branch.

### 3. Check for uncommitted or unmerged work

```bash
# Check worktree for uncommitted changes
cd worktrees/sdlc-N-<slug> && git status --short

# Check if branch is fully merged into main/master
git branch -d sdlc/#N-<slug> --dry-run 2>&1 | grep -q "not fully merged" && echo "UNMERGED"
```

If uncommitted changes exist:
```
Warning: worktrees/sdlc-N-<slug>/ has uncommitted changes.
These will be lost. Continue? (yes / no)
```

If branch is unmerged:
```
Warning: sdlc/#N-<slug> has unmerged commits.
Delete branch anyway? (yes / no)
```

### 4. Confirm

```
About to close #N: <title>

  Worktree:  worktrees/sdlc-N-<slug>/  [will be deleted]
  Branch:    sdlc/#N-<slug>            [will be deleted if merged]
  Issue:     #N                        [will be closed]

Continue? (yes / no)
```

### 5. On yes — execute

```bash
# Remove worktree
git worktree remove worktrees/sdlc-N-<slug> --force

# Delete local branch (skip if unmerged and user said no above)
git branch -d sdlc/#N-<slug> 2>/dev/null \
  || git branch -D sdlc/#N-<slug> 2>/dev/null  # if user confirmed unmerged delete

# Close issue on GitHub
gh issue close N

# Advance to done
CURRENT_STEP=$(gh issue view N --json labels --jq '.labels[].name | select(test("^sdlc:[^:]+:[^:]+$") and (test("^sdlc:workflow") | not))')
WORKFLOW=$(gh issue view N --json labels --jq '.labels[].name | select(test("^sdlc:workflow:")) | split(":")[2]')
gh issue edit N \
  --remove-label "$CURRENT_STEP" \
  --add-label "sdlc:$WORKFLOW:done"
```

### 6. Print

```
Closed #N: <title>
Worktree removed: worktrees/sdlc-N-<slug>/
Branch deleted:   sdlc/#N-<slug>
Issue closed:     <url>
```

## Notes

- `review --merge` already runs this teardown. Running `close` after a merge is a no-op cleanup.
- If the worktree doesn't exist (already removed), skip the removal step silently.
- Never close a figuring-it-out parent (`idea`) that has open children — warn instead:
  ```
  #N has open child issues: #M1, #M2. Close them first, or confirm you want to abandon the parent.
  ```
