# SIMD after GetColor — research and implementation

Live on `cursor/1.0.26-catchup-playtest-e27c` (`LodRgbSimd`). Research note ported from `cursor/simd-after-getcolor` (did not merge that branch wholesale).  
Parent plan: [`login-bake-efficiency.md`](login-bake-efficiency.md) §4.

## Constraint (hard)

Vintage Story `Block.GetColor` / `GetColorWithoutTint` **must stay scalar** on the client main thread. They touch client-only block state, climate/season maps, and texture sampling. SIMD applies only to **already-sampled** `int[]` / `byte[]` buffers in the login-bake paint path.

```
GetColor (scalar, main thread)
    → int[] rawMix[4096] + byte[] mixMask[4096]
    → LodRgbSimd.BlurLandOnce ×2  (LodSurfaceMix.BlurLand)
    → optional Quantize (API live; production grid does not quantize — checkerboard risk)
```

## Type and call sites

| Component | Role |
|-----------|------|
| `LodRgbSimd` | All post-GetColor integer kernels |
| `LodSurfaceMix.BlurLand` | Two-pass box blur; delegates to `LodRgbSimd.BlurLandOnce` |
| `LodSurfaceMix.Quantize` | Thin wrapper over `LodRgbSimd.QuantizePacked` |
| `LodSeasonBake` | Fills `raw` via GetColor; calls `LodSurfaceMix` finish/blur |
| `LodMesher` | Consumes baked palette RGB; no GetColor in mesh upload |

Production overlay uses `LodSurfaceMix.BlurRadius = 0`. Radius 0 is **not** a no-op: it still runs the mask-aware land/water copy and forces `Pack(Unpack(rgb))` so alpha `0xFF` is set on land cells (including packed `0`).

## Vectorized loops

### 1. `BlurLandOnce` — radius 0 (production hot path)

**Loop:** 4096 cells (`64×64` L0 column grid), mask byte + packed `0xFFBBGGRR` int per cell.

**SIMD:** `BlurLandOnceRadiusZero` — 8-wide AVX2 or 4-wide `Vector128`:
- Widen mask bytes to int lanes (`Widen8` / `Widen4`)
- Unpack RGB from packed ints
- `ConditionalSelect` for land (mask==1) vs water (mask==2) vs empty
- Repack with `Pack8` / `Pack4`

**Scalar tail:** remaining cells `< 8` (or `< 4`).

**Cost (reasoned):** ~512 AVX2 iterations per pass × 2 passes ≈ 1k vector blocks per L0 blur. At ~10–20 cycles/block this is **≪ 1 ms** vs GetColor’s **4096 × stack depth** API calls per section. Blur is not the login-bake bottleneck; vectorizing removes avoidable scalar overhead on every painted stop without changing visuals (`BlurRadius` stays 0).

### 2. `BlurLandOnce` — radius ≥ 1 (API / future)

**Loop:** Per output cell, box window over land neighbours; integer sum then `/ n` (truncated).

**SIMD:**
1. `UnpackPlanes` — deinterleave 4096 packed ints → R/G/B plane arrays (AVX2 8-wide, else Vector128 4-wide)
2. `WidenMask` — byte mask → int plane
3. `AccumulateRow` — within each blur window row, vector accumulate land pixels (8- or 4-wide), `Vector256.Sum` / `Vector128.Sum` into scalar `long` accumulators
4. Scalar tail per row + per-cell divide/pack

**Bit identity:** Integer division truncates toward zero, matching `BlurLandOnceScalar`.

### 3. `QuantizePacked` / `QuantizeSpan`

**Loop:** Snap each channel to nearest `step` with half-step bias: `((v + step/2) / step) * step`, clamp 255. Preserve `color == 0` as transparent.

**SIMD paths (in order):**
1. **AVX2** — 8 packed colors per iteration (`QuantizeSpan`)
2. **`System.Numerics.Vector<int>`** — hardware width (often 4 or 8) when AVX2 absent but `Vector.IsHardwareAccelerated`
3. **`Vector128<int>`** — 4-wide portable (SSE2 on x64, NEON on ARM64)
4. **Scalar** — per-element `QuantizePacked`

Single-color `QuantizePacked` uses `Vector128` lanes when accelerated.

**Production note:** The 64×64 bake grid is **not** quantized in overlay (snow/dirt checkerboard). APIs are live for tests and optional tooling.

### 4. `UnpackPlanes` / `PackPlanes`

**Loop:** 4096 packed ↔ three int planes (R, G, B separate arrays).

**SIMD:** AVX2 8-wide or Vector128 4-wide shift/mask unpack; inverse for pack with clamp and `0xFF000000` alpha OR.

Used by radius≥1 blur and round-trip tests.

## Intrinsic selection and fallback

```text
ForceScalar (tests only)
    → scalar reference kernels

Avx2.IsSupported
    → Vector256 8-wide (System.Runtime.Intrinsics.X86)

Vector128.IsHardwareAccelerated
    → Vector128 4-wide (SSE2 / NEON)

Vector.IsHardwareAccelerated (QuantizeSpan only)
    → System.Numerics.Vector<int> portable width

else
    → scalar (BlurLandOnceScalar, per-pixel QuantizePacked, scalar unpack/pack)
```

`LodRgbSimd.PathName` reports active tier: `avx2` | `vector128` | `vector` | `scalar`.

`LodRgbSimd.ForceScalar` is `internal`; unit checks toggle it to prove **bit-identical** results vs SIMD.

No runtime CPU dispatch tables beyond BCL `IsSupported` checks — standard .NET pattern.

## Correctness guarantees

| Property | Guarantee |
|----------|-----------|
| GetColor isolation | `LodRgbSimd` contains no `GetColor` / `GetColorWithoutTint` |
| Pack layout | `0xFFBBGGRR`, same as `LodSurfaceMix.Pack` |
| Blur average | Truncating integer `/ n`, not rounded |
| Quantize | Half-step snap; `0` stays `0` |
| Radius 0 land | `Pack(0,0,0)` → alpha `0xFF` on mask==1 |

Tests: `SeasonBakeChecks.SimdAfterGetColor`, `LoginSweepChecks.PostGetColorSimd` (static source guards + bit-identical property tests).

## Benchmarks

No in-repo microbench on this agent VM (no Vintage Story assemblies / `dotnet` in cloud snapshot). Reasoned model:

| Stage | Per L0 section | Dominant cost |
|-------|----------------|---------------|
| GetColor × stack | 4096 × up to 16 samples | **Primary** (~seconds across overlay) |
| Texture-mean cache | 1×8 samples per BlockId | Secondary (cached per section) |
| BlurLand r=0 SIMD | ~2 × 4096 cells vectorized | **Negligible** vs GetColor |
| Quantize (if enabled) | 4096 vectorized snaps | Small; disabled in production paint |

Expected speedup from SIMD: trims microseconds–low milliseconds per blurred section — worthwhile for correctness hygiene and headroom if `BlurRadius` > 0 is re-enabled, not a substitute for GetColor pooling/caching (see parent plan A3/B).

## Out of scope (intentionally scalar)

- `Block.GetColor`, `GetColorWithoutTint`, `SampleTextureMean` texture API
- `LodSurfaceMix.FinishColumnPaint`, `LerpRgb`, stack classification loops (branchy, per-column)
- `LodMesher` vertex generation (different data layout; separate optimization track)
- Scouts, skip-gates, mesh-wait, Farseer mask rebuild

## Files

| File | Purpose |
|------|---------|
| `DistantVistas/src/Render/LodRgbSimd.cs` | SIMD kernels + scalar reference |
| `DistantVistas/src/Render/LodSurfaceMix.cs` | `BlurLand`, `Quantize` call sites |
| `tests/VintageHorizons.Checks/SeasonBakeChecks.cs` | Bit-identical property tests |
| `tests/VintageHorizons.Checks/LoginSweepChecks.cs` | Static SIMD contract checks |

## Citations

- Microsoft Learn, [Use SIMD-accelerated types](https://learn.microsoft.com/en-us/dotnet/standard/simd)
- Microsoft Learn, [Vector256\<T\>](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.vector256-1)
- Microsoft Learn, [Vector128\<T\>](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.vector128-1)
- Microsoft Learn, [System.Numerics.Vector\<T\>](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.vector-1)
- Microsoft Learn, [Avx2 class](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.x86.avx2)
- Intel, [Intrinsics Guide — AVX2](https://www.intel.com/content/www/us/en/docs/intrinsics-guide/index.html#avx2techs=AVX2)

## Verification

On a machine with Vintage Story + .NET:

```bash
scripts/check.sh fast
```

Look for `SimdAfterGetColor` and `PostGetColorSimd` passing. Cold login playtest: unchanged visuals with `BlurRadius = 0`; overlay invariants unchanged (no teleports, spawn-solid, scout despawn).
