# analyze — Run the current phase analysis

**Usage:** `/sdlc analyze #N`

User-triggered. Runs generative analysis for the issue's current phase, writes and commits the artifact, presents findings, then waits for user confirmation before advancing the gate. The user says "yes" (or any affirmative); the skill handles label advancement.

## Steps

### 1. Read the issue

```bash
gh issue view N --json number,title,labels,body
```

Extract `workflow_type` and `current_step`.

### 2. Resolve the phase

| Workflow | Step | Analysis | Output file |
|---|---|---|---|
| `idea` | `captured` or `analyzing` | Problem/opportunity: what is this, why does it matter, what does success look like, what do we not know | `sdlc/#N/analysis.md` |
| `idea` | `prior-art` | Research: what exists, what's been tried, what works, what doesn't. Scan the codebase + any context the user provides | `sdlc/#N/prior-art.md` |
| `idea` | `planning` | Work breakdown: what child issues are needed, with type + persona per issue. This becomes input to `/sdlc plan #N` | `sdlc/#N/plan.md` |
| `sysdesign` `techdesign` | `captured` or `drafting` | System/tech design: components, interfaces, tradeoffs, decisions, scope | `sdlc/#N/design.md` |

If the step is `captured` on a figuring-it-out workflow, treat it as the first analysis phase (`analyzing` for idea, `drafting` for sysdesign/techdesign).

### 3. Ensure worktree exists

```bash
git worktree list --porcelain | grep "sdlc-N-"
```

If missing, create it (same branch logic as `work.md` step 3–4).
All writes happen inside the worktree at `worktrees/sdlc-N-<slug>/`.

### 4. Run the analysis

Adopt the resolved persona (read `../personas.md`). Generate the artifact content:
- Be generative and exploratory — surface what isn't obvious from the title alone
- For `analysis.md`: define the problem clearly, name assumptions, identify unknowns
- For `prior-art.md`: grep the codebase, reason about patterns, note what's reusable
- For `plan.md`: produce a concrete list of child issues (title, type, persona, any blockers between them)
- For `design.md`: draw the architecture in prose/pseudocode, name every tradeoff explicitly

Write the artifact to `worktrees/sdlc-N-<slug>/sdlc/#N/<artifact>.md`.

### 5. Commit the artifact

```bash
cd worktrees/sdlc-N-<slug>
git add sdlc/#N/
git commit -m "docs(#N): <phase> — <one-line summary>"
```

### 6. Present findings and wait

Show the artifact to the user (full if short, summary + key points if long).

Then ask:
```
Analysis committed to sdlc/#N/<artifact>.md

Advance to next step? (yes / no / revise)
```

- **yes / done / looks good / any affirmative**: proceed to step 7
- **no / stop**: stay at the current step, do not update labels
- **revise / <correction>**: revise the artifact in place, re-commit, ask again

### 7. Advance the gate (on user confirmation)

Transition map:

| Workflow | From | To |
|---|---|---|
| `idea` | `captured` | `analyzing` |
| `idea` | `analyzing` | `prior-art` |
| `idea` | `prior-art` | `planning` |
| `idea` | `planning` | `done` |
| `sysdesign` `techdesign` | `captured` | `drafting` |
| `sysdesign` `techdesign` | `drafting` | `done` |

```bash
gh issue edit N \
  --remove-label "sdlc:<workflow>:<current>" \
  --add-label "sdlc:<workflow>:<next>"
```

Print:
```
Step advanced: sdlc:<workflow>:<current> → sdlc:<workflow>:<next>
Artifact:      sdlc/#N/<artifact>.md
```

If the new step is `done` for an `idea` issue (planning phase just completed):
```
Pre-plan phases complete. Run /sdlc plan #N to generate child issues.
```

If `done` for sysdesign/techdesign:
```
Design complete. Run /sdlc close #N or hand off to implementation issues.
```
