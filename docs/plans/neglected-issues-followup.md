# Neglected-issues follow-up (this tip)

Source audit: `cursor/neglected-issues-audit-1ef2` / `docs/plans/neglected-issues-audit.md` (that branch). Addressed on `cursor/1.0.26-catchup-playtest-e27c` as part of 1.0.32.

| Item | Status |
|------|--------|
| In-window skip claiming complete with large `FindMisses` / `LastUnfilledGaps` | Done — threshold 32: force scout fill; below that, honest deferred-count skip (including dropped Esc resume) |
| `DiscoverOnly` / `explorePending` stall after skip | Done — `MaxExplorePendingYield` 4→24; skip does not `ExploreBake.Clear()` |
| Palette no-colour `MarkChanged` storm on first load | Done — persist every repair; remesh ≤2 sections/tick (`NotePaletteRepair`) |
| Expire-recapture missing-tex white strip | Done — `RejectExpireMissingTex` / `IsMissingTextureWhite` before write |
| Uncapped `CollectExpireLeftovers` on large caches | Done — nearest 256 L0 |
| Leave-world mesh-pressure latch | Done — `ClearMeshes` resets `MeshPressureActive` |

Not re-opened (already verified on this tip): spawn-center, 75% snapshot gate, `RemeshStaleLiveTintParent`, Pause-on-Start, Esc resume, `RequestGpuSwap` mip drain, `LodVsCompat`.
