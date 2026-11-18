using System;
using System.Numerics;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Server-authoritative mining/placing. Attach to the NETWORKED player prefab,
/// not the shared world object. Tallow transports the owning player's TickInput.
/// </summary>
public class BlockEditor : Script
{
    public float Reach = 6f;
    public float EyeHeight = 0.7f;
    public float EditCooldown = 0.15f;

    private BlockWorld? _world;
    private float _cooldown;

    public override void Update(TickInput input, float deltaTime)
    {
        // Never trust a client-supplied world coordinate or mutate terrain on a
        // predicted/replayed client tick. Authority lives solely on the server.
        if (!Scene.IsServer)
            return;

        _cooldown = MathF.Max(0f, _cooldown - deltaTime);

        bool breaking = input.Pressed(InputButton.Fire);
        bool placing = input.Pressed(InputButton.Use);
        if ((!breaking && !placing) || _cooldown > 0f)
            return;

        _world ??= Scene.Find("BlockWorld")?.GetComponent<BlockWorld>();
        if (_world == null)
            return;

        Vector3 origin = Transform.Position + Vector3.UnitY * EyeHeight;
        Vector3 direction = Vector3.Transform(Vector3.UnitZ, input.LookRotation);

        if (_world.TryEditFromRay(origin, direction, Math.Clamp(Reach, 1f, 8f), breaking, GameObject))
            _cooldown = MathF.Max(0.05f, EditCooldown);
    }
}
