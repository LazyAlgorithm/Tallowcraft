using System;
using System.Collections.Generic;
using System.Numerics;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Server-owned roaming wildlife near the current exploration focus.
/// One bounded navigation surface follows the player in steps, rather
/// than being permanently baked at the original spawn. Density, biome,
/// collision and population limits are all enforced before instantiation.
/// A future milestone can stream multiple nav surfaces per distant player.
/// </summary>
public sealed class PeacefulMobHabitat : Script
{
    public int MaxAnimals = 10;
    public float RoamRadius = 19f;
    public float SurfaceWidth = 48f;

    private GameObject? _navigationObject;
    private readonly List<GameObject> _animals = new();
    private readonly Dictionary<long, float> _recentKills = new();
    private Vector3 _center;
    private float _spawnClock;
    private float _recenterClock;
    private float _elapsed;
    private int _spawnSequence;

    public override void Start()
    {
        Network.RegisterPrefab("TallowcraftSheep", "Prefabs/Sheep.tprefab");
        Network.RegisterPrefab("TallowcraftPig", "Prefabs/Pig.tprefab");
    }

    private static long CellKey(float x, float z)
    {
        int cx = (int)MathF.Floor(x / WildlifeRules.SpatialCell);
        int cz = (int)MathF.Floor(z / WildlifeRules.SpatialCell);
        return ((long)cx << 32) | (uint)cz;
    }

    private void CullAndPrune()
    {
        for (int i = _animals.Count - 1; i >= 0; i--)
        {
            GameObject animal = _animals[i];
            if (!animal.IsValid)
            {
                _animals.RemoveAt(i);
                continue;
            }
            Vector3 position = animal.Transform.Position;
            // Do not erase a dying animal before the server awards its loot.
            bool dying = animal.GetComponent<PeacefulAnimal>()?.Dying == true;
            if (dying)
                _recentKills[CellKey(position.X, position.Z)] =
                    _elapsed + 45f;
            float dist = Vector2.Distance(
                new Vector2(position.X, position.Z),
                new Vector2(_center.X, _center.Z));
            if (dist > 38f && !dying)
            {
                animal.Destroy();
                _animals.RemoveAt(i);
            }
        }
        if (_recentKills.Count > 0)
        {
            var expired = new List<long>();
            foreach (var item in _recentKills)
                if (item.Value <= _elapsed) expired.Add(item.Key);
            foreach (long key in expired) _recentKills.Remove(key);
        }
    }

    private void CreateNavigation(Vector3 position, BlockWorld world)
    {
        float side = Math.Clamp(SurfaceWidth, 36f, 48f);
        _center = position;
        GameObject nav = Scene.CreateGameObject("Roaming Wildlife Navigation");
        nav.Transient = true;
        nav.Transform.Position = new Vector3(
            _center.X - side * .5f, _center.Y - 11f, _center.Z - side * .5f);
        NavigationSurface surface = nav.AddComponent<NavigationSurface>();
        surface.Size = new Vector3(side, 24f, side);
        surface.CellSize = .6f;
        surface.AgentRadius = .4f;
        surface.AgentHeight = 1.3f;
        surface.MaxStep = AnimalCombatRules.StairClimbHeight;
        surface.MaxSlope = 48f;
        surface.IncludeRenderMeshes = false;
        surface.RebuildOnStart = true;
        _navigationObject = nav;
        _spawnClock = 2.5f;
        _recenterClock = 12f;
    }

    public override void LocalUpdate(float deltaTime)
    {
        if (!Scene.IsServer) return;
        var world = Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (world == null || string.IsNullOrEmpty(world.WorldIdentity))
            return;

        GameObject? player = Network.PlayerObject(Scene, 0) ??
            Scene.Find("Player");
        if (player is not { IsValid: true } focus) return;
        float dt = Math.Clamp(deltaTime, 0f, .25f);
        _elapsed += dt;
        _recenterClock = MathF.Max(0f, _recenterClock - dt);

        Vector3 playerPosition = focus.Transform.Position;
        bool noSurface = _navigationObject is not { IsValid: true };
        float distance = Vector2.Distance(
            new Vector2(playerPosition.X, playerPosition.Z),
            new Vector2(_center.X, _center.Z));

        if (noSurface || (distance > 24f && _recenterClock <= 0f))
        {
            // The terrain mesh and its near Jolt colliders must be uploaded
            // at the new position before the navmesh bakes from colliders.
            if (!world.ImmediateMobTerrainReady(playerPosition)) return;
            if (_navigationObject is { IsValid: true } old)
            {
                old.Destroy();
                _navigationObject = null;
            }
            CreateNavigation(playerPosition, world);
            CullAndPrune();
            return;
        }

        // Prune each spawn window, not on every render frame.
        _spawnClock -= dt;
        if (_spawnClock > 0f) return;
        _spawnClock = 2.2f;
        CullAndPrune();
        if (_animals.Count >= Math.Clamp(MaxAnimals, 0, 12))
            return;

        if (_navigationObject is not { IsValid: true } navObject)
            return;
        NavigationSurface nav = navObject.GetComponent<NavigationSurface>();
        if (!nav.IsValid) return;

        // All attempted positions are derived from the world seed and the
        // spawn-attempt index; worker completion order cannot affect species.
        int index = _spawnSequence++;
        var random = new Random(unchecked(world.Seed * 157 + index * 719 + 117));
        if (!nav.RandomPoint(_center,
                Math.Clamp(RoamRadius, 10f, 21f), random,
                out Vector3 ground))
            return;

        int x = (int)MathF.Floor(ground.X);
        int z = (int)MathF.Floor(ground.Z);
        int species = WildlifeRules.SpeciesAt(x, z, world.Seed);
        if (species == 0) return;

        // Prefer visible wild animals, rather than placing a herd directly
        // on top of the player or stacking several mobs on one grass patch.
        if (Vector2.Distance(new Vector2(x, z),
                new Vector2(playerPosition.X, playerPosition.Z)) < 8f)
            return;

        long key = CellKey(x, z);
        if (_recentKills.TryGetValue(key, out float until) &&
            until > _elapsed)
            return;
        foreach (GameObject animal in _animals)
        {
            if (!animal.IsValid) continue;
            Vector3 position = animal.Transform.Position;
            if (CellKey(position.X, position.Z) == key ||
                Vector2.DistanceSquared(new Vector2(position.X, position.Z),
                    new Vector2(ground.X, ground.Z)) < 6f * 6f)
                return;
        }

        float centerY = world.SafeSpawnHeight(ground.X, ground.Z, 1.2f, .35f);
        if (MathF.Abs(centerY - (ground.Y + .6f)) > 2.3f)
            return; // A steep edge, cave or missing collider is unsafe.
        int floorY = (int)MathF.Floor(centerY - .75f);
        int groundType = new BlockState(world.BlockAt(new BlockPosition(
            x, floorY, z))).Type;
        if (!WildlifeRules.CanSpawnOn(species, groundType, x, z, world.Seed))
            return;

        string prefab = species == WildlifeRules.Sheep ?
            "Prefabs/Sheep.tprefab" : "Prefabs/Pig.tprefab";
        GameObject? spawned = Prefab.Instantiate(Scene, prefab,
            new Vector3(ground.X, centerY, ground.Z),
            Quaternion.Identity);
        if (spawned is { IsValid: true } mob)
            _animals.Add(mob);
    }

    public override void OnDestroy()
    {
        if (!Scene.IsServer) return;
        foreach (GameObject animal in _animals)
            if (animal.IsValid) animal.Destroy();
        _animals.Clear();
        if (_navigationObject is { IsValid: true } nav) nav.Destroy();
    }
}
