using System;
using System.Collections.Generic;
using System.Numerics;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Server-authoritative collectible item entity. The server owns item type,
/// quantity, rigidbody movement and who may pick it up; clients only draw the
/// 2-plane sprite atlas visual. No client RPC can claim an item or change its
/// quantity. This deliberately has a small per-scene cap to protect netcode.
/// </summary>
public sealed class WorldDrop : Script
{
    public const int MaxPerDrop = BlockInventory.StackLimit;
    public const int MaxActive = 256;
    public const float PickupDelay = .5f;
    public const float DespawnAfter = 300f;

    [Sync] public int ItemType = 0;
    [Sync] public int Quantity = 0;

    private static readonly List<WorldDrop> Active = new();
    private MeshRenderer _renderer;
    private int _renderType;
    private float _age;
    private bool _registered;

    public override void Start()
    {
        _renderer = GameObject.GetComponent<MeshRenderer>();
        if (Scene.IsServer && !_registered)
        {
            Active.Add(this);
            _registered = true;
        }
    }

    /// <summary>
    /// Merge nearby identical drops before spawning another entity. When the
    /// 256-entity cap is reached, return false so mining can safely award its
    /// reward directly instead of losing an item or spawning unlimited actors.
    /// </summary>
    public static bool TrySpawn(Scene scene, int type, Vector3 position,
        int quantity = 1)
    {
        if (!scene.IsServer || type is < 1 or > BlockInventory.MaxType ||
            quantity is < 1 or > MaxPerDrop ||
            !float.IsFinite(position.X) ||
            !float.IsFinite(position.Y) ||
            !float.IsFinite(position.Z))
            return false;

        int count = 0;
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            WorldDrop other = Active[i];
            if (!other.GameObject.IsValid)
            {
                Active.RemoveAt(i);
                continue;
            }
            if (other.Scene.SessionId != scene.SessionId)
                continue;
            count++;
            if (other.ItemType != type ||
                other.Quantity > MaxPerDrop - quantity ||
                Vector3.DistanceSquared(position,
                    other.Transform.Position) > 1.25f * 1.25f)
                continue;
            other.Quantity += quantity;
            return true;
        }
        if (count >= MaxActive)
            return false;

        GameObject? spawned = Prefab.Instantiate(scene,
            "Prefabs/WorldDrop.tprefab", position, Quaternion.Identity);
        if (spawned is not { IsValid: true } item)
            return false;
        WorldDrop? script = item.GetComponent<WorldDrop>();
        if (script == null)
        {
            if (scene.IsNetworked) scene.Despawn(item);
            else scene.Destroy(item);
            return false;
        }

        script.ItemType = type;
        script.Quantity = quantity;
        return true;
    }

    public override void Update(TickInput input, float deltaTime)
    {
        // The scene replicates spawned objects and their transform; the
        // cosmetic mesh is built on every peer from the saved type ID.
        if (_renderer.IsValid && ItemType != _renderType)
        {
            DroppedItemMesh mesh = DroppedItemMesh.ForType(ItemType);
            if (mesh.Triangles.Length > 0)
                _renderer.SetMesh(mesh.Positions, mesh.Triangles,
                    mesh.Normals, mesh.Uvs);
            _renderType = ItemType;
        }
        if (!Scene.IsServer || Quantity <= 0 || ItemType <= 0)
            return;

        _age += MathF.Max(0f, deltaTime);
        if (_age >= DespawnAfter)
        {
            RemoveDrop();
            return;
        }
        if (_age < PickupDelay)
            return;

        // A player may receive a drop only within an actual server-verified
        // radius and only if their server-owned profile has capacity.
        if (TryPickup(Network.PlayerObject(Scene, 0)))
            return;
        if (Scene.IsNetworked)
        {
            foreach (int clientId in Network.ClientIds(Scene))
                if (TryPickup(Network.PlayerObject(Scene, clientId)))
                    return;
        }
        else
        {
            // Single-player scenes may not have a network player record.
            TryPickup(Scene.Find("Player"));
        }
    }

    private bool TryPickup(GameObject? gameObject)
    {
        if (gameObject is not { IsValid: true } player ||
            !DroppedItemMesh.InPickupRange(
                Transform.Position, player.Transform.Position +
                    new Vector3(0f,.45f,0f)))
            return false;
        BlockEditor? inventory = player.GetComponent<BlockEditor>();
        if (inventory == null || !inventory.TryReceiveWorldDrop(ItemType, Quantity))
            return false;
        RemoveDrop();
        return true;
    }

    private void RemoveDrop()
    {
        if (!Scene.IsServer)
            return;
        Quantity = 0;
        if (Scene.IsNetworked)
            Scene.Despawn(GameObject);
        else
            Scene.Destroy(GameObject);
    }

    public override void OnDestroy()
    {
        if (_registered)
        {
            Active.Remove(this);
            _registered = false;
        }
    }
}
