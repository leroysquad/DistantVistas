namespace DistantVistas;

/// <summary>
/// When a child can replace its parent's mesh, and when vanilla actually owns
/// a column this frame (3D + look-down). Completeness is L0-only; applying it
/// to L1+ hid the far landscape. PreferParentCoverage is the completeness gate
/// (do not punch sky by pretending an incomplete L0 replaced its parent). The
/// renderer must not use that as a license to draw the parent as a giant plate.
/// </summary>
public static class LodCoveragePolicy
{
    /// <summary>
    /// Captured L0/L1 near the player trail may bypass frustum cull so fast flight
    /// does not punch sky holes behind the camera. Horizon-wide L0 still culls.
    /// </summary>
    public const int VisitedKeepMaxLevel = 1;
    public static bool IsVisitedKeepLevel(int level) => level <= VisitedKeepMaxLevel;

    /// <summary>
    /// Keep-circle is vanilla view distance times this scale (2× on big RAM so
    /// visited land stays). LodMemoryBudget sets it at startup. Mesh count alone
    /// never shrinks it; pressure eviction only drops outside PressureKeepScale.
    /// </summary>
    public static float KeepCircleScale = LodMemoryBudget.DefaultKeepScale;

    /// <summary>
    /// Inside this circle, visited L0/L1 stays on the GPU and skips frustum cull.
    /// Outside it, under pressure only, oldest meshes may un-render; disk stays.
    /// </summary>
    public static bool IsNearVisitedTrail(double distance, double viewDistanceAnchor) =>
        distance < viewDistanceAnchor * KeepCircleScale;

    /// <summary>
    /// One L0 section is 64 blocks on a side. The render window origin only
    /// moves when the player walks at least that far in XZ. Looking around
    /// is not a move, and neither is a step that stays inside the same tile.
    /// </summary>
    public const int IdleOriginTileBlocks = LodSection.SectionBlocks;

    public static bool OriginShifted(double originX, double originZ, double x, double z)
    {
        double dx = x - originX;
        double dz = z - originZ;
        double tile = IdleOriginTileBlocks;
        return dx * dx + dz * dz >= tile * tile;
    }

    public static double KeepCircleRadius(double viewDistanceAnchor) =>
        viewDistanceAnchor * KeepCircleScale;

    /// <summary>
    /// Draw at full L0/L1 only inside live vanilla view distance. The keep-circle
    /// is larger and only about holding GPU meshes, not about which mesh we submit.
    /// </summary>
    public static bool IsDrawFullDetail(double distance, double viewDistanceAnchor) =>
        distance < viewDistanceAnchor;

    public static double DrawFullDetailRadius(double viewDistanceAnchor) =>
        viewDistanceAnchor;

    public static bool ShouldKeepVisitedDraw(int level, bool hasDataSet, double distance, double viewDistanceAnchor) =>
        hasDataSet && IsVisitedKeepLevel(level) && IsNearVisitedTrail(distance, viewDistanceAnchor);

    public static bool MustDescendForVisualCap(int level, int maxVisualLevel) =>
        level > Math.Clamp(maxVisualLevel, 0, LodWorld.MaxLevel);

    /// <summary>
    /// 0 at/above the horizon, 1 looking straight down. VS Y is up; GetViewVector
    /// Y is negative when the camera pitches toward the ground.
    /// </summary>
    public static float LookDownAmount(double viewY)
    {
        if (viewY >= 0) return 0f;
        return (float)Math.Min(1.0, -viewY);
    }

    /// <summary>
    /// Vanilla owns this ground only if the 3D distance from camera to the
    /// surface is inside the skip sphere. Horizontal-only is never enough: at
    /// altitude the ground is outside that sphere and LOD stays. Looking down
    /// from high up may shrink the remaining disc so a mid-alt column vanilla
    /// dropped still gets LOD. Looking down at your feet does not shrink: that
    /// ice is loaded chunks, and LOD on top of it flickers. Missing surface
    /// bounds must not be passed here; the caller treats that as not owned.
    /// </summary>
    public static bool InsideVanillaCoverage(
        double horizontalDistanceSq, double cameraY, int surfaceYMin, int surfaceYMax,
        double radius, double lookDown01 = 0)
    {
        if (radius <= 0) return false;
        lookDown01 = Math.Clamp(lookDown01, 0, 1);

        double verticalDistance = cameraY > surfaceYMax
            ? cameraY - surfaceYMax
            : cameraY < surfaceYMin
                ? surfaceYMin - cameraY
                : 0;

        if (horizontalDistanceSq + verticalDistance * verticalDistance >= radius * radius)
            return false;
        if (verticalDistance >= radius) return false;

        double groundReachSq = radius * radius - verticalDistance * verticalDistance;
        // Look-down used to shrink this to zero for every pitch, including the
        // tile you are standing on. That drew LOD on loaded ice and the two
        // meshes flickered. Only uncover when the camera is high enough that
        // vanilla often dropped that column; at your feet the 3D sphere wins.
        // Mild pitch still has skyline in frame (0.55 is ~33 deg). Do not
        // shrink until LookDownSteepAmount, same gate as coarse fill.
        double aboveSurface = Math.Max(0, cameraY - surfaceYMax);
        float steep = LookDownSteepAmount(lookDown01);
        if (aboveSurface >= radius * 0.45 && steep > 0)
        {
            double scale = 1.0 - steep;
            groundReachSq *= scale * scale;
        }
        return horizontalDistanceSq < groundReachSq;
    }

    /// <summary>
    /// Vanilla owns this TILE only if the farthest XZ corner is still inside
    /// the skip sphere. The nearest-point test hid a whole 64x64 as soon as
    /// the circle touched it, which punched a camera-locked chopped ring.
    /// </summary>
    public static bool EntireAabbInsideVanilla(
        double minX, double maxX, double minZ, double maxZ,
        double camX, double camZ, double cameraY,
        int surfaceYMin, int surfaceYMax,
        double radius, double lookDown01 = 0)
    {
        double midX = (minX + maxX) * 0.5;
        double midZ = (minZ + maxZ) * 0.5;
        double farX = camX < midX ? maxX : minX;
        double farZ = camZ < midZ ? maxZ : minZ;
        double dx = farX - camX;
        double dz = farZ - camZ;
        return InsideVanillaCoverage(
            dx * dx + dz * dz, cameraY, surfaceYMin, surfaceYMax, radius, lookDown01);
    }

    /// <summary>
    /// Hand the tile to vanilla when the whole AABB is inside the skip disc,
    /// every map-chunk covering it has arrived, and every world column is
    /// actually loaded at the surface. Map chunks stay for explored land
    /// (minimap); vanilla only tessellates world chunks. Explored-but-unloaded
    /// ground is LOD, not sky. A geometric circle alone punches sky when you
    /// raise VD before the columns arrive (0.7.48). Camera-Y air is not a
    /// world column — see WorldColumnIsTessellated.
    /// </summary>
    public static bool VanillaOwnsFootprint(
        bool entireAabbInsideVanilla3D, bool allMapChunksLoaded, bool allWorldColumnsLoaded) =>
        entireAabbInsideVanilla3D && allMapChunksLoaded && allWorldColumnsLoaded;

    /// <summary>
    /// Vanilla draws the surface of a column, not air at the camera.
    /// Flying over ocean loads sky chunks around you; those are not water
    /// and not seafloor. Treating camera-Y as owned discards the LOD tile
    /// and leaves a wide-open hole — neighbouring tiles still draw, so you
    /// see their seafloor through it. Close in it fills; fly back and the
    /// hole returns. cameraYChunkLoaded is never proof.
    /// </summary>
    public static bool WorldColumnIsTessellated(
        bool cameraYChunkLoaded, bool surfaceChunkLoaded)
    {
        _ = cameraYChunkLoaded;
        return surfaceChunkLoaded;
    }

    /// <summary>
    /// At least one vanilla map-chunk covering this block AABB is present.
    /// </summary>
    public static bool AnyMapChunkLoaded(
        int minX, int maxXExclusive, int minZ, int maxZExclusive,
        int chunkSize, Func<int, int, bool> mapChunkLoaded)
    {
        if (chunkSize <= 0 || maxXExclusive <= minX || maxZExclusive <= minZ)
            return false;
        int cx0 = FloorDiv(minX, chunkSize);
        int cz0 = FloorDiv(minZ, chunkSize);
        int cx1 = FloorDiv(maxXExclusive - 1, chunkSize);
        int cz1 = FloorDiv(maxZExclusive - 1, chunkSize);
        for (int cz = cz0; cz <= cz1; cz++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                if (mapChunkLoaded(cx, cz)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Every vanilla map-chunk that covers this block AABB is present.
    /// </summary>
    public static bool AllMapChunksLoaded(
        int minX, int maxXExclusive, int minZ, int maxZExclusive,
        int chunkSize, Func<int, int, bool> mapChunkLoaded)
    {
        if (chunkSize <= 0 || maxXExclusive <= minX || maxZExclusive <= minZ)
            return false;
        int cx0 = FloorDiv(minX, chunkSize);
        int cz0 = FloorDiv(minZ, chunkSize);
        int cx1 = FloorDiv(maxXExclusive - 1, chunkSize);
        int cz1 = FloorDiv(maxZExclusive - 1, chunkSize);
        for (int cz = cz0; cz <= cz1; cz++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                if (!mapChunkLoaded(cx, cz)) return false;
            }
        }
        return true;
    }

    static int FloorDiv(int a, int b)
    {
        int q = a / b;
        return ((a ^ b) < 0 && a % b != 0) ? q - 1 : q;
    }

    public static bool ChildCanReplaceParent(
        int level, bool hasData, int capturedColumns, bool hasMesh)
    {
        if (!hasData) return false;
        if (capturedColumns == 0) return true;

        int full = LodSection.GridSize * LodSection.GridSize;
        if (level == 0 && capturedColumns < full) return false;

        return hasMesh;
    }

    /// <summary>
    /// Completeness gate: a parent is still the only coverage until every child
    /// can replace it. That does not mean draw the parent as a square plate.
    /// </summary>
    public static bool PreferParentCoverage(bool hasParentMesh, bool childrenFullyReplaced) =>
        hasParentMesh && !childrenFullyReplaced;

    /// <summary>
    /// L0/L1 we already captured is the picture. Wanted-level is a camera
    /// window; it must not refuse to keep or remesh those tiles.
    /// </summary>
    public static bool KeepVisitedSurface(int level, bool hasData) =>
        hasData && level >= 0 && level <= 1;

    /// <summary>
    /// Intervening span rule. A visited L0/L1 that sits between the camera and
    /// land we are already drawing farther out is not optional: skipping it
    /// leaves a sky rectangle (or a bare wall next to a kept neighbour) in the
    /// middle of terrain that is otherwise there. Bounded to the lead cone so
    /// the 360-degree map does not explode into L0 draws nobody can see, and
    /// to tiles we have data for; incomplete L0 is still the renderer's skip.
    /// Past LeadConeFineScale the coarse parent is the picture.
    /// </summary>
    public static bool MustCoverIntervening(int level, bool hasData, bool inLeadCone, bool fartherLoaded,
        double nearDist = 0, double viewDistance = 0) =>
        KeepVisitedSurface(level, hasData) && inLeadCone && fartherLoaded
        && (viewDistance <= 0 || nearDist <= viewDistance * LeadConeFineScale);

    /// <summary>
    /// A tile's far corner lies short of the farthest mesh we hold: land is
    /// loaded past it, so it is intervening rather than the frontier.
    /// </summary>
    public static bool IsFartherLoaded(double nearDistance, int footprintBlocks, double farthestMeshedDistance) =>
        farthestMeshedDistance > 0 && nearDistance + footprintBlocks * 1.5 < farthestMeshedDistance;

    /// <summary>
    /// Gap fill. A captured footprint that nothing in its subtree drew this
    /// frame (unmeshed, evicted, still loading, refused by a horizon rule) is a
    /// gap, and the nearest ancestor with a resident mesh paints exactly that
    /// footprint with its own mip, clipped to the gap (see clipRect in the
    /// shader). Siblings that did draw are untouched, so nothing collapses to
    /// a coarser rung and nothing pops when the child finally arrives: it
    /// simply lands on top and the clip draw is dropped. Every rung may fill,
    /// horizon lead cone included - the cone ban stays a preference for
    /// descending into finer meshes, not a license to leave sky - because the
    /// coarse mip is the same captured land at lower resolution, and the only
    /// alternative here is a hole. Vanilla-owned ground is never filled: a gap
    /// that reaches into the vanilla bubble is loaded chunks, not sky.
    /// </summary>
    public static bool MayFillGapWithParent(
        int parentLevel, bool parentHasMesh, bool parentInsideVanilla, bool gapTouchesVanilla) =>
        parentHasMesh && !parentInsideVanilla && !gapTouchesVanilla && parentLevel >= 1;

    /// <summary>
    /// After a parent walked its children and none of them drew, may it paint
    /// its own mesh over the whole footprint? Not when any child drew (the
    /// 0.7.20 no-box rule), not when the visual cap or the visited hold wants
    /// the children, not without a drawable mesh - and not when the footprint
    /// reaches into the vanilla bubble. Children there stepped aside because
    /// loaded chunks own that ground, not because anything was missing, and a
    /// whole L1 mip on top of them is a 2-block staircase of max-height columns
    /// and solid tree pillars right next to the player. Such a parent fills
    /// only the gaps its subtree reported, clipped, like any other.
    /// </summary>
    public static bool MayPaintWholeAfterDescent(
        bool anyChildDrew, bool drawableCoarse, bool forcedDetail, bool holdVisitedL0,
        bool touchesVanilla) =>
        !anyChildDrew && drawableCoarse && !forcedDetail && !holdVisitedL0 && !touchesVanilla;

    /// <summary>
    /// Never paint a coarse (L1+) WHOLE footprint when the camera is inside it.
    /// Descend and draw children; clip-fill only gaps that fail VanillaOwnsKey.
    /// Far L1/L2 whose near edge is past the coverage radius may still draw whole.
    /// Not a blanket "nearDist &lt; view distance" ban  -  that hid far children of
    /// a tile you are standing in (0.7.51 rectangle / chunkster).
    /// vanillaOwns defaults true so spawn-plate tests stay valid. When vanilla
    /// is NOT drawing that tile, a mid-ground L1 whose near edge sits inside
    /// the 0.55 disc is still land: refusing it at altitude punched 128x128
    /// sky squares. nearDist == 0 stays blocked for L1+ (underfoot / spawn).
    /// </summary>
    public static bool MaySubmitCoarseWhole(
        int level, double nearDist, double vanillaCoverageRadius, bool vanillaOwns = true)
    {
        if (level < 1) return true;
        if (nearDist >= vanillaCoverageRadius) return true;
        return !vanillaOwns && nearDist > 0;
    }

    /// <summary>
    /// Idle remesh. Already-meshed land waits for a 64-block origin shift, except
    /// a peek / provisional section: that mesh is terrain-only cubes until the
    /// real chunk recaptures, and spawn never walks a tile.
    /// </summary>
    public static bool ShouldRemeshWhileIdle(
        bool windowMoved, bool hasMesh, bool provisional) =>
        windowMoved || !hasMesh || provisional;

    /// <summary>
    /// Whether an L0 that has captured only some of its columns draws its own
    /// mesh. Its captured quadrants are the finest picture we hold of them;
    /// the parent mip has nothing more there. Only vanilla-owned ground hides
    /// it, and the never-captured quadrants stay whatever the ancestors fill.
    /// </summary>
    public static bool DrawIncompleteL0(bool hasMesh, bool insideVanilla) =>
        hasMesh && !insideVanilla;

    /// <summary>
    /// Most gap draws submitted per frame. A fresh world with thousands of
    /// tiles in flight must not turn into thousands of clipped parent draws;
    /// past the cap the farthest gaps wait a frame, which is still land next
    /// frame rather than sky.
    /// </summary>
    public const int MaxGapDrawsPerFrame = 1024;

    /// <summary>
    /// Ask for the same-quality GPU mesh of visited L0/L1 inside the 1.0x draw
    /// ring even when WantedLevel wants something coarser. Far visited land
    /// meshes at the wanted rung instead; the keep-circle still holds meshes
    /// we already uploaded. Intervening land in the lead cone is the exception:
    /// it is requested however far out it sits.
    /// </summary>
    public static bool RequestVisitedKeepMesh(
        int level, bool hasMesh, bool hasData, bool insideVanilla,
        double distance, double viewDistanceAnchor,
        bool inLeadCone = false, bool fartherLoaded = false) =>
        !hasMesh && !insideVanilla && KeepVisitedSurface(level, hasData)
        && (IsDrawFullDetail(distance, viewDistanceAnchor)
            || MustCoverIntervening(level, hasData, inLeadCone, fartherLoaded,
                distance, viewDistanceAnchor));

    /// <summary>
    /// Missing wanted-level (L2+) parent inside the keep-circle. Not L0 at 2×
    /// (that was the turn hitch). Walking starts these so the trail is land.
    /// </summary>
    public static bool RequestKeepCircleParent(
        int level, bool hasMesh, bool hasData, bool insideVanilla,
        double distance, double viewDistanceAnchor, int wantedLevel)
    {
        if (hasMesh || insideVanilla || !hasData) return false;
        if (level < 2) return false;
        if (!IsNearVisitedTrail(distance, viewDistanceAnchor)) return false;
        return level == wantedLevel || level == wantedLevel + 1;
    }

    /// <summary>
    /// Walk into children that already hold captured land inside the 1.0x draw
    /// ring, even if this node is coarser than WantedLevel. Past that ring the
    /// parent mesh is what we draw, as long as it is a real mesh. When this
    /// node could not stop on its own mesh and its children are intervening
    /// land in the lead cone, keep walking so they are not left as sky.
    /// </summary>
    public static bool DescendForVisitedKeep(
        int level, bool childHasVisitedSurface, double distance, double viewDistanceAnchor,
        bool inLeadCone = false, bool fartherLoaded = false) =>
        level > 0 && childHasVisitedSurface
        && (IsDrawFullDetail(distance, viewDistanceAnchor)
            || (inLeadCone && fartherLoaded
                && (viewDistanceAnchor <= 0 || distance <= viewDistanceAnchor * LeadConeFineScale)));

    /// <summary>
    /// Lead-cone L0/L1 preference only inside this scale of view distance.
    /// Past it, turning would promote every behind-camera L2 plate into a
    /// field of 64x64 meshes (opaque 20-38ms, tick 50-117ms on 0.7.67).
    /// </summary>
    public const float LeadConeFineScale = 1.5f;

    /// <summary>
    /// Farthest we hand off in the lead cone when a companion is actually
    /// drawing past us, as a multiple of view distance. 3x is our L1 hills
    /// through the fog band. Past it Farseer's heightmaps are the cheap
    /// silhouettes. When the companion is off we keep DV land-like cover
    /// instead of stopping into empty sky.
    /// </summary>
    public const float HorizonDrawScale = 3f;

    public static double HorizonDrawDistance(double viewDistance) =>
        viewDistance <= 0 ? 0 : viewDistance * HorizonDrawScale;

    public static bool PastHorizonDraw(double nearDist, double viewDistance) =>
        viewDistance > 0 && nearDist > HorizonDrawDistance(viewDistance);

    /// <summary>
    /// Empty-stop past HorizonDrawScale only when a companion is drawing that
    /// band. Farseer off means DrawAfterCompanion is false: keep our cover.
    /// </summary>
    public static bool PastHorizonEmptyStop(
        double nearDist, double viewDistance, bool companionDrawing) =>
        companionDrawing && PastHorizonDraw(nearDist, viewDistance);

    /// <summary>
    /// Degrees past each frustum edge that still count as in front: never draw
    /// L2+ (including plates), and start meshing children so land is ready
    /// before the player finishes turning. Only L0 and land-like L1 draw in
    /// this cone. Behind it a cheap parent (even a plate) may stay as a
    /// stand-in so the 360-degree map does not explode into L0 draws the
    /// camera cannot see.
    /// </summary>
    public const float LeadConeDegrees = 15f;

    /// <summary>
    /// Coarsest rung that may draw inside the lead cone. L2 and coarser,
    /// even with enough SurfaceRelief to pass IsLandLikeCoarseMesh, still
    /// reads as a cake shelf on the horizon. Those stand-ins are only
    /// allowed behind the camera.
    /// </summary>
    public const int LeadConeMaxDrawLevel = 1;

    /// <summary>
    /// Coarsest rung that may whole-cover a hole in the lead cone while
    /// children remesh. L1 is always fine (LeadConeMaxDrawLevel). Land-like
    /// L2 may temporary-cover when PreferParentCoverage says children are
    /// not ready. L3+ never whole-submits in the cone  -  that is the cake
    /// plate / stacked slab look. Holes that only have an L3+ ancestor use
    /// AddGap + clipped fill, or wait for L1 remesh.
    /// </summary>
    public const int LeadConeMaxCoverLevel = 2;

    /// <summary>
    /// Pitch below the horizon (LookDownAmount) at which the lead cone is
    /// the ground, not the skyline. 0.55 is ~33 deg and 0.82 is ~55 deg:
    /// both still leave a strip of sky in frame, and the hills in front
    /// already turned into blocks. 0.92 is ~67 deg: skyline is gone on a
    /// typical FOV, not yet nadir. Horizon bans L2+ so a 256-block shelf
    /// does not sit on the hills; looking straight down that same ban left
    /// 64x64 sky squares wherever an L0 was incomplete or unmeshed. At or
    /// above this pitch, a parent mesh is coverage.
    /// </summary>
    public const float LookDownCoarseFill = 0.92f;

    /// <summary>
    /// 0 until LookDownCoarseFill, 1 looking straight down. Skip-disc
    /// shrink and the shader near-sink use this so a skyline pan does not
    /// already drop to cubes.
    /// </summary>
    public static float LookDownSteepAmount(double lookDown01)
    {
        lookDown01 = Math.Clamp(lookDown01, 0, 1);
        if (lookDown01 <= LookDownCoarseFill) return 0f;
        return (float)((lookDown01 - LookDownCoarseFill) / (1.0 - LookDownCoarseFill));
    }

    /// <summary>
    /// True when the lead-cone shelf ban still applies. Looking down is
    /// not the horizon: inLeadCone is true for almost every tile, but L2
    /// on the ground is land, not a cake plate against the sky.
    /// </summary>
    public static bool HorizonLeadCone(bool inLeadCone, float lookDown01 = 0) =>
        inLeadCone && lookDown01 < LookDownCoarseFill;

    /// <summary>
    /// L0 walk inside the lead cone. Past LeadConeFineScale we still ban L2
    /// shelves on the skyline, but we stop at L1 instead of promoting every
    /// 64x64. That was the turn hitch.
    /// </summary>
    public static bool HorizonLeadConeFine(bool inLeadCone, float lookDown01,
        double nearDist, double viewDistance) =>
        HorizonLeadCone(inLeadCone, lookDown01)
        && (viewDistance <= 0 || nearDist <= viewDistance * LeadConeFineScale);

    /// <summary>
    /// Captured columns at or below this (one quadrant of a 64x64 grid) is a
    /// thin 1-of-4 slice, not a land-like coarse mesh.
    /// </summary>
    public const int CoarseSparseColumnLimit = (LodSection.GridSize * LodSection.GridSize) / 4;

    /// <summary>
    /// Minimum SurfaceRelief for an L1+ mesh to count as land rather than a
    /// slab. Scales with section footprint: a 256-block L2 that only varies a
    /// couple of blocks is a box; a mountain L2 with tens of blocks is land.
    /// L1: 4, L2: 8, L3: 16, L4+: 24.
    /// </summary>
    public static int MinLandLikeRelief(int level)
    {
        if (level < 1) return 0;
        int footprint = LodSection.SectionBlocks << level;
        return Math.Max(4, Math.Min(24, footprint / 32));
    }

    /// <summary>
    /// L0 is not gated here (IncompleteL0 skip stays in the renderer). L1+
    /// must have surface bounds, enough captured columns, and relief at or
    /// above MinLandLikeRelief. Missing any of those is a parent plate: do
    /// not draw it in front of the camera.
    /// </summary>
    public static bool IsLandLikeCoarseMesh(
        int level, bool hasSurfaceBounds, int surfaceRelief, int capturedColumns)
    {
        if (level < 1) return true;
        if (!hasSurfaceBounds) return false;
        if (capturedColumns <= CoarseSparseColumnLimit) return false;
        return surfaceRelief >= MinLandLikeRelief(level);
    }

    /// <summary>
    /// Draw walk: children only when they are coarse enough to reach wanted,
    /// or we are still inside the 1.0x ring. A missing parent mesh is one-rung
    /// hole-fill (draw L1 children of a missing L2), not a walk all the way to L0
    /// unless that L0 already has data. Captured L0/L1 is the land the player
    /// already stood on; refusing those at range is a sky rectangle, not a saving.
    /// In the lead cone at horizon pitch L2+ is never coverage: walk L1, and
    /// past 1.5x also walk L2 so an L3 cannot skip four pads into sky. Looking
    /// down, the cone is the ground; a parent mesh is coverage and this does
    /// not force empty L0. A captured child that is intervening land in the
    /// lead cone is always visited: the parent did not stop on a mesh of its
    /// own (or we would not be here), so refusing the child is a hole, not a
    /// saving.
    /// </summary>
    public static bool ShouldVisitChildForDraw(
        int childLevel, int wanted, bool drawFullDetail, bool parentHasMesh,
        bool parentLandLike = true, bool inLeadCone = false, float lookDown01 = 0,
        bool childHasData = false, bool fartherLoaded = false,
        double nearDist = 0, double viewDistance = 0,
        bool companionDrawing = false)
    {
        if (drawFullDetail) return true;
        if (HorizonLeadCone(inLeadCone, lookDown01)
            && PastHorizonEmptyStop(nearDist, viewDistance, companionDrawing))
            return false;
        if (MustCoverIntervening(childLevel, childHasData, inLeadCone, fartherLoaded,
                nearDist, viewDistance)) return true;
        // Fine ring: L0/L1 in front, or any child under a plate.
        if (HorizonLeadConeFine(inLeadCone, lookDown01, nearDist, viewDistance)
            && (childLevel <= LeadConeMaxDrawLevel || !parentLandLike)) return true;
        // Past 1.5x: still walk L1 (no L2 shelf) and L2 (MayLeadConeCoarseCover).
        // L3 used to skip every L2 child with no AddGap — a 256-block hole at
        // ~1.9x VD (fly to the pad and it fills; back off and it is empty again).
        // Do not walk L0 here: that is the turn hitch.
        if (HorizonLeadCone(inLeadCone, lookDown01)
            && (childLevel == LeadConeMaxDrawLevel
                || childLevel == LeadConeMaxCoverLevel
                || !parentLandLike)) return true;
        if (childLevel >= wanted) return true;
        // Hole-fill of one rung. Missing L2 visits L1; it does not visit L0.
        if (!parentHasMesh && childLevel >= Math.Max(0, wanted - 1)) return true;
        if (childHasData && childLevel <= 1 && !parentHasMesh) return true;
        return false;
    }

    /// <summary>
    /// This node has a GPU mesh, we are outside the 1.0x ring, and this rung is
    /// not coarser than wanted (L1 hole-fill when wanted is 2+ still counts).
    /// In the lead cone L0/L1 land-like meshes may stop; land-like L2 may
    /// also stop past the 1.5x fine ring (MayLeadConeCoarseCover). L3+ never
    /// stops in the cone. Behind the lead cone a plate may stop (cheap
    /// stand-in; the tight frustum still culls the submit).
    /// </summary>
    public static bool StopDescentAtAvailableRung(
        int level, int wanted, bool drawFullDetail, bool hasMesh,
        bool landLike, bool inLeadCone, float lookDown01 = 0,
        double nearDist = 0, double viewDistance = 0,
        bool preferParentCoverage = false) =>
        hasMesh && !drawFullDetail && level >= 1 && level <= Math.Max(wanted, 1)
        && !(HorizonLeadCone(inLeadCone, lookDown01) && level > LeadConeMaxDrawLevel
            && !MayLeadConeCoarseCover(level, landLike, inLeadCone, lookDown01,
                nearDist, viewDistance, preferParentCoverage))
        && (landLike || !HorizonLeadCone(inLeadCone, lookDown01));

    /// <summary>
    /// Lead-cone exception for a land-like coarse parent that must cover until
    /// its children can replace it. PreferParentCoverage is the completeness
    /// gate for L1/L2 only. Past the fine ring, land-like L2 alone may also
    /// cover so turning does not leave sky while L0/L1 are still meshing.
    /// Non-land plates stay banned. L3+ never whole-covers in the cone  -  that
    /// was the 0.7.72 cake-plate regression; those holes use AddGap + clipRect.
    /// </summary>
    public static bool MayLeadConeCoarseCover(
        int level, bool landLike, bool inLeadCone, float lookDown01,
        double nearDist, double viewDistance, bool preferParentCoverage)
    {
        if (!landLike || !HorizonLeadCone(inLeadCone, lookDown01)) return false;
        if (level <= LeadConeMaxDrawLevel) return true;
        // Hard ban: never whole L3+ plates in the lead/view cone.
        if (level > LeadConeMaxCoverLevel) return false;
        // L2 only from here. PreferParentCoverage = children not ready.
        if (preferParentCoverage) return true;
        // Soften past the fine ring: land-like L2 only.
        return level == 2
            && !HorizonLeadConeFine(inLeadCone, lookDown01, nearDist, viewDistance);
    }

    /// <summary>
    /// Whether CollectDrawNodes may add this L1+ mesh to the draw list.
    /// Never over vanilla-owned ground. Never an L2+ flat plate in the lead cone.
    /// L1 plates (ocean/beach/plains) may cover in the cone when
    /// PreferParentCoverage says children cannot replace them. L2+ in the lead
    /// cone is refused unless MayLeadConeCoarseCover says the land-like L2
    /// parent is temporary cover (PreferParentCoverage, or land-like L2 past
    /// the fine ring). L3+ never qualifies. Looking down, L2+ and even a plains
    /// plate may cover so incomplete L0 does not punch sky. Behind the cone a
    /// plate may stay as a cheap stand-in. L0 is not a coarse parent;
    /// IncompleteL0 is a separate rule (DrawIncompleteL0). A refused parent
    /// must still register a gap so ancestors can clip-fill.
    /// </summary>
    public static bool MayDrawCoarseParent(
        int level, bool insideVanilla, bool landLike, bool inLeadCone,
        float lookDown01 = 0, double nearDist = 0, double viewDistance = 0,
        bool preferParentCoverage = false)
    {
        if (level < 1) return true;
        if (insideVanilla) return false;
        if (HorizonLeadCone(inLeadCone, lookDown01) && level > LeadConeMaxDrawLevel)
        {
            if (MayLeadConeCoarseCover(level, landLike, inLeadCone, lookDown01,
                    nearDist, viewDistance, preferParentCoverage))
                return true;
            return false;
        }
        if (!landLike && HorizonLeadCone(inLeadCone, lookDown01))
        {
            // L1 ocean/beach/plains fail IsLandLikeCoarseMesh (relief < 4).
            // Banning those plates in the cone left 128x128 sky squares between
            // forest hills after you flew up. Children that can replace the
            // parent still win the walk; keep the plate only when they cannot.
            return level == LeadConeMaxDrawLevel && preferParentCoverage;
        }
        return true;
    }

    /// <summary>
    /// A coarse parent is the same hills only if its surface band overlaps the
    /// children and is not a thin shelf through them. Missing bounds, a parent
    /// sitting above the real hills (sky gap), or a flat Y that would slice the
    /// hill is a plate: hide it and draw the children.
    /// </summary>
    public static bool ParentFollowsChildSurface(
        bool parentHasBounds, int parentYMin, int parentYMax,
        bool childrenHaveBounds, int childYMin, int childYMax)
    {
        if (!parentHasBounds || !childrenHaveBounds) return false;
        if (parentYMax < childYMin || parentYMin > childYMax) return false;
        int parentRelief = parentYMax - parentYMin;
        int childRelief = childYMax - childYMin;
        if (childRelief > parentRelief && parentRelief < Math.Max(4, childRelief / 2))
            return false;
        return true;
    }

    /// <summary>
    /// Too fine for this camera window, and the parent already has a real mesh
    /// to draw instead. Stamp lastSelectedFrame at the caller so keep-circle
    /// eviction does not dump the finer mesh. A plate in the lead cone is not
    /// a real parent, and neither is L2+: keep drawing L0/L1 across the 1.0x
    /// coarsen ring so the horizon is not a shelf. Intervening land in the
    /// lead cone never skips: the parent that "has a mesh" is only drawn when
    /// none of its children drew, so a skipped sibling is a hole.
    /// </summary>
    public static bool SkipDrawTooFine(
        int level, int wanted, bool drawFullDetail, bool parentHasMesh,
        bool parentLandLike = true, bool inLeadCone = false, float lookDown01 = 0,
        bool mustCover = false, double nearDist = 0, double viewDistance = 0)
    {
        if (level == 0) return false;
        if (drawFullDetail || level >= wanted || !parentHasMesh) return false;
        if (mustCover) return false;
        if (HorizonLeadCone(inLeadCone, lookDown01) && !parentLandLike) return false;
        if (HorizonLeadCone(inLeadCone, lookDown01) && level >= LeadConeMaxDrawLevel)
            return false;
        return true;
    }

    /// <summary>
    /// 0.7.66 yielded past 1.5x view distance so Farseer could occupy that band.
    /// Farseer did not fill it. We punched 33 holes in our own draw (31 walked)
    /// and the far band was sky. Do not yield. Draw everything we have.
    /// </summary>
    public static bool YieldFootprintToCompanion(bool companionOn, bool hasRealSurface) =>
        YieldFootprintToCompanion(companionOn, hasRealSurface, 0, int.MaxValue, 0, 0);

    public static bool YieldFootprintToCompanion(
        bool companionOn,
        bool hasRealSurface,
        int liveMeshes,
        int maxMeshes,
        double nearestEdge,
        double viewDistance)
    {
        _ = companionOn;
        _ = hasRealSurface;
        _ = liveMeshes;
        _ = maxMeshes;
        _ = nearestEdge;
        _ = viewDistance;
        return false;
    }
}
