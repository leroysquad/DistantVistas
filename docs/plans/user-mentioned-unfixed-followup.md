# User-mentioned unfixed follow-up (this tip)

Source audit: `cursor/user-mentioned-unfixed-8587` / `docs/plans/user-mentioned-unfixed.md`. Addressed on `cursor/1.0.26-catchup-playtest-e27c` as part of 1.0.32.

| Item | Status |
|------|--------|
| Sky gap (FlagBaked/mesh lag Farseer onset → white strip) | Done — pull visit-onset uniforms to meshed rim when lag > 256; overlap 128; recede to 4.5× VD + 700 as meshes catch up. `region.fsh` unchanged. Overlay far-ready 75% gate unchanged. |
| Skip-with-gaps (in-window “complete” with holes) | Already on tip — `MaxSkipMisses` / `MaxSkipUnfilledGaps` = 32 force scout fill; below that, deferred-count skip |
| Sticky `emptyMeshKeys` as hasMesh | Done — parent coverage, gap-fill, neighbour request, too-fine flush, eviction use `HasDrawableMesh`. `HasEmptyMeshClaim` kept for scout Mesh-wait. RenderDirty prune still uses `HasAnyMesh` so empty-claim jobs are not dropped. |

Not re-opened: four-box Farseer, Valks rain, photorealistic shaders, player teleports, SIMD/bake-efficiency already on this tip.
