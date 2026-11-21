using System;
using System.Collections.Generic;
using System.Numerics;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Bounded, server-owned wildlife around up to four distinct exploration
/// areas. Nearby players share one region; widely separated players get
/// different navmeshes and independent herds. Native NavigationAgents would
/// bind the first surface in the scene, so newly spawned animals instead use
/// PeacefulAnimal's region-specific nav queries and Jolt SimpleMove.
/// </summary>
public sealed class PeacefulMobHabitat : Script
{
    public int MaxAnimals = 10;
    public float RoamRadius = 19f;
    public float SurfaceWidth = 48f;
    public int MaxRegions = 4;

    private sealed class Region
    {
        public readonly int Key;
        public GameObject? Navigation;
        public Vector3 Center;
        public float SpawnClock = 2.5f;
        public float RecenterClock;
        public float RebuildClock;
        public bool NeedsRebuild;
        public Vector3 DirtyMin, DirtyMax;
        public int SpawnSequence;
        public readonly List<GameObject> Animals = new();

        public Region(int key, Vector3 center)
        {
            Key = key;
            Center = center;
        }
    }

    private readonly Dictionary<int, Region> _regions = new();
    private readonly List<(int Key, Vector3 Position)> _players = new();
    private readonly List<(int Key, Vector3 Position)> _desired = new();
    private readonly List<int> _removed = new();
    private readonly List<GameObject> _dyingOrphans = new();
    private readonly Dictionary<long, float> _recentKills = new();
    private float _elapsed;
    private float _scanClock;
    private string _activeWorldId = "";

    public override void Start()
    {
        // Every peer must know the prefab names before the host spawns.
        Network.RegisterPrefab("TallowcraftSheep", "Prefabs/Sheep.tprefab");
        Network.RegisterPrefab("TallowcraftPig", "Prefabs/Pig.tprefab");
    }

    public void NoteKill(Vector3 position)
    {
        if (!Scene.IsServer) return;
        _recentKills[CellKey(position.X, position.Z)] = _elapsed + 45f;
    }

    private static long CellKey(float x, float z)
    {
        int cx = (int)MathF.Floor(x / WildlifeRules.SpatialCell);
        int cz = (int)MathF.Floor(z / WildlifeRules.SpatialCell);
        return ((long)cx << 32) | (uint)cz;
    }

    private static float DistanceXZ(Vector3 a, Vector3 b) =>
        WildlifeRegionRules.DistanceXZ(a, b);

    private void FindPlayerRegions()
    {
        _players.Clear();
        if (Network.PlayerObject(Scene, 0) is { IsValid: true } host)
            _players.Add((0, host.Transform.Position));
        else if (Scene.Find("Player") is { IsValid: true } offline)
            _players.Add((0, offline.Transform.Position));

        if (Scene.IsNetworked)
            foreach (int id in Network.ClientIds(Scene))
            {
                if (id == 0) continue;
                if (Network.PlayerObject(Scene, id) is { IsValid: true } peer)
                    _players.Add((id, peer.Transform.Position));
            }
        _players.Sort((a, b) => a.Key.CompareTo(b.Key));

        // Players close together share one navigator; the oldest (lowest
        // network client ID) is the stable region anchor until they separate.
        _desired.Clear();
        foreach (var player in _players)
        {
            bool covered = false;
            foreach (var existing in _desired)
                if (WildlifeRegionRules.Share(
                    existing.Position, player.Position))
                {
                    covered = true;
                    break;
                }
            if (!covered && _desired.Count <
                Math.Clamp(MaxRegions, 1, WildlifeRegionRules.MaxRegions))
                _desired.Add(player);
        }
    }

    private static bool Intersects(Region region, Vector3 min, Vector3 max) =>
        WildlifeRegionRules.IntersectsEdit(region.Center, min, max);

    private void NoticeEdits(BlockWorld world)
    {
        if (!world.TryDrainWildlifeEditBounds(out Vector3 min,
                out Vector3 max))
            return;
        foreach (Region region in _regions.Values)
        {
            if (!Intersects(region, min, max)) continue;
            if (!region.NeedsRebuild)
            {
                region.DirtyMin = min;
                region.DirtyMax = max;
            }
            else
            {
                region.DirtyMin = Vector3.Min(region.DirtyMin, min);
                region.DirtyMax = Vector3.Max(region.DirtyMax, max);
            }
            region.NeedsRebuild = true;
            region.RebuildClock = 1.25f; // wait for async mesh/collider uploads
        }
    }

    private void CreateNavigation(Region region, Vector3 position)
    {
        if (region.Navigation is { IsValid: true } old)
            old.Destroy();
        float side = Math.Clamp(SurfaceWidth, 36f, 48f);
        region.Center = position;
        GameObject nav = Scene.CreateGameObject(
            "Wildlife Navigation " + region.Key);
        nav.Transient = true;
        nav.Transform.Position = new Vector3(
            position.X - side*.5f, position.Y - 11f, position.Z - side*.5f);
        NavigationSurface surface = nav.AddComponent<NavigationSurface>();
        surface.Size = new Vector3(side, 24f, side);
        surface.CellSize = .6f;
        surface.AgentRadius = .4f;
        surface.AgentHeight = 1.3f;
        surface.MaxStep = AnimalCombatRules.StairClimbHeight;
        surface.MaxSlope = 48f;
        surface.IncludeRenderMeshes = false;
        surface.RebuildOnStart = true;
        region.Navigation = nav;
        region.NeedsRebuild = false;
        region.RecenterClock = 12f;
        region.SpawnClock = 2.5f;
        // Existing animals continue through the new region; no need to
        // recreate or duplicate their network identities.
        foreach (GameObject animal in region.Animals)
            if (animal.IsValid &&
                animal.GetComponent<PeacefulAnimal>() is { Dying: false } mob)
                mob.BindRegion(nav, 13 + region.Key);
    }

    private void RemoveRegion(Region region)
    {
        foreach (GameObject animal in region.Animals)
        {
            if (!animal.IsValid) continue;
            if (animal.GetComponent<PeacefulAnimal>()?.Dying == true)
                _dyingOrphans.Add(animal); // let the death-drop timer finish
            else
                animal.Destroy();
        }
        region.Animals.Clear();
        if (region.Navigation is { IsValid: true } nav)
            nav.Destroy();
        region.Navigation = null;
    }

    private void TickRegion(Region region, Vector3 focus,
        BlockWorld world, float dt)
    {
        region.SpawnClock -= dt;
        region.RecenterClock = MathF.Max(0f, region.RecenterClock - dt);
        region.RebuildClock = MathF.Max(0f, region.RebuildClock - dt);
        bool needsCenter = region.Navigation is not { IsValid: true } ||
            WildlifeRegionRules.Recenter(region.Center, focus,
                region.RecenterClock);
        if (needsCenter)
        {
            if (!world.ImmediateMobTerrainReady(focus)) return;
            CreateNavigation(region, focus);
            return;
        }

        if (region.NeedsRebuild && region.RebuildClock <= 0f)
        {
            // Collider uploads are asynchronous. Check the actual local 3x3
            // chunk volume again before asking Tallow to repath this region.
            if (world.ImmediateMobTerrainReady(region.Center) &&
                region.Navigation is { IsValid: true } nav)
            {
                NavigationSurface surface =
                    nav.GetComponent<NavigationSurface>();
                if (surface.IsValid &&
                    surface.Rebuild(region.DirtyMin -
                        new Vector3(2f), region.DirtyMax +
                        new Vector3(3f)))
                    region.NeedsRebuild = false;
                else
                    region.RebuildClock = 1.5f;
            }
            else
                region.RebuildClock = .75f;
        }

        if (region.SpawnClock > 0f) return;
        region.SpawnClock = 2.3f;

        for (int i = region.Animals.Count - 1; i >= 0; i--)
        {
            GameObject animal = region.Animals[i];
            if (!animal.IsValid)
            {
                region.Animals.RemoveAt(i);
                continue;
            }
            bool dying = animal.GetComponent<PeacefulAnimal>()?.Dying == true;
            if (!dying && DistanceXZ(animal.Transform.Position, region.Center) >
                WildlifeRegionRules.CullDistance)
            {
                animal.Destroy();
                region.Animals.RemoveAt(i);
            }
        }
        if (region.Animals.Count >= Math.Clamp(MaxAnimals, 0, 12) ||
            region.Navigation is not { IsValid: true } navObject ||
            region.NeedsRebuild)
            return;
        NavigationSurface navigation =
            navObject.GetComponent<NavigationSurface>();
        if (!navigation.IsValid) return;

        int index = region.SpawnSequence++;
        var random = new Random(unchecked(world.Seed * 157 +
            region.Key * 1301 + index * 719 + 117));
        if (!navigation.RandomPoint(region.Center,
            Math.Clamp(RoamRadius, 10f, 21f), random, out Vector3 ground))
            return;
        int x = (int)MathF.Floor(ground.X);
        int z = (int)MathF.Floor(ground.Z);
        int species = WildlifeRules.SpeciesAt(x, z, world.Seed);
        if (species == 0 || WorldWater.HasSurface(x, z, world.Seed))
            return;

        // Also avoid new spawns immediately beside any other player
        // (including one sharing the same navigation region).
        foreach (var player in _players)
            if (DistanceXZ(player.Position, ground) < 8f)
                return;

        long cell = CellKey(x, z);
        if (_recentKills.TryGetValue(cell, out float cooldown) &&
            cooldown > _elapsed) return;
        foreach (Region other in _regions.Values)
        foreach (GameObject animal in other.Animals)
        {
            if (!animal.IsValid) continue;
            Vector3 position = animal.Transform.Position;
            if (CellKey(position.X, position.Z) == cell ||
                DistanceXZ(position, ground) < 6f)
                return;
        }

        float centerY = world.SafeSpawnHeight(ground.X, ground.Z, 1.2f, .35f);
        if (MathF.Abs(centerY - (ground.Y + .6f)) > 2.3f)
            return;
        int floorY = (int)MathF.Floor(centerY - .85f);
        int groundType = new BlockState(world.BlockAt(new BlockPosition(
            x, floorY, z))).Type;
        if (!WildlifeRules.CanSpawnOn(species, groundType, x, z, world.Seed))
            return;

        string prefab = species == WildlifeRules.Sheep ?
            "Prefabs/Sheep.tprefab" : "Prefabs/Pig.tprefab";
        GameObject? spawned = Prefab.Instantiate(Scene, prefab,
            new Vector3(ground.X, centerY, ground.Z), Quaternion.Identity);
        if (spawned is not { IsValid: true } mob) return;
        if (mob.GetComponent<PeacefulAnimal>() is { } animalScript)
            animalScript.BindRegion(navObject, world.Seed);
        region.Animals.Add(mob);
    }

    public override void LocalUpdate(float deltaTime)
    {
        if (!Scene.IsServer) return;
        BlockWorld? world =
            Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (world == null || world.WorldIdentity.Length == 0) return;
        if (_activeWorldId != world.WorldIdentity)
        {
            // Changing the world seed/identity invalidates every previous
            // animal and baked navigation surface, even without a Play restart.
            foreach (Region previous in _regions.Values)
                RemoveRegion(previous);
            foreach (GameObject dying in _dyingOrphans)
                if (dying.IsValid) dying.Destroy();
            _regions.Clear();
            _dyingOrphans.Clear();
            _recentKills.Clear();
            _desired.Clear();
            _activeWorldId = world.WorldIdentity;
            _scanClock = 0f;
        }
        float dt = Math.Clamp(deltaTime, 0f, .25f);
        _elapsed += dt;
        NoticeEdits(world);
        _scanClock -= dt;
        if (_scanClock <= 0f)
        {
            _scanClock = .35f;
            FindPlayerRegions();
            _removed.Clear();
            foreach (var existing in _regions)
            {
                bool active = false;
                foreach (var desired in _desired)
                    if (desired.Key == existing.Key)
                    {
                        active = true;
                        break;
                    }
                if (!active) _removed.Add(existing.Key);
            }
            foreach (int key in _removed)
            {
                RemoveRegion(_regions[key]);
                _regions.Remove(key);
            }
            foreach (var desired in _desired)
                if (!_regions.ContainsKey(desired.Key))
                    _regions.Add(desired.Key,
                        new Region(desired.Key, desired.Position));

            for (int i = _dyingOrphans.Count - 1; i >= 0; i--)
                if (!_dyingOrphans[i].IsValid)
                    _dyingOrphans.RemoveAt(i);
            var expired = new List<long>();
            foreach (var item in _recentKills)
                if (item.Value <= _elapsed) expired.Add(item.Key);
            foreach (long key in expired) _recentKills.Remove(key);
        }

        foreach (var desired in _desired)
            if (_regions.TryGetValue(desired.Key, out Region? region))
                TickRegion(region, desired.Position, world, dt);
    }

    public override void OnDestroy()
    {
        if (!Scene.IsServer) return;
        foreach (Region region in _regions.Values)
        {
            foreach (GameObject animal in region.Animals)
                if (animal.IsValid) animal.Destroy();
            if (region.Navigation is { IsValid: true } nav)
                nav.Destroy();
        }
        foreach (GameObject animal in _dyingOrphans)
            if (animal.IsValid) animal.Destroy();
        _regions.Clear();
        _dyingOrphans.Clear();
    }
}
