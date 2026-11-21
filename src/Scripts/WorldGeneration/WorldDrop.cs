using System;
using System.Collections.Generic;
using System.Numerics;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Server-authoritative collectible item entity. The server owns item type,
/// quantity, rigidbody movement and who may pick it up. Each peer builds a
/// transient child mesh: miniature textured cubes for building blocks and a
/// camera-facing icon for ore, coal and ingots. Rendering never rotates the
/// networked Jolt body. No client RPC may claim or modify a pickup.
/// </summary>
public sealed class WorldDrop : Script
{
    public const int MaxPerDrop = BlockInventory.StackLimit;
    public const int MaxActive = 256;
    public const float PickupDelay = .5f;
    public const float DespawnAfter = WorldDropSaveCodec.MaxAgeSeconds;

    [Sync] public int ItemType = 0;
    [Sync] public int Quantity = 0;

    private static readonly List<WorldDrop> Active = new();
    private GameObject? _visualObject;
    private GameObject? _camera;
    private MeshRenderer _visualRenderer;
    private int _renderType;
    private float _visualTime;
    private float _age;
    private bool _registered;

    public override void Start() => Register();

    private void Register()
    {
        if (!Scene.IsServer || _registered) return;
        Active.Add(this);
        _registered = true;
    }

    private static void MarkDropSaveDirty(Scene scene)
    {
        if (!scene.IsServer) return;
        scene.Find("BlockWorld")?.GetComponent<BlockWorld>()?.MarkDropsChanged();
    }

    /// <summary>Capture a stable snapshot on the main game thread; the save
    /// worker never touches networked objects or Jolt transforms.</summary>
    public static IReadOnlyList<SavedWorldDrop> Capture(Scene scene,
        WorldDrop? exclude = null)
    {
        var result = new List<SavedWorldDrop>();
        if (!scene.IsServer) return result;
        foreach (WorldDrop drop in Active)
        {
            if (ReferenceEquals(drop, exclude) ||
                !drop.GameObject.IsValid ||
                drop.Scene.SessionId != scene.SessionId ||
                drop.Quantity <= 0 || drop._age >= DespawnAfter)
                continue;
            var saved = new SavedWorldDrop(drop.ItemType, drop.Quantity,
                drop.Transform.Position, drop._age);
            if (!WorldDropSaveCodec.IsValid(saved))
                throw new InvalidOperationException("Cannot save invalid world drop.");
            result.Add(saved);
        }
        return result;
    }

    public static bool HasLive(Scene scene)
    {
        if (!scene.IsServer) return false;
        foreach (WorldDrop drop in Active)
            if (drop.GameObject.IsValid &&
                drop.Scene.SessionId == scene.SessionId &&
                drop.Quantity > 0 && drop._age < DespawnAfter)
                return true;
        return false;
    }

    /// <summary>Restore one-to-one from saved records; do not merge saved
    /// stacks, and refuse to load into a scene that already contains drops.</summary>
    public static bool Restore(Scene scene, IReadOnlyList<SavedWorldDrop> drops)
    {
        if (!scene.IsServer || HasLive(scene) ||
            drops.Count > MaxActive) return false;
        foreach (SavedWorldDrop saved in drops)
        {
            if (!WorldDropSaveCodec.IsValid(saved))
            {
                Clear(scene);
                return false;
            }
            GameObject? spawned = Prefab.Instantiate(scene,
                "Prefabs/WorldDrop.tprefab", saved.Position,
                Quaternion.Identity);
            if (spawned is not { IsValid: true } item)
            {
                Clear(scene);
                return false;
            }
            WorldDrop? script = item.GetComponent<WorldDrop>();
            if (script == null)
            {
                if (scene.IsNetworked) scene.Despawn(item);
                else item.Destroy();
                Clear(scene);
                return false;
            }
            script.ItemType = saved.ItemType;
            script.Quantity = saved.Quantity;
            script._age = saved.Age;
            script.Register();
        }
        return true;
    }

    /// <summary>Delete transient item actors when switching world seeds or
    /// rolling back a failed restore.</summary>
    public static void Clear(Scene scene)
    {
        foreach (WorldDrop drop in new List<WorldDrop>(Active))
            if (drop.GameObject.IsValid &&
                drop.Scene.SessionId == scene.SessionId)
                drop.RemoveDrop();
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
            MarkDropSaveDirty(scene);
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
            else item.Destroy();
            return false;
        }

        script.ItemType = type;
        script.Quantity = quantity;
        script.Register();
        MarkDropSaveDirty(scene);
        return true;
    }

    public override void Update(TickInput input, float deltaTime)
    {
        // Only the lightweight visual child changes orientation. The
        // replicated physics collider/transform always remains authoritative.
        UpdateVisual(deltaTime);
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

    private void UpdateVisual(float deltaTime)
    {
        if (ItemType is < 1 or > BlockInventory.MaxType)
        {
            if (_visualObject is { IsValid: true } hidden)
                hidden.SetActive(false);
            return;
        }

        if (_visualObject is not { IsValid: true })
        {
            GameObject visual = Scene.CreateGameObject("Local pickup visual");
            visual.Transient = true;
            if (!visual.SetParent(GameObject, keepWorldPose: false))
            {
                visual.Destroy();
                return;
            }
            _visualRenderer = visual.AddComponent<MeshRenderer>();
            _visualRenderer.CastShadows = false;
            _visualRenderer.FlatShading = true;
            _visualObject = visual;
            _renderType = 0;
        }

        GameObject child = _visualObject.Value;
        if (_renderType != ItemType)
        {
            if (ItemType is BlockInventory.Wool or BlockInventory.RawPork)
            {
                // Soft voxel-style resource pickups need no extra material
                // atlas or individual Rigidbody. The same small actor is
                // server-owned and saved exactly like mined block drops.
                _visualRenderer.Mesh = "builtin:cube";
                _visualRenderer.Material = "";
                _visualRenderer.Color = ItemType == BlockInventory.Wool ?
                    new Color(.91f, .87f, .77f, 1f) :
                    new Color(.84f, .43f, .43f, 1f);
                child.Transform.LocalScale = ItemType == BlockInventory.Wool ?
                    new Vector3(.25f, .22f, .24f) :
                    new Vector3(.30f, .18f, .23f);
            }
            else if (BlockDropMesh.IsCube(ItemType))
            {
                BlockDropMesh mesh = BlockDropMesh.ForType(ItemType);
                _visualRenderer.Material = ItemType == BlockInventory.Glass
                    ? "Materials/glass.tmat"
                    : ItemType >= BlockInventory.RedBricks
                        ? "Materials/extra_blocks.tmat"
                        : "Materials/block_drop.tmat";
                _visualRenderer.BackFaceCulling = true;
                _visualRenderer.SetMesh(mesh.Positions, mesh.Triangles,
                    mesh.Normals, mesh.Uvs);
            }
            else if (DroppedItemMesh.IsBillboard(ItemType))
            {
                DroppedItemMesh mesh = DroppedItemMesh.ForType(ItemType);
                _visualRenderer.Material = "Materials/item_drop.tmat";
                _visualRenderer.BackFaceCulling = false;
                _visualRenderer.SetMesh(mesh.Positions, mesh.Triangles,
                    mesh.Normals, mesh.Uvs);
            }
            _renderType = ItemType;
            child.SetActive(true);
        }

        _visualTime += MathF.Max(0f, deltaTime);
        child.Transform.LocalPosition = new Vector3(
            0f, .045f + .025f * MathF.Sin(_visualTime * 3f), 0f);

        if (DroppedItemMesh.IsBillboard(ItemType))
        {
            if (_camera is not { IsValid: true })
                _camera = Scene.Find("Main Camera");
            if (_camera is { IsValid: true } camera)
            {
                Vector3 towardCamera =
                    camera.Transform.Position - child.Transform.Position;
                if (towardCamera.LengthSquared() > 0.0001f)
                {
                    Vector3 forward = Vector3.Normalize(towardCamera);
                    Vector3 up = MathF.Abs(Vector3.Dot(forward,
                        Vector3.UnitY)) > .97f ? Vector3.UnitZ :
                        Vector3.UnitY;
                    child.Transform.Rotation =
                        Quaternion.CreateFromRotationMatrix(
                            Matrix4x4.CreateWorld(Vector3.Zero, forward, up));
                }
            }
        }
        else
            child.Transform.LocalRotation =
                Quaternion.CreateFromAxisAngle(Vector3.UnitY,
                    _visualTime * 1.3f);
    }

    private bool TryPickup(GameObject? gameObject)
    {
        if (gameObject is not { IsValid: true } player ||
            !DroppedItemMesh.InPickupRange(
                Transform.Position, player.Transform.Position +
                    new Vector3(0f,.45f,0f)))
            return false;
        BlockEditor? inventory = player.GetComponent<BlockEditor>();
        if (inventory == null || !inventory.TryReceivePersistedDrop(this))
            return false;
        // Disk commit is complete before deleting the authoritative object.
        RemoveDrop();
        return true;
    }

    private void RemoveDrop()
    {
        if (!Scene.IsServer)
            return;
        Quantity = 0;
        MarkDropSaveDirty(Scene);
        if (Scene.IsNetworked)
            Scene.Despawn(GameObject);
        else
            GameObject.Destroy();
    }

    public override void OnDestroy()
    {
        if (_visualObject is { IsValid: true } visual)
            visual.Destroy();
        _visualObject = null;
        if (_registered)
        {
            Active.Remove(this);
            _registered = false;
        }
    }
}
