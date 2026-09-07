using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace DistantVistas;

/// <summary>
/// Post-GetColor integer kernels. Vintage Story Block.GetColor stays scalar on the
/// main thread; these vectorize blur, quantize, and RGB pack/unpack over already
/// sampled <c>int[]</c> buffers. AVX2 (8-wide) then portable <see cref="Vector128"/>
/// (SSE2 / NEON; same hardware as <see cref="Vector.IsHardwareAccelerated"/>), then
/// scalar. Results are bit-identical to the scalar path (truncated integer averages,
/// same pack/unpack layout). See docs/plans/simd-after-getcolor.md and
/// docs/plans/login-bake-efficiency.md §4.
/// </summary>
public static class LodRgbSimd
{
    /// <summary>Test hook: force the scalar kernels even when hardware SIMD exists.</summary>
    internal static bool ForceScalar;

    public static bool HardwareAccelerated =>
        !ForceScalar && (Avx2.IsSupported || Vector128.IsHardwareAccelerated
            || Vector.IsHardwareAccelerated);

    public static string PathName =>
        ForceScalar ? "scalar"
        : Avx2.IsSupported ? "avx2"
        : Vector128.IsHardwareAccelerated ? "vector128"
        : Vector.IsHardwareAccelerated ? "vector"
        : "scalar";

    [ThreadStatic] static int[]? planeR;
    [ThreadStatic] static int[]? planeG;
    [ThreadStatic] static int[]? planeB;
    [ThreadStatic] static int[]? planeM;

    const int Alpha = unchecked((int)0xFF000000);

    public static int QuantizePacked(int color, int step)
    {
        if (color == 0 || step <= 1) return color;
        Unpack(color, out int r, out int g, out int b);
        if (!HardwareAccelerated)
            return Pack(Snap(r, step), Snap(g, step), Snap(b, step));

        Vector128<int> v = Vector128.Create(r, g, b, 0);
        v = SnapLanes128(v, step);
        return Pack(v.GetElement(0), v.GetElement(1), v.GetElement(2));
    }

    public static void QuantizeSpan(Span<int> colors, int step)
    {
        if (step <= 1 || colors.Length == 0) return;
        int i = 0;
        if (!ForceScalar && Avx2.IsSupported)
        {
            ref int c0 = ref MemoryMarshal.GetReference(colors);
            for (; i <= colors.Length - 8; i += 8)
            {
                Vector256<int> packed = Vector256.LoadUnsafe(ref Unsafe.Add(ref c0, i));
                Vector256<int> zero = Vector256.Equals(packed, Vector256<int>.Zero);
                Unpack8(packed, out Vector256<int> r, out Vector256<int> g, out Vector256<int> b);
                r = SnapLanes256(r, step);
                g = SnapLanes256(g, step);
                b = SnapLanes256(b, step);
                Vector256<int> q = Pack8(r, g, b);
                q = Vector256.ConditionalSelect(zero, Vector256<int>.Zero, q);
                Vector256.StoreUnsafe(q, ref Unsafe.Add(ref c0, i));
            }
        }
        else if (!ForceScalar && Vector.IsHardwareAccelerated && Vector<int>.Count >= 4)
        {
            int w = Vector<int>.Count;
            Vector<int> stepV = new(step);
            Vector<int> halfV = new(step / 2);
            Vector<int> ff = new(255);
            Vector<int> byteMask = new(0xFF);
            Vector<int> alpha = new(Alpha);
            for (; i <= colors.Length - w; i += w)
            {
                Vector<int> packed = new(colors.Slice(i, w));
                Vector<int> isZero = Vector.Equals(packed, Vector<int>.Zero);
                Vector<int> r = packed & byteMask;
                Vector<int> g = Vector.ShiftRightLogical(packed, 8) & byteMask;
                Vector<int> b = Vector.ShiftRightLogical(packed, 16) & byteMask;
                r = Vector.Min(Vector.Divide(r + halfV, stepV) * stepV, ff);
                g = Vector.Min(Vector.Divide(g + halfV, stepV) * stepV, ff);
                b = Vector.Min(Vector.Divide(b + halfV, stepV) * stepV, ff);
                Vector<int> q = alpha | Vector.ShiftLeft(b, 16) | Vector.ShiftLeft(g, 8) | r;
                q = Vector.ConditionalSelect(isZero, Vector<int>.Zero, q);
                q.CopyTo(colors.Slice(i, w));
            }
        }
        else if (!ForceScalar && Vector128.IsHardwareAccelerated)
        {
            ref int c0 = ref MemoryMarshal.GetReference(colors);
            for (; i <= colors.Length - 4; i += 4)
            {
                Vector128<int> packed = Vector128.LoadUnsafe(ref Unsafe.Add(ref c0, i));
                Vector128<int> zero = Vector128.Equals(packed, Vector128<int>.Zero);
                Unpack4(packed, out Vector128<int> r, out Vector128<int> g, out Vector128<int> b);
                r = SnapLanes128(r, step);
                g = SnapLanes128(g, step);
                b = SnapLanes128(b, step);
                Vector128<int> q = Pack4(r, g, b);
                q = Vector128.ConditionalSelect(zero, Vector128<int>.Zero, q);
                Vector128.StoreUnsafe(q, ref Unsafe.Add(ref c0, i));
            }
        }

        for (; i < colors.Length; i++)
            colors[i] = QuantizePacked(colors[i], step);
    }

    public static void UnpackPlanes(ReadOnlySpan<int> packed, Span<int> r, Span<int> g, Span<int> b)
    {
        int n = packed.Length;
        int i = 0;
        if (!ForceScalar && Avx2.IsSupported)
        {
            ref int p0 = ref MemoryMarshal.GetReference(packed);
            ref int r0 = ref MemoryMarshal.GetReference(r);
            ref int g0 = ref MemoryMarshal.GetReference(g);
            ref int b0 = ref MemoryMarshal.GetReference(b);
            for (; i <= n - 8; i += 8)
            {
                Vector256<int> v = Vector256.LoadUnsafe(ref Unsafe.Add(ref p0, i));
                Unpack8(v, out Vector256<int> vr, out Vector256<int> vg, out Vector256<int> vb);
                Vector256.StoreUnsafe(vr, ref Unsafe.Add(ref r0, i));
                Vector256.StoreUnsafe(vg, ref Unsafe.Add(ref g0, i));
                Vector256.StoreUnsafe(vb, ref Unsafe.Add(ref b0, i));
            }
        }
        else if (!ForceScalar && Vector128.IsHardwareAccelerated)
        {
            ref int p0 = ref MemoryMarshal.GetReference(packed);
            ref int r0 = ref MemoryMarshal.GetReference(r);
            ref int g0 = ref MemoryMarshal.GetReference(g);
            ref int b0 = ref MemoryMarshal.GetReference(b);
            for (; i <= n - 4; i += 4)
            {
                Vector128<int> v = Vector128.LoadUnsafe(ref Unsafe.Add(ref p0, i));
                Unpack4(v, out Vector128<int> vr, out Vector128<int> vg, out Vector128<int> vb);
                Vector128.StoreUnsafe(vr, ref Unsafe.Add(ref r0, i));
                Vector128.StoreUnsafe(vg, ref Unsafe.Add(ref g0, i));
                Vector128.StoreUnsafe(vb, ref Unsafe.Add(ref b0, i));
            }
        }

        for (; i < n; i++)
            Unpack(packed[i], out r[i], out g[i], out b[i]);
    }

    public static void PackPlanes(ReadOnlySpan<int> r, ReadOnlySpan<int> g, ReadOnlySpan<int> b, Span<int> packed)
    {
        int n = packed.Length;
        int i = 0;
        if (!ForceScalar && Avx2.IsSupported)
        {
            ref int p0 = ref MemoryMarshal.GetReference(packed);
            ref int r0 = ref MemoryMarshal.GetReference(r);
            ref int g0 = ref MemoryMarshal.GetReference(g);
            ref int b0 = ref MemoryMarshal.GetReference(b);
            for (; i <= n - 8; i += 8)
            {
                Vector256<int> vr = Vector256.LoadUnsafe(ref Unsafe.Add(ref r0, i));
                Vector256<int> vg = Vector256.LoadUnsafe(ref Unsafe.Add(ref g0, i));
                Vector256<int> vb = Vector256.LoadUnsafe(ref Unsafe.Add(ref b0, i));
                Vector256.StoreUnsafe(Pack8(vr, vg, vb), ref Unsafe.Add(ref p0, i));
            }
        }
        else if (!ForceScalar && Vector128.IsHardwareAccelerated)
        {
            ref int p0 = ref MemoryMarshal.GetReference(packed);
            ref int r0 = ref MemoryMarshal.GetReference(r);
            ref int g0 = ref MemoryMarshal.GetReference(g);
            ref int b0 = ref MemoryMarshal.GetReference(b);
            for (; i <= n - 4; i += 4)
            {
                Vector128<int> vr = Vector128.LoadUnsafe(ref Unsafe.Add(ref r0, i));
                Vector128<int> vg = Vector128.LoadUnsafe(ref Unsafe.Add(ref g0, i));
                Vector128<int> vb = Vector128.LoadUnsafe(ref Unsafe.Add(ref b0, i));
                Vector128.StoreUnsafe(Pack4(vr, vg, vb), ref Unsafe.Add(ref p0, i));
            }
        }

        for (; i < n; i++)
            packed[i] = Pack(r[i], g[i], b[i]);
    }

    /// <summary>
    /// One box-blur pass. Land (mask 1) averages land neighbours; water copies;
    /// empty stays 0. Bit-identical to <see cref="BlurLandOnceScalar"/>.
    /// </summary>
    public static void BlurLandOnce(int[] src, byte[] mask, int[] dst, int gs, int radius)
    {
        if (radius < 0) radius = 0;
        int n = gs * gs;
        if (ForceScalar || !HardwareAccelerated)
        {
            BlurLandOnceScalar(src, mask, dst, gs, radius);
            return;
        }

        if (radius == 0)
        {
            BlurLandOnceRadiusZero(src, mask, dst, n);
            return;
        }

        RentPlanes(n, out int[] pr, out int[] pg, out int[] pb, out int[] pm);
        UnpackPlanes(src.AsSpan(0, n), pr.AsSpan(0, n), pg.AsSpan(0, n), pb.AsSpan(0, n));
        WidenMask(mask.AsSpan(0, n), pm.AsSpan(0, n));
        BlurLandOnceFromPlanes(src, pr, pg, pb, pm, dst, gs, radius);
    }

    public static void BlurLandOnceScalar(int[] src, byte[] mask, int[] dst, int gs, int radius)
    {
        for (int cz = 0; cz < gs; cz++)
        {
            for (int cx = 0; cx < gs; cx++)
            {
                int i = cz * gs + cx;
                byte m = mask[i];
                if (m != 1)
                {
                    dst[i] = m == 2 ? src[i] : 0;
                    continue;
                }

                long r = 0, g = 0, b = 0, n = 0;
                int z0 = cz - radius, z1 = cz + radius;
                int x0 = cx - radius, x1 = cx + radius;
                if (z0 < 0) z0 = 0;
                if (x0 < 0) x0 = 0;
                if (z1 >= gs) z1 = gs - 1;
                if (x1 >= gs) x1 = gs - 1;
                for (int nz = z0; nz <= z1; nz++)
                {
                    int row = nz * gs;
                    for (int nx = x0; nx <= x1; nx++)
                    {
                        int j = row + nx;
                        if (mask[j] != 1) continue;
                        Unpack(src[j], out int sr, out int sg, out int sb);
                        r += sr;
                        g += sg;
                        b += sb;
                        n++;
                    }
                }

                dst[i] = n == 0 ? src[i] : Pack((int)(r / n), (int)(g / n), (int)(b / n));
            }
        }
    }

    static void BlurLandOnceRadiusZero(int[] src, byte[] mask, int[] dst, int n)
    {
        int i = 0;
        if (Avx2.IsSupported)
        {
            ref int s0 = ref MemoryMarshal.GetArrayDataReference(src);
            ref int d0 = ref MemoryMarshal.GetArrayDataReference(dst);
            ref byte m0 = ref MemoryMarshal.GetArrayDataReference(mask);
            for (; i <= n - 8; i += 8)
            {
                Vector256<int> packed = Vector256.LoadUnsafe(ref Unsafe.Add(ref s0, i));
                Vector256<int> mi = Widen8(ref Unsafe.Add(ref m0, i));
                Unpack8(packed, out Vector256<int> r, out Vector256<int> g, out Vector256<int> b);
                Vector256<int> landPacked = Pack8(r, g, b);
                Vector256<int> isLand = Vector256.Equals(mi, Vector256.Create(1));
                Vector256<int> isWater = Vector256.Equals(mi, Vector256.Create(2));
                Vector256<int> land = Vector256.ConditionalSelect(isLand, landPacked, Vector256<int>.Zero);
                Vector256<int> water = Vector256.ConditionalSelect(isWater, packed, Vector256<int>.Zero);
                Vector256.StoreUnsafe(land | water, ref Unsafe.Add(ref d0, i));
            }
        }
        else if (Vector128.IsHardwareAccelerated)
        {
            ref int s0 = ref MemoryMarshal.GetArrayDataReference(src);
            ref int d0 = ref MemoryMarshal.GetArrayDataReference(dst);
            ref byte m0 = ref MemoryMarshal.GetArrayDataReference(mask);
            for (; i <= n - 4; i += 4)
            {
                Vector128<int> packed = Vector128.LoadUnsafe(ref Unsafe.Add(ref s0, i));
                Vector128<int> mi = Widen4(ref Unsafe.Add(ref m0, i));
                Unpack4(packed, out Vector128<int> r, out Vector128<int> g, out Vector128<int> b);
                Vector128<int> landPacked = Pack4(r, g, b);
                Vector128<int> isLand = Vector128.Equals(mi, Vector128.Create(1));
                Vector128<int> isWater = Vector128.Equals(mi, Vector128.Create(2));
                Vector128<int> land = Vector128.ConditionalSelect(isLand, landPacked, Vector128<int>.Zero);
                Vector128<int> water = Vector128.ConditionalSelect(isWater, packed, Vector128<int>.Zero);
                Vector128.StoreUnsafe(land | water, ref Unsafe.Add(ref d0, i));
            }
        }

        for (; i < n; i++)
        {
            byte m = mask[i];
            if (m != 1)
            {
                dst[i] = m == 2 ? src[i] : 0;
                continue;
            }
            Unpack(src[i], out int r, out int g, out int b);
            dst[i] = Pack(r, g, b);
        }
    }

    static void BlurLandOnceFromPlanes(
        int[] src, int[] pr, int[] pg, int[] pb, int[] pm, int[] dst, int gs, int radius)
    {
        for (int cz = 0; cz < gs; cz++)
        {
            for (int cx = 0; cx < gs; cx++)
            {
                int i = cz * gs + cx;
                int m = pm[i];
                if (m != 1)
                {
                    dst[i] = m == 2 ? src[i] : 0;
                    continue;
                }

                int z0 = cz - radius, z1 = cz + radius;
                int x0 = cx - radius, x1 = cx + radius;
                if (z0 < 0) z0 = 0;
                if (x0 < 0) x0 = 0;
                if (z1 >= gs) z1 = gs - 1;
                if (x1 >= gs) x1 = gs - 1;

                long r = 0, g = 0, b = 0, n = 0;
                for (int nz = z0; nz <= z1; nz++)
                    AccumulateRow(pr, pg, pb, pm, nz * gs, x0, x1, ref r, ref g, ref b, ref n);

                dst[i] = n == 0 ? src[i] : Pack((int)(r / n), (int)(g / n), (int)(b / n));
            }
        }
    }

    static void AccumulateRow(
        int[] pr, int[] pg, int[] pb, int[] pm,
        int row, int x0, int x1,
        ref long r, ref long g, ref long b, ref long n)
    {
        int nx = x0;
        if (!ForceScalar && Avx2.IsSupported)
        {
            Vector256<int> accR = Vector256<int>.Zero;
            Vector256<int> accG = Vector256<int>.Zero;
            Vector256<int> accB = Vector256<int>.Zero;
            Vector256<int> accN = Vector256<int>.Zero;
            Vector256<int> one = Vector256.Create(1);
            for (; nx + 8 <= x1 + 1; nx += 8)
            {
                int j = row + nx;
                Vector256<int> land = Vector256.Equals(
                    Vector256.LoadUnsafe(ref pm[j]), one);
                accR += Vector256.ConditionalSelect(land, Vector256.LoadUnsafe(ref pr[j]), Vector256<int>.Zero);
                accG += Vector256.ConditionalSelect(land, Vector256.LoadUnsafe(ref pg[j]), Vector256<int>.Zero);
                accB += Vector256.ConditionalSelect(land, Vector256.LoadUnsafe(ref pb[j]), Vector256<int>.Zero);
                accN += Vector256.ConditionalSelect(land, one, Vector256<int>.Zero);
            }
            r += Vector256.Sum(accR);
            g += Vector256.Sum(accG);
            b += Vector256.Sum(accB);
            n += Vector256.Sum(accN);
        }
        else if (!ForceScalar && Vector128.IsHardwareAccelerated)
        {
            Vector128<int> accR = Vector128<int>.Zero;
            Vector128<int> accG = Vector128<int>.Zero;
            Vector128<int> accB = Vector128<int>.Zero;
            Vector128<int> accN = Vector128<int>.Zero;
            Vector128<int> one = Vector128.Create(1);
            for (; nx + 4 <= x1 + 1; nx += 4)
            {
                int j = row + nx;
                Vector128<int> land = Vector128.Equals(
                    Vector128.LoadUnsafe(ref pm[j]), one);
                accR += Vector128.ConditionalSelect(land, Vector128.LoadUnsafe(ref pr[j]), Vector128<int>.Zero);
                accG += Vector128.ConditionalSelect(land, Vector128.LoadUnsafe(ref pg[j]), Vector128<int>.Zero);
                accB += Vector128.ConditionalSelect(land, Vector128.LoadUnsafe(ref pb[j]), Vector128<int>.Zero);
                accN += Vector128.ConditionalSelect(land, one, Vector128<int>.Zero);
            }
            r += Vector128.Sum(accR);
            g += Vector128.Sum(accG);
            b += Vector128.Sum(accB);
            n += Vector128.Sum(accN);
        }

        for (; nx <= x1; nx++)
        {
            int j = row + nx;
            if (pm[j] != 1) continue;
            r += pr[j];
            g += pg[j];
            b += pb[j];
            n++;
        }
    }

    static void WidenMask(ReadOnlySpan<byte> mask, Span<int> dst)
    {
        int n = mask.Length;
        int i = 0;
        if (!ForceScalar && Avx2.IsSupported)
        {
            ref byte m0 = ref MemoryMarshal.GetReference(mask);
            ref int d0 = ref MemoryMarshal.GetReference(dst);
            for (; i <= n - 8; i += 8)
                Vector256.StoreUnsafe(Widen8(ref Unsafe.Add(ref m0, i)), ref Unsafe.Add(ref d0, i));
        }
        else if (!ForceScalar && Vector128.IsHardwareAccelerated)
        {
            ref byte m0 = ref MemoryMarshal.GetReference(mask);
            ref int d0 = ref MemoryMarshal.GetReference(dst);
            for (; i <= n - 4; i += 4)
                Vector128.StoreUnsafe(Widen4(ref Unsafe.Add(ref m0, i)), ref Unsafe.Add(ref d0, i));
        }

        for (; i < n; i++)
            dst[i] = mask[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<int> Widen8(ref byte m)
    {
        ulong packed = Unsafe.ReadUnaligned<ulong>(ref m);
        return Avx2.ConvertToVector256Int32(Vector128.CreateScalar(packed).AsByte());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<int> Widen4(ref byte m)
    {
        int p = Unsafe.ReadUnaligned<int>(ref m);
        return Vector128.Create(p & 0xFF, (p >> 8) & 0xFF, (p >> 16) & 0xFF, (p >> 24) & 0xFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Unpack8(Vector256<int> packed, out Vector256<int> r, out Vector256<int> g, out Vector256<int> b)
    {
        Vector256<int> m = Vector256.Create(0xFF);
        r = packed & m;
        g = Vector256.ShiftRightLogical(packed, 8) & m;
        b = Vector256.ShiftRightLogical(packed, 16) & m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Unpack4(Vector128<int> packed, out Vector128<int> r, out Vector128<int> g, out Vector128<int> b)
    {
        Vector128<int> m = Vector128.Create(0xFF);
        r = packed & m;
        g = Vector128.ShiftRightLogical(packed, 8) & m;
        b = Vector128.ShiftRightLogical(packed, 16) & m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<int> Pack8(Vector256<int> r, Vector256<int> g, Vector256<int> b)
    {
        Vector256<int> z = Vector256<int>.Zero;
        Vector256<int> ff = Vector256.Create(255);
        r = Vector256.Min(Vector256.Max(r, z), ff);
        g = Vector256.Min(Vector256.Max(g, z), ff);
        b = Vector256.Min(Vector256.Max(b, z), ff);
        return Vector256.Create(Alpha)
            | Vector256.ShiftLeft(b, 16)
            | Vector256.ShiftLeft(g, 8)
            | r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<int> Pack4(Vector128<int> r, Vector128<int> g, Vector128<int> b)
    {
        Vector128<int> z = Vector128<int>.Zero;
        Vector128<int> ff = Vector128.Create(255);
        r = Vector128.Min(Vector128.Max(r, z), ff);
        g = Vector128.Min(Vector128.Max(g, z), ff);
        b = Vector128.Min(Vector128.Max(b, z), ff);
        return Vector128.Create(Alpha)
            | Vector128.ShiftLeft(b, 16)
            | Vector128.ShiftLeft(g, 8)
            | r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector256<int> SnapLanes256(Vector256<int> v, int step)
    {
        Vector256<int> s = Vector256.Create(step);
        Vector256<int> q = Vector256.Divide(v + Vector256.Create(step / 2), s) * s;
        return Vector256.Min(q, Vector256.Create(255));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector128<int> SnapLanes128(Vector128<int> v, int step)
    {
        Vector128<int> s = Vector128.Create(step);
        Vector128<int> q = Vector128.Divide(v + Vector128.Create(step / 2), s) * s;
        return Vector128.Min(q, Vector128.Create(255));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Snap(int v, int step)
    {
        int q = ((v + step / 2) / step) * step;
        return q > 255 ? 255 : q;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int Pack(int r, int g, int b) =>
        Alpha
        | Math.Clamp(b, 0, 255) << 16
        | Math.Clamp(g, 0, 255) << 8
        | Math.Clamp(r, 0, 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Unpack(int color, out int r, out int g, out int b)
    {
        r = color & 0xFF;
        g = (color >> 8) & 0xFF;
        b = (color >> 16) & 0xFF;
    }

    static void RentPlanes(int n, out int[] r, out int[] g, out int[] b, out int[] m)
    {
        if (planeR == null || planeR.Length < n)
        {
            if (planeR != null)
            {
                ArrayPool<int>.Shared.Return(planeR);
                ArrayPool<int>.Shared.Return(planeG!);
                ArrayPool<int>.Shared.Return(planeB!);
                ArrayPool<int>.Shared.Return(planeM!);
            }
            planeR = ArrayPool<int>.Shared.Rent(n);
            planeG = ArrayPool<int>.Shared.Rent(n);
            planeB = ArrayPool<int>.Shared.Rent(n);
            planeM = ArrayPool<int>.Shared.Rent(n);
        }

        r = planeR;
        g = planeG!;
        b = planeB!;
        m = planeM!;
    }
}
