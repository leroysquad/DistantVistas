---
tags: [vintage-story, distant-vistas, performance, sources]
aliases: [C# performance sources, MSR citations, ArrayPool docs]
---

# Sources — C Sharp Performance

First-hand citations used in [[Login Bake Efficiency]] and the bake research branch. URLs are stable Microsoft Learn / research links.

Related: [[Login Bake Efficiency]] · [[Pipeline - Cursor Agents]] · [[Distant Vistas]]

## ArrayPool

**Microsoft Learn — `ArrayPool<T>`**  
https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1

- Rent/return fixed-size arrays from a shared pool.
- Reduces GC pressure from frequent `new T[n]` in hot paths (e.g. 4096-column GetColor pass).

**Adam Sitnik — Pooling large arrays with ArrayPool (2018)**  
https://adamsitnik.com/Array-Pool/

- Practical patterns: always `Return` rented arrays; prefer `ArrayPool<byte>.Shared`.
- Context for login-bake column scratch buffers.

**DV use:** `LodBakeScratch`, `LodSurfaceMix.Rent`, `LodMesher.RentCopy`.

## Span

**Microsoft Learn — `Span<T>`**  
https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-span%7Bt%7D

- Stack-friendly views over arrays without allocating subarrays.
- Pairs with ArrayPool for zero-copy slices over rented buffers.

**DV use:** column iteration over pooled scratch in bake path (1.0.32).

## TPL

**Leijen et al. — The design of a Task Parallel Library (OOPSLA 2009)**  
https://www.microsoft.com/en-us/research/wp-content/uploads/2009/09/TheDesignOfATaskParallelLibraryoopsla2009.pdf

- Work-stealing task scheduler design.
- **Relevance:** explains why parallelizing `Block.GetColor` on the main thread is wrong target — mesh/mip/persist benefit from worker pool; GetColor does not.

**DV architecture:** unified worker pool in `DESIGN.md` §7 — ingest ≫ save ≫ mesh ≫ mip-propagate.

## Large object heap

**Microsoft Learn — Large object heap**  
https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap

- Objects ≥ 85 KB go to LOH; collected less frequently; fragmentation risk.
- Mesh GPU buffers and large vertex arrays are LOH candidates.

**DV implication:** pool mesher paths; avoid per-tick large allocations during overlay ([[Playtest Log - 2026-09-07]] ~7 GB managed growth).

## CLR 4.0 GC

**Maoni Stephens — So what's new in the CLR 4.0 GC?**  
https://devblogs.microsoft.com/dotnet/so-whats-new-in-the-clr-4-0-gc/

- Background GC reduces full blocking collections.
- Context for SOH churn from GetColor `BlockPos` / `List` growth vs LOH mesh traffic.

## SIMD

**Not yet applied in DV** — candidate stage **after** GetColor sampling.

- `BlurLand` / `Quantize` in bake pipeline could use `Vector<T>` / hardware intrinsics.
- `Block.GetColor` / `GetColorWithoutTint` cannot be vectorized (game API per-column).

**Priority:** low while `BlurRadius = 0` ([[Login Bake Efficiency#Technique 4 — SIMD after GetColor (not done)]])

**Further reading (general):**

- Microsoft Learn — `System.Numerics.Vector`  
  https://learn.microsoft.com/en-us/dotnet/api/system.numerics.vector
- Microsoft Learn — Hardware intrinsics namespace  
  https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics

Owned by [[Pipeline - Cursor Agents|SIMD expert agent]] — not speed-bot scope.

## How citations map to 1.0.32

| Citation | DV change |
|----------|-----------|
| ArrayPool + Sitnik | `LodBakeScratch` column arrays |
| Span | Slices over rented buffers |
| TPL paper | Parallel scouts + main-thread GetColor split |
| LOH + CLR GC | Texture-mean cache, reuse lists, despawn scouts |
| SIMD (future) | Post-GetColor blur/quantize only |

## Repo primary doc

[[Sources/login-bake-efficiency]] — `docs/plans/login-bake-efficiency.md`
