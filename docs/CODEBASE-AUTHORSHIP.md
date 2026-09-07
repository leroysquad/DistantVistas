# Distant Vistas — Codebase Authorship (1.0.32)

Line counts from `git blame` on surviving lines in the release tree. Git records
AI-assisted commits under `Cursor Agent`; for attribution, those count as **IllLeroySquad**
directed work.

Method: `wc -l` for file inventory; `git blame` for who wrote each surviving line.

---

## Full repository

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

---

## Plain summary

| | Lines |
| --- | ---: |
| **IllLeroySquad** (including all Cursor Agent commits) | **28,852** |
| **AliasFactory** (Vintage Horizons foundation) | **18,575** |
| **Total repository** | **47,427** |

AliasFactory mod C# foundation alone: **9,134 lines** (~9,000).

---

## IllLeroySquad breakdown (git history)

| Git author | Lines |
| --- | ---: |
| IllLeroySquad / leroysquad / Private Citizen (direct) | 6,002 |
| Cursor Agent (directed — counted as IllLeroySquad) | 22,850 |
| **IllLeroySquad total** | **28,852** |

---

## C# detail

| | Mod C# | Tests | Total |
| --- | ---: | ---: | ---: |
| IllLeroySquad | 21,786 | 4,612 | 26,398 |
| AliasFactory | 9,134 | 4,234 | 13,368 |
| **Total** | **30,920** | **8,846** | **39,766** |

Largest single file: `LodTerrainRenderer.cs` at 3,458 lines.

---

## Shaders

| File | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: |
| `lodterrain.fsh` | 200 | 53 | 147 |
| `lodterrain.vsh` | 217 | 100 | 117 |
| `farseer-region.fsh` | 111 | 111 | 0 |
| `farseer-region.vsh` | 80 | 80 | 0 |
| `region.fsh` (Farseer) | 111 | 111 | 0 |
| `region.vsh` (Farseer) | 80 | 80 | 0 |
| `chunkliquid.vsh` | 139 | 0 | 139 |
| `chunkopaque.vsh` | 140 | 0 | 140 |
| `chunktopsoil.vsh` | 107 | 0 | 107 |
| `chunktransparent.vsh` | 100 | 0 | 100 |
| **Shader total** | **1,285** | **535** | **750** |

| | IllLeroySquad | AliasFactory |
| --- | ---: | ---: |
| LOD + Farseer region shaders | 535 | 264 |
| Vanilla chunk shader overrides | 0 | 486 |
| **Shader total** | **535** | **750** |

---

## Scripts

| | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: |
| Build, bench, and test scripts (`scripts/`) | 2,732 | 6 | 2,726 |

---

## Documentation

| File | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: |
| `CHANGELOG.md` | 1,050 | 690 | 360 |
| `DESIGN.md` | 938 | 0 | 938 |
| `README.md` | 266 | 47 | 219 |
| `docs/OFFICIAL-DESCRIPTION.md` | 148 | 148 | 0 |
| `docs/RELEASING.md` | 129 | 7 | 122 |
| `docs/WHAT-WE-DO.md` | 29 | 29 | 0 |
| `docs/community/distantvistas-moddb.md` | 32 | 32 | 0 |
| `docs/community/moddb-0.7.55-listing.html` | 59 | 59 | 0 |
| `docs/community/moddb-1.0.32-listing.html` | 154 | 154 | 0 |
| `docs/community/set-moddb-listing.js` | 84 | 84 | 0 |
| `docs/plans/login-bake-efficiency.md` | 85 | 85 | 0 |
| `docs/plans/neglected-issues-followup.md` | 14 | 14 | 0 |
| `docs/plans/simd-after-getcolor.md` | 162 | 162 | 0 |
| `docs/plans/user-mentioned-unfixed-followup.md` | 11 | 11 | 0 |
| `docs/season-color-audit-1.0.19.md` | 13 | 13 | 0 |
| `LICENSE` | 21 | 0 | 21 |
| **Documentation total** | **3,195** | **1,535** | **1,660** |

---

## Mod metadata

| | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: |
| `modinfo.json`, `.csproj`, config json | 148 | 77 | 71 |

---

## Canvases

| | Lines | IllLeroySquad | AliasFactory |
| --- | ---: | ---: | ---: |
| `canvases/` | 301 | 301 | 0 |

---

## Notes

- Blame totals may differ from `wc -l` by a few lines per file.
- Block-scale math (radius, end-to-end, square blocks) is in
  `docs/community/moddb-1.0.32-listing.html`.
- Official mod description: `docs/OFFICIAL-DESCRIPTION.md`.
