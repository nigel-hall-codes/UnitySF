# SDLC Label Schema

Three orthogonal dimensions + two link types. All in the `sdlc:` namespace except `persona:`.

## Dimension 1 — Workflow type
`sdlc:workflow:<name>`

| Value | Family |
|---|---|
| `idea` | Figuring-it-out |
| `sysdesign` | Figuring-it-out |
| `techdesign` | Figuring-it-out |
| `fix` | Leaf |
| `feat` | Leaf |
| `refactor` | Leaf |
| `docs` | Leaf |
| `chore` | Leaf |

## Dimension 2 — Current step
`sdlc:<workflow>:<step>`

| Workflow | Steps (ordered) |
|---|---|
| `idea` | `captured` → `analyzing` → `prior-art` → `planning` → `done` |
| `sysdesign` | `captured` → `drafting` → `done` |
| `techdesign` | `captured` → `drafting` → `done` |
| `fix` `feat` `refactor` `docs` `chore` | `captured` → `scoped` → `in-progress` → `review` → `done` |

Examples: `sdlc:idea:analyzing`, `sdlc:fix:in-progress`, `sdlc:refactor:review`

## Dimension 3 — Persona
`persona:<name>`

Default values (full routing logic in `personas.md`):

| Workflow | Default persona |
|---|---|
| `idea` | `product-owner` |
| `sysdesign` | `systems-architect` |
| `techdesign` | `fullstackcoder` |
| `fix` `feat` `docs` `chore` | `fullstackcoder` |
| `refactor` | `techdesigner` |

## Link type 1 — Provenance
`discovered-from:#N`

Applied to child issues created by `plan`. Points to the parent `idea` issue.
The parent gets a corresponding `spawned:#M` label for each child.

## Link type 2 — Dependency
`blocker:#N`

Applied to an issue that cannot proceed until issue #N is resolved.
The blocking issue does NOT get a back-link — use `summarize` to see what an issue blocks.

## Creating labels in a new repo

If labels don't exist yet:
```bash
# Workflow type labels (create once per project)
for type in idea sysdesign techdesign fix feat refactor docs chore; do
  gh label create "sdlc:workflow:$type" --color 0075ca 2>/dev/null || true
done

# Step labels
for step in captured analyzing prior-art planning drafting scoped in-progress review done; do
  gh label create "sdlc:idea:$step" --color e4e669 2>/dev/null || true
done
# ... (run capture.md's label-init block for the full set)
```
