using System;
using System.Collections.Generic;
using System.Numerics;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Small, server-owned peaceful-animal demonstration habitat. Tallow's
/// NavigationSurface and BehaviorTreeRunner do pathfinding and decisions.
/// This bounded surface is deliberately not a claim of world-scale navigation:
/// runtime navmeshes currently do not stream with terrain LOD.
/// </summary>
public sealed class PeacefulMobHabitat : Script
{
    public int MaxAnimals = 6;
    public float RoamRadius = 13f;
    public float SurfaceWidth = 40f;

    private GameObject? _navigationObject;
    private readonly List<GameObject> _animals = new();
    private Vector3 _center;
    private float _spawnClock;

    public override void Start()
    {
        // Every peer registers the same asset names; only the server spawns.
        Network.RegisterPrefab("TallowcraftSheep", "Prefabs/Sheep.tprefab");
        Network.RegisterPrefab("TallowcraftPig", "Prefabs/Pig.tprefab");
    }

    public override void LocalUpdate(float deltaTime)
    {
        if (!Scene.IsServer) return;
        var world = Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (world == null || string.IsNullOrEmpty(world.WorldIdentity)) return;

        GameObject? player = Network.PlayerObject(Scene, 0) ?? Scene.Find("Player");
        if (player is not { IsValid: true }) return;
        if (_navigationObject is not { IsValid: true })
        {
            // Avoid baking an empty NavMesh while the initial voxel meshes
            // and near-player Jolt colliders are still streaming in.
            if (!world.ImmediateMobTerrainReady(player.Value.Transform.Position))
                return;
            _center = player.Value.Transform.Position;
            float side = Math.Clamp(SurfaceWidth, 16f, 48f);
            GameObject nav = Scene.CreateGameObject("Peaceful Mob Navigation");
            nav.Transient = true;
            nav.Transform.Position = new Vector3(
                _center.X - side * .5f, _center.Y - 10f, _center.Z - side * .5f);
            NavigationSurface surface = nav.AddComponent<NavigationSurface>();
            surface.Size = new Vector3(side, 22f, side);
            surface.CellSize = .6f;
            surface.AgentRadius = .4f;
            surface.AgentHeight = 1.3f;
            surface.MaxStep = .6f;
            surface.MaxSlope = 48f;
            surface.IncludeRenderMeshes = false; // mesh colliders, not distant LOD
            surface.RebuildOnStart = true; // component's Start bakes after creation
            _navigationObject = nav;
            _spawnClock = 2f;
            return;
        }

        _spawnClock -= deltaTime;
        if (_spawnClock > 0 || _animals.Count >= Math.Clamp(MaxAnimals, 0, 12))
            return;
        _spawnClock = 2f;

        NavigationSurface navigation = NavigationSurface.Find(Scene);
        if (!navigation.IsValid) return;

        // A reproducible sequence: animals do not depend on render worker order.
        int index = _animals.Count;
        var random = new Random(unchecked(world.Seed * 157 + index * 719 + 117));
        if (!navigation.RandomPoint(_center, Math.Clamp(RoamRadius, 2f, 16f),
                random, out Vector3 onGround))
            return;
        float y = world.SafeSpawnHeight(onGround.X, onGround.Z, 1.2f, .35f);
        string prefab = (index & 1) == 0 ? "Prefabs/Sheep.tprefab" : "Prefabs/Pig.tprefab";
        GameObject? animal = Prefab.Instantiate(Scene, prefab,
            new Vector3(onGround.X, y, onGround.Z), Quaternion.Identity);
        if (animal is { IsValid: true })
            _animals.Add(animal.Value);
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
