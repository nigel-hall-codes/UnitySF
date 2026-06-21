# capture — Create and classify a GitHub issue

**Usage:** `/sdlc capture <title>`

## Steps

### 1. Ensure labels exist

Check whether SDLC labels have been created in this repo. If not, offer to create them:
```bash
gh label list --json name | jq -r '.[].name' | grep -q "sdlc:workflow" || echo "LABELS_MISSING"
```

If missing, ask the user: "SDLC labels don't exist yet. Create them now? (yes/no)"

On yes:
```bash
# Workflow types
for type in idea sysdesign techdesign fix feat refactor docs chore; do
  gh label create "sdlc:workflow:$type" --color "0075ca" --description "SDLC workflow type" 2>/dev/null || true
done

# Step labels per workflow
for step in captured analyzing prior-art planning done; do
  gh label create "sdlc:idea:$step" --color "e4e669" 2>/dev/null || true
done
for wf in sysdesign techdesign; do
  for step in captured drafting done; do
    gh label create "sdlc:$wf:$step" --color "e4e669" 2>/dev/null || true
  done
done
for wf in fix feat refactor docs chore; do
  for step in captured scoped in-progress review done; do
    gh label create "sdlc:$wf:$step" --color "e4e669" 2>/dev/null || true
  done
done
```

### 2. Classify the workflow

Infer the workflow type from the title and any context the user provides:

| Signal | Workflow |
|---|---|
| `fix:` prefix, "broken", "crash", "bug", "error", "wrong" | `fix` |
| `feat:` prefix, "add", "new", "implement", "support for" | `feat` |
| `refactor:` prefix, "clean up", "restructure", "reorganize", "simplify" | `refactor` |
| `docs:` prefix, "document", "README", "update docs" | `docs` |
| `chore:` prefix, "bump", "upgrade", "ci", "dependencies", "tooling" | `chore` |
| "how should we", "what if we", "explore", "idea:", question-shaped | `idea` |
| "design the", "architecture for", "system for", "how does X fit into" | `sysdesign` |
| "tech spec", "how to implement", "approach for", "spike:", "spec:" | `techdesign` |

Show classification + one-line reasoning: `"Classified as: feat (title starts with 'implement')"`.

If ambiguous, show top two candidates and ask the user to choose.

### 3. Resolve persona

Read `../personas.md`. Resolve the persona for this workflow type.
Show: `Persona: <name> (from <source>)` where source is "project agent" or "default".

### 4. Confirm

Display the proposed issue summary:
```
Title:    <title>
Workflow: <type> (<family>)
Step:     captured
Persona:  <name>

Create? (yes / no / correct me)
```

On correction: re-classify and confirm again. Never create without explicit yes.

### 5. Create the issue

For `idea` issues: ask "Add a brief description of what you're exploring?" before creating. Use the answer as the issue body.

```bash
gh issue create \
  --title "<title>" \
  --body "<description-if-any>" \
  --label "sdlc:workflow:<type>,sdlc:<type>:captured,persona:<name>"
```

Note the issue number from the output.

### 6. Print summary

```
Created #N: <title>
Workflow:   <type> (<family>)
Step:       captured
Persona:    <name>
URL:        <gh-url>

Next: /sdlc work #N
```
