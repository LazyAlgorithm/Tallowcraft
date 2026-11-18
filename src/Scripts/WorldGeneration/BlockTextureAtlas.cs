using System.Numerics;

namespace WorldGeneration;

/// <summary>
/// Nine authored 16×16 pixel-art textures packed into a 3×3 PNG
/// with one-pixel duplicate-color gutters to avoid atlas edge bleed.
/// The PNG is a committed asset, not created on every game startup.
/// </summary>
public static class BlockTextureAtlas
{
    public const string Path = "Textures/block_atlas.png";
    public const int TileSize = 16;
    public const int Gutter = 1;
    public const int Columns = 3;
    public const int Rows = 3;
    private const int Stride = TileSize + 2 * Gutter;
    private const int Width = Stride * Columns;
    private const int Height = Stride * Rows;

    public static Vector2 UV(int tile, float u, float v)
    {
        int col = tile % Columns;
        int row = tile / Columns;
        return new Vector2(
            (col * Stride + Gutter + u * TileSize) / Width,
            (row * Stride + Gutter + v * TileSize) / Height);
    }

    // Vertical faces use local mesh directions for correct triangle winding,
    // not necessarily the directions that texture art expects.
    public static Vector2 FaceUV(int tile, int face, float u, float v) =>
        face switch
        {
            0 or 3 => UV(tile, u, 1f - v),
            1 or 2 => UV(tile, v, 1f - u),
            _ => UV(tile, u, v)
        };

    // 0 grass top, 1 grass side, 2 dirt, 3 stone,
    // 4 log bark, 5 log end, 6 forge side, 7 forge front, 8 planks.
    public static int TileFor(BlockState state, int face) =>
        state.Type switch
        {
            1 => face == 4 ? 0 : face == 5 ? 2 : 1,
            2 => 2,
            3 => 3,
            4 => face >= 4 ? 5 : 4,
            5 => face == (state.Facing switch
            {
                0 => 0,
                1 => 2,
                2 => 1,
                _ => 3
            }) ? 7 : 6,
            6 => 8,
            _ => 3
        };
}
