# Grok Handoff — Distant Vistas 1.0.32 (all numbers + save instructions)

**For Grokbot:** Save this file locally. User save path:

`C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\docs\GROK-HANDOFF.md`

Also update sibling files in that folder:

- `CODEBASE-AUTHORSHIP.md`
- `OFFICIAL-DESCRIPTION.md`
- `ARCHITECTURE-AND-OVERHAULS.md`

Optional Playwright task: open ModDB listing editor at
https://mods.vintagestory.at/distantvistas — user will log in through Grok VRM.

---

## Message to paste to Grokbot

```
Grok — save the markdown below to:
C:\Users\Private Citizen\AppData\Roaming\VintagestoryData\ClientMods\distantvistas\docs\GROK-HANDOFF.md

Split into separate files if you want:
- CODEBASE-AUTHORSHIP.md (authorship tables only)
- OFFICIAL-DESCRIPTION.md (mod description)
- ARCHITECTURE-AND-OVERHAULS.md (architecture — ask Cursor for full text or pull from GitHub branch cursor/official-description-6148)

If Playwright is available on my PC: open https://mods.vintagestory.at/distantvistas edit page.
I will log in through your VRM. Do not submit until I confirm. Paste OFFICIAL-DESCRIPTION + numbers into the listing when ready.
```

---

# Distant Vistas — Complete Numbers (Official 1.0.32)

Release: **1.0.32** · Vintage Story **1.22.5–1.22.7** · Author **IllLeroySquad**
· Fork **Vintage Horizons** (AliasFactory, MIT) · GitHub https://github.com/leroysquad/DistantVistas

---

## Authorship (git blame — Cursor Agent counts as IllLeroySquad)

### Plain summary

| | Lines |
| --- | ---: |
| **IllLeroySquad** (all Cursor Agent commits = directed by user) | **28,852** |
| **AliasFactory** (Vintage Horizons foundation) | **18,575** |
| **Total repository** | **47,427** |

AliasFactory **mod C# foundation only: 9,134 lines** (~9,000).

### Git author breakdown (IllLeroySquad)

| Git author | Lines |
| --- | ---: |
| IllLeroySquad / leroysquad / Private Citizen (direct) | 6,002 |
| Cursor Agent (directed — user's AI sessions) | 22,850 |
| **IllLeroySquad total** | **28,852** |

### Full repository by category

| Category | Files | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: | ---: |
| Mod C# | 89 | 30,920 | 21,786 | 9,134 |
| Tests (C#) | 34 | 8,846 | 4,612 | 4,234 |
| Shaders (GLSL) | 10 | 1,285 | 535 | 750 |
| Scripts | 16 | 2,732 | 6 | 2,726 |
| Documentation | 16 | 3,195 | 1,535 | 1,660 |
| Mod metadata (json, csproj) | 4 | 148 | 77 | 71 |
| Canvases | 2 | 301 | 301 | 0 |
| **Total** | **171** | **47,427** | **28,852** | **18,575** |

### C# only

| | Mod C# | Tests | Total |
| --- | ---: | ---: | ---: |
| IllLeroySquad | 21,786 | 4,612 | 26,398 |
| AliasFactory | 9,134 | 4,234 | 13,368 |
| **Total** | **30,920** | **8,846** | **39,766** |

`wc -l` inventory (release tree): mod **30,917** · tests **8,846** · C# total **39,763** · shaders **1,282** · scripts **2,732**.

### C# by subsystem (mod source)

| Area | Lines |
| --- | ---: |
| Render | 18,995 |
| Lod core | 4,529 |
| Net | 3,800 |
| Storage | 866 |
| Entry / config | 2,727 |

Largest file: **`LodTerrainRenderer.cs` — 3,458 lines**.

### Shaders (per file)

| File | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: |
| lodterrain.fsh | 200 | 53 | 147 |
| lodterrain.vsh | 217 | 100 | 117 |
| farseer-region.fsh | 111 | 111 | 0 |
| farseer-region.vsh | 80 | 80 | 0 |
| region.fsh | 111 | 111 | 0 |
| region.vsh | 80 | 80 | 0 |
| chunkliquid.vsh | 139 | 0 | 139 |
| chunkopaque.vsh | 140 | 0 | 140 |
| chunktopsoil.vsh | 107 | 0 | 107 |
| chunktransparent.vsh | 100 | 0 | 100 |
| **Total** | **1,285** | **535** | **750** |

### Scripts

| | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: |
| scripts/ | 2,732 | 6 | 2,726 |

### Documentation (per file)

| File | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: |
| CHANGELOG.md | 1,050 | 690 | 360 |
| DESIGN.md | 938 | 0 | 938 |
| README.md | 266 | 47 | 219 |
| docs/OFFICIAL-DESCRIPTION.md | 148 | 148 | 0 |
| docs/RELEASING.md | 129 | 7 | 122 |
| docs/WHAT-WE-DO.md | 29 | 29 | 0 |
| docs/community/* + plans + audits | 664 | 643 | 21 |
| LICENSE | 21 | 0 | 21 |
| **Total** | **3,195** | **1,535** | **1,660** |

### Git commits by author

| Author | Commits |
| --- | ---: |
| AliasFactory | 114 |
| Cursor Agent | 103 |
| IllLeroySquad | 8 |
| Private Citizen | 5 |
| leroysquad | 1 |
| **Total** | **227** |

---

## World scale / login bake math (1.0.32)

**Units:** 1 block ≈ 1 meter · 1 block² = 1 m² flat ground
· 1 mile = **1,609.344 blocks** · 1 sq mi = **2,589,988 block²**

**Login bake disk formula:** `4.5 × 750 + 700 = 4,075 blocks` radius

| Measure | Value |
| --- | ---: |
| Radius | 4,075 blocks (2.53 mi) |
| End to end (diameter) | 8,150 blocks (5.06 mi) |
| Square blocks inside circle (πr²) | 52,168,110 block² (20.1 sq mi) |
| L0 cell size | 64 × 64 = 4,096 block² |
| Estimated L0 cells in disk | 12,868 |
| Scout visit budget | 1,680 stops max |
| Parallel scouts | 16 |
| MaxBakePerTick | 24 |
| Spawn solid zone radius | 1,024 blocks (2,048 end to end) |
| Login graphics hold | 750 blocks |
| `.dvfar` max cap | 262,144 blocks (524,288 end to end) |

**Old probe disks (not LOC — block radius in code):**

| Version | Radius | End to end | block² inside |
| --- | ---: | ---: | ---: |
| 1.0.1 | 36,000 | 72,000 | 4,071,504,079 |
| 1.0.2 | 72,000 | 144,000 | 16,286,016,316 |
| 1.0.3 | 144,000 | 288,000 | 65,144,065,265 |
| 1.0.29+ | 4,075 | 8,150 | 52,168,110 |

---

## Architecture scale (what the numbers represent)

| Item | Count |
| --- | ---: |
| Mod C# files | 89 |
| Test C# files | 34 |
| GLSL shader files | 10 |
| Build/test scripts | 16 |
| Login bake subsystem files | 30+ (LodLogin* / scout / overlay) |
| Automated test lines | 8,846 |
| No CI (game DLLs not redistributable) | local `scripts/check.sh` only |

**Major overhauls from Horizons foundation (~9k mod C#):**

- Parent plates removed · visited land retention · scout login (no hop teleport)
- 4,075-block Farseer onset disk (replaced 36k–144k probes)
- Live season + night ambient + GetColor bake path
- Vanilla chunk shader patches (4 files) · ocean seabed seal
- Farseer compositing + shader overlay · optional server assist (M7)
- Capture pipeline hardening · 8,846 lines of local tests

---

## modinfo.json one-liner

Official 1.0.32. 47,427 lines across 171 files: 28,852 IllLeroySquad, 18,575 AliasFactory (9,134 mod C# foundation). Client-side far terrain with persistent LOD cache, extended render distance, live seasonal colour, and spawn-centered login bake.

---

## Playwright checklist (Grok on user's PC)

1. Launch browser (user logs in via Grok VRM if needed).
2. Navigate: https://mods.vintagestory.at/distantvistas
3. Open edit / description field (after login).
4. Paste content from `OFFICIAL-DESCRIPTION.md` + authorship summary table.
5. **Wait for user confirm before save/submit.**
6. Save local markdown copies to `ClientMods\distantvistas\docs\`.

---

## Source repo paths (GitHub branch `cursor/official-description-6148`)

- `docs/GROK-HANDOFF.md` (this file)
- `docs/CODEBASE-AUTHORSHIP.md`
- `docs/OFFICIAL-DESCRIPTION.md`
- `docs/ARCHITECTURE-AND-OVERHAULS.md`
- `docs/community/moddb-1.0.32-listing.html` (HTML listing + full math)
