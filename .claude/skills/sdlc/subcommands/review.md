# review — Produce findings, optionally merge

**Usage:** `/sdlc review #N [--merge]`

Loads the PR linked to the issue, adopts a reviewer persona (always fresh eyes), produces structured findings, and optionally merges and tears down.

## Steps

### 1. Read the issue

```bash
gh issue view N --json number,title,labels,body,url
```

Extract `workflow_type` and `current_step`. Step should be `review` for leaf issues.

### 2. Find the PR

```bash
# Try by branch name first
BRANCH="sdlc/#N-$(echo '<slug>' | head -c 40)"
gh pr list --head "$BRANCH" --json number,title,url,state,mergeStateStatus

# Fallback: search by issue reference in PR body
gh pr list --search "Closes #N in:body" --json number,title,url,state
```

If no PR found:
```
No PR found for #N. Push the branch and open one first:

  cd worktrees/sdlc-N-<slug>
  git push -u origin sdlc/#N-<slug>
  gh pr create --title "<title> (#N)" --body "Closes #N"
```

### 3. Load reviewer persona

Read `../personas.md` — always use the `code-reviewer` persona.
Scan `.claude/agents/` for an agent mentioning `review`, `quality`, `audit`, or `test` — prefer it if found.

### 4. Read the diff

```bash
gh pr view PR_NUM --json title,body,commits,changedFiles,additions,deletions
gh pr diff PR_NUM
```

Also read the issue body and any linked figuring-it-out artifacts (`sdlc/#N/design.md`, etc.) to understand what was intended.

### 5. Produce findings

Review in priority order:

#### Correctness (highest priority)
Real bugs, logic errors, null/error paths not handled, off-by-one, race conditions, unsafe patterns, data loss risk.
Label: `[CRITICAL]` blocks merge. `[WARN]` advisory.

#### Scope alignment
Does the code match what the issue asked for? Over-built (introduced unrequested scope)? Under-built (issue isn't fully addressed)?
Label: `[OUT-OF-SCOPE]` or `[MISSING]`.

#### Clarity
Naming, structure, unnecessary complexity — only flag when it actively obscures meaning or will cause bugs later. Not style.
Label: `[CLARITY]`.

Output format:
```
## Review: #N <title> (PR #PR_NUM)

### Correctness
- [CRITICAL] <file>:<line> — <finding>
- [WARN] <file>:<line> — <finding>
(or "Nothing critical found.")

### Scope
- [MISSING] <finding>
- [OUT-OF-SCOPE] <finding>
(or "LGTM — matches issue scope.")

### Clarity
- [CLARITY] <finding>
(or "LGTM")

### Verdict
APPROVE | REQUEST CHANGES | NEEDS DISCUSSION

Reason: <one line>
```

### 6. Handle `--merge`

Only proceed if verdict is `APPROVE` and no `[CRITICAL]` findings remain.

Ask:
```
Merge PR #PR_NUM and close issue #N? (yes / no)
```

On yes:
```bash
gh pr merge PR_NUM --squash --delete-branch --subject "<title> (#N)"
gh issue close N --comment "Resolved in PR #PR_NUM."
gh issue edit N \
  --remove-label "sdlc:<workflow>:review" \
  --add-label "sdlc:<workflow>:done"

# Remove worktree
git worktree remove worktrees/sdlc-N-<slug> --force
git branch -d sdlc/#N-<slug> 2>/dev/null || true
```

Print:
```
Merged PR #PR_NUM, closed #N.
Worktree worktrees/sdlc-N-<slug>/ removed.
```

### 7. Without `--merge`

Present findings and stop. User decides what to do next.
If the verdict is APPROVE: "Run /sdlc review #N --merge to merge and close."
If REQUEST CHANGES: "Address the findings and re-run /sdlc review #N."
