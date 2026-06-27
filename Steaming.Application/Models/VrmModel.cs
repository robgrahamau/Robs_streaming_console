using System.Numerics;

namespace Steaming.Application.Models;

// All data extracted from a VRM 1.0 GLB file.
// Populated once by VrmLoaderService; read-only after that.
// Background render thread reads these; no locking required after load.

public sealed class VrmModel
{
    public List<VrmMesh>       Meshes      { get; } = [];
    public Dictionary<string, VrmExpression> Expressions { get; } = [];
    public List<VrmNode>       Nodes       { get; } = [];
    public List<VrmSkin>       Skins       { get; } = [];
    public Vector3             BoundsMin   { get; set; }
    public Vector3             BoundsMax   { get; set; }
}

// Raw BGRA texture decoded from GLB image data
public readonly struct VrmTexture(byte[] pixels, int width, int height)
{
    public byte[] Pixels { get; } = pixels;
    public int    Width  { get; } = width;
    public int    Height { get; } = height;
}

public sealed class VrmMesh
{
    // Dense float arrays: 3 floats per vertex (x,y,z)
    public float[]   Positions  { get; set; } = [];
    public float[]   Normals    { get; set; } = [];
    // Dense float array: 2 floats per vertex (u,v)
    public float[]   UVs        { get; set; } = [];
    // Triangle index list (uint32)
    public uint[]    Indices    { get; set; } = [];
    // Per morph target: dense float[] length == Positions.Length (x,y,z deltas)
    public List<float[]> MorphDeltaPositions { get; } = [];

    // Skinning — 4 joint indices and 4 weights per vertex; empty if mesh is unskinned
    public byte[]    Joints0    { get; set; } = [];   // 4 bytes per vertex
    public float[]   Weights0   { get; set; } = [];   // 4 floats per vertex
    public int       SkinIndex  { get; set; } = -1;

    // Decoded texture (premultiplied BGRA) — null if mesh uses only base color factor
    public VrmTexture? Texture    { get; set; }
    public byte BaseColorR       { get; set; } = 255;
    public byte BaseColorG       { get; set; } = 255;
    public byte BaseColorB       { get; set; } = 255;
    public byte BaseColorA       { get; set; } = 255;
}

public sealed class VrmExpression
{
    public List<ExpressionBind> Binds    { get; } = [];
    public bool                 IsBinary { get; set; }
}

public sealed class ExpressionBind
{
    public int   MeshIndex        { get; set; }
    public int   MorphTargetIndex { get; set; }
    public float Weight           { get; set; } = 1f;
}

// One node in the glTF node hierarchy
public sealed class VrmNode
{
    public string     Name            { get; set; } = "";
    public int        Parent          { get; set; } = -1;
    public Matrix4x4  LocalTransform  { get; set; } = Matrix4x4.Identity;
    public Matrix4x4  WorldTransform  { get; set; } = Matrix4x4.Identity;
    public List<int>  Children        { get; } = [];
}

// One skin: list of joint node indices + inverse bind matrices
public sealed class VrmSkin
{
    public int[]        JointNodeIndices     { get; set; } = [];
    public Matrix4x4[]  InverseBindMatrices  { get; set; } = [];
}
