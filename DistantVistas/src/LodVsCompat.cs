using System.Collections.Concurrent;
using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace DistantVistas;

/// <summary>
/// Vintage Story 1.22.7 client API differences vs 1.22.5: <c>UpdatePartitioning</c>
/// is not always a public Entity method, and <c>LoadedEntities</c> lives on
/// <see cref="IServerWorldAccessor"/> (not <see cref="IWorldAccessor"/>).
/// Reflection keeps 1.22.5 and 1.22.7 both compiling.
/// </summary>
public static class LodVsCompat
{
    static readonly ConcurrentDictionary<Type, MethodInfo?> PartitioningByType = new();
    const BindingFlags InstanceAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void TryUpdatePartitioning(Entity? entity)
    {
        if (entity == null) return;
        MethodInfo? method = PartitioningByType.GetOrAdd(entity.GetType(), FindUpdatePartitioning);
        if (method == null) return;
        try { method.Invoke(entity, null); } catch { }
    }

    static MethodInfo? FindUpdatePartitioning(Type type)
    {
        Type? walk = type;
        while (walk != null && walk != typeof(object))
        {
            MethodInfo? method = walk.GetMethod("UpdatePartitioning", InstanceAny, binder: null, types: Type.EmptyTypes, modifiers: null);
            if (method != null) return method;
            walk = walk.BaseType;
        }
        return null;
    }

    /// <summary>
    /// Server worlds expose LoadedEntities on <see cref="IServerWorldAccessor"/>.
    /// Client worlds fall back to a LoadedEntities property/field on the live type.
    /// </summary>
    public static IDictionary<long, Entity>? TryGetLoadedEntities(IWorldAccessor? world)
    {
        if (world == null) return null;
        if (world is IServerWorldAccessor server)
        {
            try
            {
                IDictionary<long, Entity>? dict = server.LoadedEntities;
                if (dict != null) return dict;
            }
            catch { }
        }

        return AsEntityDictionary(ReadLoadedEntitiesObject(world));
    }

    static object? ReadLoadedEntitiesObject(IWorldAccessor world)
    {
        Type type = world.GetType();
        try
        {
            object? value = type.GetProperty("LoadedEntities", InstanceAny)?.GetValue(world);
            if (value != null) return value;
        }
        catch { }
        try { return type.GetField("LoadedEntities", InstanceAny)?.GetValue(world); }
        catch { return null; }
    }

    static IDictionary<long, Entity>? AsEntityDictionary(object? value)
    {
        if (value is IDictionary<long, Entity> typed) return typed;
        return null;
    }

    public static void TryIndexLoadedEntity(IWorldAccessor? world, Entity entity)
    {
        IDictionary<long, Entity>? dict = TryGetLoadedEntities(world);
        if (dict == null) return;
        try { dict[entity.EntityId] = entity; } catch { }
    }

    public static void TryRemoveLoadedEntity(IWorldAccessor? world, long entityId)
    {
        IDictionary<long, Entity>? dict = TryGetLoadedEntities(world);
        if (dict == null) return;
        try { dict.Remove(entityId); } catch { }
    }
}
