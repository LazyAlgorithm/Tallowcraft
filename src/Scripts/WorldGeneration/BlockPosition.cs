namespace WorldGeneration;

/// <summary>An absolute voxel coordinate. X and Z can be negative.</summary>
public readonly record struct BlockPosition(int X, int Y, int Z);
