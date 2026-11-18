using System;
using System.Numerics;
using Tallow;

namespace WorldGeneration;

/// <summary>
/// Deterministic pixel-art atlas created on each machine. No external textures
/// or per-voxel render objects; a chunk has one mesh and one material texture.
/// </summary>
public sealed class BlockTextureAtlas : IDisposable
{
    private const int TileSize = 16;
    private const int Stride = TileSize + 2; // edge-duplicated gutter for mip filtering
    private const int Tiles = 9;
    private const int Width = Stride * Tiles;
    private const int Height = Stride;
    private readonly Texture _texture;
    public string Name => _texture.Name;

    public BlockTextureAtlas()
    {
        _texture = new Texture("Generated/Tallowcraft/blocks", Width, Height);
        for (int tile = 0; tile < Tiles; tile++)
            for (int y = -1; y <= TileSize; y++)
                for (int x = -1; x <= TileSize; x++)
                {
                    int sourceX = Math.Clamp(x, 0, TileSize - 1);
                    int sourceY = Math.Clamp(y, 0, TileSize - 1);
                    _texture.SetPixel(tile * Stride + 1 + x, 1 + y,
                        Pixel(tile, sourceX, sourceY));
                }
        // Apply is intentionally harmless on a headless authoritative server.
        _texture.Apply();
    }

    public static Vector2 UV(int tile, float u, float v)
    {
        // Each rendered quad is capped at one block wide/high, keeping
        // texels square even when the mesher merges adjacent half-cells.
        return new Vector2((tile * Stride + 1 + u * TileSize) / Width,
            (1 + v * TileSize) / Height);
    }

    /// <summary>
    /// Map each face's mesh coordinates to texture coordinates. For wall faces
    /// texture V must follow world Y (top = 0), not the arbitrary u/v basis
    /// chosen for correct triangle winding. Without this, half the walls draw
    /// the grass border as vertical green stripes.
    /// </summary>
    public static Vector2 FaceUV(int tile, int face, float u, float v)
    {
        return face switch
        {
            // +Z / -X faces: mesh U is horizontal, mesh V is world up.
            0 or 3 => UV(tile, u, 1f - v),
            // -Z / +X faces: mesh U is world up, mesh V is horizontal.
            1 or 2 => UV(tile, v, 1f - u),
            // Horizontal top and bottom already have planar U and V.
            _ => UV(tile, u, v)
        };
    }

    public static int TileFor(BlockState state, int face)
    {
        // Faces: +Z, -Z, +X, -X, +Y, -Y.
        return state.Type switch
        {
            1 => face == 4 ? 0 : face == 5 ? 2 : 1, // grass
            2 => 2, // dirt
            3 => 3, // stone
            4 => face >= 4 ? 5 : 4, // logs: bark / end grain
            5 => face == (state.Facing switch
            {
                0 => 0, // +Z
                1 => 2, // +X
                2 => 1, // -Z
                _ => 3 // -X
            }) ? 7 : 6, // oriented forge front
            6 => 8, // planks
            _ => 3
        };
    }

    private static Color Pixel(int tile, int x, int y)
    {
        int n = unchecked((x * 92837111) ^ (y * 689287499) ^ (tile * 1376312589));
        n ^= n >> 13;
        float variation = ((n & 31) - 15f) / 255f;
        float r, g, b;
        switch (tile)
        {
            case 0: r = .25f; g = .58f; b = .18f; break;  // grass top
            case 1:
                if (y < 4 + (x % 3 == 0 ? 1 : 0))
                { r = .24f; g = .55f; b = .18f; }
                else { r = .44f; g = .31f; b = .18f; }
                break;
            case 2: r = .44f; g = .30f; b = .18f; break;
            case 3: r = .47f; g = .49f; b = .51f; break;
            case 4:
                r = .38f; g = .24f; b = .12f;
                if (x % 5 == 0) { r -= .10f; g -= .07f; }
                break;
            case 5:
                r = .66f; g = .45f; b = .24f;
                if ((Math.Abs(x - 8) + Math.Abs(y - 8)) % 4 == 0)
                { r -= .14f; g -= .10f; }
                break;
            case 6: r = .36f; g = .36f; b = .39f; break;
            case 7:
                if (x >= 3 && x <= 12 && y >= 5 && y <= 12)
                { r = .08f; g = .09f; b = .11f; }
                else { r = .47f; g = .42f; b = .39f; }
                if (x >= 6 && x <= 9 && y >= 10 && y <= 11)
                { r = .86f; g = .41f; b = .10f; }
                break;
            default:
                r = .63f; g = .43f; b = .23f;
                if (y % 5 == 0) { r -= .16f; g -= .12f; b -= .08f; }
                break;
        }
        return new Color(Math.Clamp(r + variation, 0, 1),
            Math.Clamp(g + variation, 0, 1),
            Math.Clamp(b + variation, 0, 1), 1f);
    }

    public void Dispose() => _texture.Dispose();
}
