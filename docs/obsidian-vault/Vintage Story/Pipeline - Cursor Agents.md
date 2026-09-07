---
tags: [vintage-story, distant-vistas, cursor-agents]
aliases: [Agent pipeline, Cloud agents, Mods gate]
---

# Pipeline — Cursor Agents

How **Cursor cloud agents** are orchestrated for the Distant Vistas catch-up / playtest arc. Inferred from plan branches, `docs/plans/*.md`, and `CHANGELOG.md` — not a single repo file.

Related: [[MOC - Vintage Story]] · [[Login Bake Efficiency]] · [[Neglected Issues Audit]] · [[Playtest Log - 2026-09-07]]

## Flow

```mermaid
flowchart LR
  A[Speed bot] --> B[Research / SIMD expert]
  B --> C[Review bot]
  C --> D[Mods gate]
  D --> E[Human playtest]
```

| Stage | Agent role | Output |
|-------|------------|--------|
| **1. Speed bot** | Implement ranked bake wins on `cursor/1.0.26-catchup-playtest-e27c` | 1.0.31 scouts, 1.0.32 `LodBakeScratch` / ArrayPool |
| **2. Research / SIMD expert** | Read-only perf research; citations | `cursor/bake-efficiency-research-5a8d`, `docs/plans/login-bake-efficiency.md` |
| **3. Review bot** | Audit neglected playtest items | `cursor/neglected-issues-audit-1ef2`, `cursor/user-mentioned-unfixed-8587` |
| **4. Mods gate** | Human drops zip in Mods; full quit; verify checklist | `distantvistas_1.0.32.zip` |

## Branch map

| Branch | Purpose | Merge? |
|--------|---------|--------|
| `cursor/1.0.26-catchup-playtest-e27c` | Integration / release line (1.0.32) | ✅ target |
| `cursor/bake-efficiency-research-5a8d` | GetColor GC + SIMD research | ❌ **do not merge wholesale** — serial bake, wrong APIs, no 1.22.7 shim |
| `cursor/neglected-issues-audit-1ef2` | Generic playtest audit + small UX fixes | Cherry-pick / docs |
| `cursor/user-mentioned-unfixed-8587` | User-voice backlog + `HasDrawableMesh` parent fix | Cherry-pick / docs |
| `cursor/obsidian-vs-vault-pack` | This Obsidian vault | Docs only |

## Speed bot scope (1.0.31–1.0.32)

From [[Login Bake Efficiency]] — **in flight, do not duplicate on audit branches:**

- `MaxBakePerTick`, parallel 16 scouts
- Near `WaitForMesh` / far FlagBaked-release gates
- `LodBakeScratch`, texture-mean cache, list reuse
- Farseer mask debounce, partitioning throttle

**Out of scope for speed bot:** four-box Farseer rim, ValksFuzzyClouds rain, photorealistic shaders.

## SIMD expert scope

**After GetColor only** — `BlurLand` / `Quantize` vectorization.

- `Block.GetColor` cannot be SIMD'd.
- Low priority while `BlurRadius = 0`.
- Citations: [[Sources - C Sharp Performance#SIMD]]

Research lands in plan doc; implementation waits for speed-tier completion.

## Review bot scope

Produces:

- [[Neglected Issues Audit]] — tunnel-vision checklist
- [[User-Mentioned Unfixed]] — user-spoken P0/P1

Small orthogonal fixes allowed:

- Skip notification deferred region count
- `ClearMeshes()` resets `MeshPressureActive`
- Parent coverage `HasDrawableMesh`

**No game-code drive-by** on review branch unless explicitly scoped.

## Mods gate (human)

Before declaring a build ready:

1. Drop `distantvistas_<version>.zip` in Vintage Story `Mods/` — do not extract.
2. **Fully quit** Vintage Story, then restart (not relog).
3. Run verification checklist from [[Neglected Issues Audit#Verification checklist (for parent bots)]].

Typical cold-login verify:

- 16/16 scouts within seconds
- No holes underfoot at spawn
- Same pickup XYZ after overlay
- Farseer gray tent + black tips
- `scripts/check.sh fast` on dev machine with VS assemblies

## Parent bot instructions

When spawning child agents:

- **One concern per branch** — speed vs audit vs docs.
- Fetch plan branches read-only for missing docs.
- Never merge `bake-efficiency-research-5a8d` without 1.22.7 shim + parallel bake fixes.
- Update `CHANGELOG.md` + plan doc status table on speed merges.

## Related repo automation

- `scripts/check.sh fast` — ~1 s invariant harness (no CI — VS assemblies not redistributable).
- `scripts/check.sh smoke` / `matrix` — full game-process tiers (local only).

See `DESIGN.md` §12 — The test regimen.
