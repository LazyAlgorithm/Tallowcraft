using System.Numerics;
using Tallow;

namespace WorldGeneration;

[Component]
public struct ChunkInfo
{
    public int X;
    public int Y;
    public int Z;
    public int FaceCount;
}
