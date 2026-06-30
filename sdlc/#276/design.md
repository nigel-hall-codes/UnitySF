# Design: iPad Authoring Client — SF Building Template Pipeline

**Issue:** #276
**Phase:** drafting (sysdesign)
**Date:** 2026-06-30
**Persona:** systems-architect
**Parent:** #266 (sysdesign) · **Depends on:** the Home PC Server contract (`#266/data-model.md` §5, now built: #274/#275/#281)
**Companion:** `#266/design.md` (the two-loop architecture), `#278/design.md` (the facade-canvas mode)

---

## 1. Scope & the one hard boundary

The iPad client is the PDF's **primary authoring tool**. It is a *UI over the Home PC Server* —
nothing more. The single load-bearing constraint from #266 fixes everything else:

> **The iPad talks ONLY to the Home PC Server. It never calls an AI provider and never touches
> the Unity project.** AI generation is server-mediated; the Unity coupling is the server's
> `POST /export/unity` alone.

So this document designs the client to the depth needed to (a) fix its contract with the server
and (b) draw its internal component boundaries. Pixel-level UI, gestures, and Swift specifics are
deliberately shallow — they are leaf-issue concerns (#282 and successors), not architecture.

```
   iPad client  ──── HTTP/JSON + GLB/PNG multipart ────▶  Home PC Server  ──▶  Unity (export)
   (authoring UI)  ◀──────── lists / assets ─────────    (source of truth)      (generation)
        │                                                       │
        └───────────── NEVER talks to AI or Unity ──────────────┘ (server mediates both)
```

The server already exposes the entire surface the client needs (§5, verified): `GET
/neighborhoods` · `GET /building-types` · `GET/POST /parts` (+ `PUT /parts/{id}/glb`) ·
`GET/POST /templates` · `GET/POST /palettes` · `POST /building-specific` · `POST
/ai/signs/generate` · `GET /signs` · `POST /canvas` · `GET /canvas/{osm_id}[/{facade}]` ·
`POST /export/unity`. **No new server endpoints are required for the MVP**; gaps are noted in §8.

---

## 2. Platform decision (D1)

**Decision: SwiftUI app + RealityKit for the 3D viewport, one networking layer, a thin local
cache. Native iPad, Pencil-first.**

| Axis | Choice | Why |
|---|---|---|
| UI | **SwiftUI** | Declarative, fast to iterate, good iPad multi-column/sidebar fit. |
| 3D view | **RealityKit** (fallback SceneKit) | Renders the building mass + placed parts; Pencil hit-testing on facades. SceneKit is the fallback if RealityKit's editor ergonomics disappoint. |
| Drawing | **PencilKit** for the facade canvas strokes | Native low-latency ink; strokes export as the #278 stroke list. |
| Networking | one `ServerClient` actor over `URLSession` | Single choke point for the server contract; everything else is offline-pure. |
| Persistence | local **draft cache** (Core Data or files) | The server is the source of truth; the cache only holds in-flight edits + a read snapshot for offline browsing. |

**Rejected:** a cross-platform stack (the PDF says iPad, Pencil is central); an in-app 3D *engine*
(over-built — the client previews, Unity generates).

---

## 3. Component map

```
┌──────────────────────────────────────────────────────────────────────────┐
│ iPad Authoring Client                                                      │
│                                                                            │
│  ┌────────────┐   ┌─────────────┐   ┌──────────────┐   ┌────────────────┐ │
│  │ Building    │   │ Part        │   │ Template &   │   │ Facade Canvas  │ │
│  │ Browser     │   │ Authoring   │   │ Rule Author  │   │ (#278 mode)    │ │
│  │ (list/3D)   │   │ (GLB+roles) │   │ (compat+rules)│  │ (paint+place)  │ │
│  └─────┬───────┘   └─────┬───────┘   └──────┬───────┘   └───────┬────────┘ │
│        │                 │                  │                   │          │
│        └────────┬────────┴─────────┬────────┴─────────┬─────────┘          │
│                 ▼                  ▼                  ▼                     │
│           ┌───────────┐     ┌──────────────┐   ┌──────────────┐           │
│           │ Sign       │     │ Draft Store   │   │ ServerClient │           │
│           │ Requester  │────▶│ (local cache) │──▶│  (the ONLY   │──────────┼──▶ Server
│           │ (AI via    │     │ + sync queue  │   │   egress)    │           │
│           │  server)   │     └──────────────┘   └──────────────┘           │
└──────────────────────────────────────────────────────────────────────────┘
```

**Responsibilities (one sentence each):**

- **Building Browser** — list buildings (by neighborhood / type, the server's vocabularies), show a
  building's classification facts + a 3D mass preview; the entry point to every authoring action.
- **Part Authoring** — import a reference screenshot, trace a facade element, produce a GLB +
  assign **material roles** per submesh + `anchor`/`mountDepth` (never colours — #266 principle).
- **Template & Rule Author** — assemble a `TemplateDef`: compatibility bands (neighborhood / type /
  shape / dim / floors), Exact placements, and Procedural rules (the #271 vocabulary), referencing
  authored parts by id.
- **Facade Canvas (#278 mode)** — pick one building + one facade; freehand-paint (PencilKit
  strokes) and place images / AI signs; saved as the layered canvas document.
- **Sign Requester** — a form (business type, neighborhood, text, aspect, style) → `POST
  /ai/signs/generate`; the server returns a reusable PNG asset. The iPad shows the result; **it
  never sees an AI provider**.
- **Draft Store + sync queue** — local snapshot of server lists for offline browsing, plus an
  outbox of pending authoring writes.
- **ServerClient** — the *only* component that performs I/O off-device; encodes the §5 contract.

---

## 4. The client ↔ server contract (data flows)

Every authored object maps 1:1 onto a server resource. The client holds **no authority** — it
reads, edits a draft, and POSTs; the server validates and persists.

| Authoring action | Reads | Writes |
|---|---|---|
| Browse buildings | `GET /neighborhoods`, `GET /building-types`; building facts come from the **bake sidecar** the server can surface (see §8 gap G1) | — |
| Author a part | `GET /parts` | `POST /parts` (PartDef JSON) → `PUT /parts/{id}/glb` (the traced GLB) |
| Author a template | `GET /parts`, `GET /templates` | `POST /templates` (TemplateDef) |
| Author a palette | `GET /palettes`, `GET /neighborhoods` | `POST /palettes` |
| Request a sign | — | `POST /ai/signs/generate` → asset stored server-side; `GET /signs` to list |
| Building-specific override | — | `POST /building-specific` |
| Facade canvas | `GET /canvas/{osm_id}[/{facade}]` | `POST /canvas` (the layered doc) |
| Publish to Unity | — | `POST /export/unity` (operator-triggered; see D4) |

**Wire shapes are the server's** (`data-model.md §2`, incl. the JsonUtility-friendly array-of-pairs
form for `roleSubmeshes` / `exact[].roles` / palette `roles`). The client encodes/decodes those
shapes verbatim — it does **not** invent its own; a drift here silently breaks the Unity import.

### Facade frame is inherited, not reinvented (D2)

The canvas places images in the building's **facade UV** frame: `x ∈ [0,1]` left→right along the
real facade width, `y ∈ [0,1]` bottom→top along `floorHeight × floors`. This is the **same frame**
the bake emits (`street_facades[].edge`, `base_y`, `facade_height_m`, #279) and the Unity decal
importer (#280) consumes. The iPad gets the frame from the server (which has the sidecar) and
stores only normalized coords + the `footprint_hash` guard — never world space. So an image placed
at centre on iPad lands at centre on the wall, at any real building size.

---

## 5. State & sync model (D3)

**Decision: server-authoritative, client-optimistic, last-write-wins per object, with an explicit
sync queue. No multi-user merge in v1 (single author, single home server).**

```
edit ──▶ Draft Store (local, immediately visible) ──▶ enqueue write ──▶ ServerClient POST
                                                          │
                          on success: reconcile id/version from server response
                          on failure: keep in outbox, retry; surface a sync badge
```

- **Reads** are snapshotted so the browser works offline (on the home LAN the iPad may sleep/roam).
- **Writes** are queued; the UI shows optimistic state and a per-object sync status. Because the
  server keys by stable ids (`part.id`, `template.id`, `neighborhood`, `osm_id:facade`), a retry is
  an idempotent upsert — safe to replay.
- **Conflict policy:** last-write-wins. Justified because there is one author and one server; the
  PDF describes a personal pipeline, not a team. Revisit only if multi-device authoring appears.
- **Binary assets** (GLB, reference screenshots) upload separately (`PUT /parts/{id}/glb`),
  after the metadata POST, so a failed binary doesn't orphan a half-written part.

---

## 6. The facade canvas mode in detail (consumes #278/#281)

The canvas is a **mode inside this client**, not a separate app (#278 §dependencies). Flow:

```
1. Browser: select building → select a facade (from street_facades[] + derivable Back/Left/Right)
2. Canvas surface = the facade's unit square (PencilKit + an image layer stack)
   • paint:  Pencil strokes  → stroke layers (points, colour, width)
   • place:  an image / AI sign → an image layer (rect, texture/signAsset ref, mountDepth)
3. Save  → POST /canvas (the layered FacadeCanvas doc; footprint_hash carried from the sidecar)
4. Publish (operator) → POST /export/unity: server flattens strokes → paint_<id>_<facade>.png,
   keeps placed images discrete, writes facadeDecals[] into Overrides/<osm_id>.override.json (#281)
```

The client stores the **fully layered** document; the *flattening* (strokes → one PNG) happens
**server-side on export** (#281), so the iPad keeps re-editable layers while Unity gets the cheap
flattened form. The iPad never rasterizes for Unity and never writes the override file directly.

---

## 7. Tradeoffs & decisions (record)

- **D1 — SwiftUI + RealityKit, native iPad.** (§2) Pencil-first authoring + 3D preview; not a
  cross-platform or in-app-engine build.
- **D2 — Inherit the bake's facade frame; store only normalized coords + footprint_hash.** (§4)
  Guarantees iPad-authored placements match Unity without the iPad knowing world space.
- **D3 — Server-authoritative, optimistic writes, LWW, no merge.** (§5) Right-sized for a
  single-author personal pipeline; idempotent upserts make retries safe.
- **D4 — `POST /export/unity` is an explicit operator action, not automatic on every save.** The
  export materializes a repo asset drop and (eventually) feeds a Unity import; it should be a
  deliberate "publish", not a side effect of authoring. The iPad surfaces a Publish button; the
  bake/import on the Unity side stays a separate manual step (the two-loop boundary, #266).
- **D5 — Material *roles* only, never colours, at authoring time.** (§3) The iPad assigns roles;
  Unity resolves colours from neighborhood palettes (#272). The part authoring UI must not even
  offer a colour picker for role-bearing submeshes — only a role picker.
- **D6 — Reference screenshots are authoring aids, not pipeline assets.** Imported screenshots used
  to trace a part/canvas stay on the iPad (or an optional server scratch area); only the resulting
  GLB / canvas crosses the contract. Keeps the asset library clean.

---

## 8. Gaps in the current server contract (for follow-up issues)

The MVP authoring loop is mostly served by the existing endpoints, but three reads are missing and
should become small server issues (they do not block this design):

- **G1 — Building list / facts read.** The iPad needs to browse buildings and see each one's
  classification facts (neighborhood, shape, dims, `street_facades`, `footprint_hash`) to drive
  browsing + the facade frame. Today those live only in the bake's per-chunk sidecar. *Proposed:*
  a server endpoint that ingests/serves the sidecar facts (e.g. `POST /buildings/import-sidecar`
  + `GET /buildings?neighborhood=…`, `GET /buildings/{osm_id}`). Until then, the iPad can side-load
  a sidecar file for the MVP.
- **G2 — Asset binary GET.** `GET /parts/{id}/glb` and a sign-PNG GET so the iPad can preview
  authored geometry/signs it didn't just upload. (POST/PUT exist; the GET counterparts don't.)
- **G3 — List/patch canvases across buildings** for a "what have I decorated?" overview
  (`GET /canvas` all) — minor.

---

## 9. Out of scope (this design)

- Swift/SwiftUI implementation, screen layouts, gesture details — leaf issue #282 (canvas mode
  first) and successors.
- Auth / multi-user / cloud sync — single author on a home LAN; revisit only if needed.
- On-device AI or on-device Unity preview — forbidden by the boundary (§1).
- The glTF authoring/export internals (how a traced 2D outline becomes a GLB) — a deep sub-problem;
  this design only fixes that a part **is** a GLB + roles crossing to the server.
- Perspective correction / auto-UV / facade recognition — PDF Phase 2/3, explicitly later.

---

## 10. MVP slice (smallest end-to-end authoring loop)

Mirrors #266's MVP from the iPad side, exercising every component once:

1. **Browse** → pick a building (side-loaded sidecar facts for now, G1).
2. **Author one window part**: trace → GLB + roles (`POST /parts`, `PUT /parts/{id}/glb`).
3. **Author a trivial template**: "any building, one window, Front, x=0.5, Exact"
   (`POST /templates`).
4. **Facade canvas**: pick the building's Front facade, draw one stroke + place one image
   (`POST /canvas`).
5. **Request one AI sign** (`POST /ai/signs/generate`) and place it on the canvas.
6. **Publish** (`POST /export/unity`) — then the existing Unity import (#269/#280) dresses the
   building. The iPad's job ends at the server; the round-trip is closed by the already-built
   generation loop.

Everything else (rule authoring depth, the full part toolset, perspective correction, neighborhood
template sets) layers on top of this slice as follow-up leaf issues — #282 (facade canvas mode) is
the first and is already captured.
