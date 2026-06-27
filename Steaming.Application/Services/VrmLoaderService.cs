using System.Numerics;
using System.Text.Json;
using SharpGLTF.Schema2;
using SkiaSharp;
using Steaming.Application.Models;

namespace Steaming.Application.Services;

// Loads a VRM 1.0 file (GLB binary) into a VrmModel.
// Uses SharpGLTF.Core for geometry and System.Text.Json to read VRMC_vrm expressions.
// SkiaSharp is used only for PNG/JPEG texture decoding — NOT for rendering.
public static class VrmLoaderService
{
    public static VrmModel Load(string path)
    {
        var model = new VrmModel();

        // Read VRMC_vrm expressions from raw GLB JSON chunk
        var expressions = ReadVrmExpressions(path);

        // Load geometry via SharpGLTF
        var gltf = ModelRoot.Load(path);

        // ── Build node hierarchy ──────────────────────────────────────────────
        // One VrmNode per logical node; build parent links and local transforms.
        for (int ni = 0; ni < gltf.LogicalNodes.Count; ni++)
        {
            var gn = gltf.LogicalNodes[ni];
            var vn = new VrmNode
            {
                LocalTransform = gn.LocalMatrix,
                Name           = gn.Name ?? "",
            };
            model.Nodes.Add(vn);
        }

        // Fill parent/children from gltf node hierarchy
        for (int ni = 0; ni < gltf.LogicalNodes.Count; ni++)
        {
            var gn = gltf.LogicalNodes[ni];
            foreach (var child in gn.VisualChildren)
            {
                int ci = child.LogicalIndex;
                model.Nodes[ni].Children.Add(ci);
                model.Nodes[ci].Parent = ni;
            }
        }

        // Compute world transforms (breadth-first from roots)
        var roots = Enumerable.Range(0, model.Nodes.Count)
                              .Where(i => model.Nodes[i].Parent == -1);
        var queue = new Queue<int>(roots);
        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            var vn  = model.Nodes[idx];
            vn.WorldTransform = vn.Parent == -1
                ? vn.LocalTransform
                : vn.LocalTransform * model.Nodes[vn.Parent].WorldTransform;
            foreach (int ci in vn.Children) queue.Enqueue(ci);
        }

        // ── Load skins ────────────────────────────────────────────────────────
        for (int si = 0; si < gltf.LogicalSkins.Count; si++)
        {
            var gs   = gltf.LogicalSkins[si];
            var skin = new VrmSkin();

            int jcount = gs.JointsCount;
            skin.JointNodeIndices    = new int[jcount];
            skin.InverseBindMatrices = new Matrix4x4[jcount];

            for (int j = 0; j < jcount; j++)
            {
                skin.JointNodeIndices[j]    = gs.GetJoint(j).Item1.LogicalIndex;
                skin.InverseBindMatrices[j] = gs.GetJoint(j).Item2;
            }

            model.Skins.Add(skin);
        }

        // ── Load meshes ───────────────────────────────────────────────────────
        var imageCache = new Dictionary<int, VrmTexture>();

        // Map (meshLogicalIndex, primIndex) → flat model.Meshes index
        int flatIdx      = 0;
        var primToFlat   = new Dictionary<(int, int), int>();

        for (int mi = 0; mi < gltf.LogicalMeshes.Count; mi++)
        {
            var gltfMesh = gltf.LogicalMeshes[mi];
            int pi       = 0;
            foreach (var prim in gltfMesh.Primitives)
            {
                var vm = ExtractPrimitive(prim, gltf, imageCache);
                model.Meshes.Add(vm);
                primToFlat[(mi, pi)] = flatIdx++;
                pi++;
            }
        }

        // Assign skin index to each mesh (find the node that uses this mesh)
        for (int ni = 0; ni < gltf.LogicalNodes.Count; ni++)
        {
            var gn = gltf.LogicalNodes[ni];
            if (gn.Mesh == null) continue;
            int meshLogIdx = gn.Mesh.LogicalIndex;
            int skinIdx    = gn.Skin?.LogicalIndex ?? -1;

            for (int pi = 0; pi < gn.Mesh.Primitives.Count; pi++)
            {
                if (primToFlat.TryGetValue((meshLogIdx, pi), out int fmi))
                    model.Meshes[fmi].SkinIndex = skinIdx;
            }
        }

        // ── Bounding box (use raw bind-pose positions) ────────────────────────
        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        foreach (var m in model.Meshes)
        {
            for (int i = 0; i < m.Positions.Length; i += 3)
            {
                float x = m.Positions[i], y = m.Positions[i + 1], z = m.Positions[i + 2];
                if (x < bmin.X) bmin.X = x; if (x > bmax.X) bmax.X = x;
                if (y < bmin.Y) bmin.Y = y; if (y > bmax.Y) bmax.Y = y;
                if (z < bmin.Z) bmin.Z = z; if (z > bmax.Z) bmax.Z = z;
            }
        }
        model.BoundsMin = bmin;
        model.BoundsMax = bmax;

        // ── Stitch VRMC_vrm expressions (VRM 1.0) ────────────────────────────
        foreach (var (exprName, binds) in expressions)
        {
            var ve = new VrmExpression();
            foreach (var (nodeIdx, morphIdx, weight) in binds)
            {
                if (nodeIdx < 0 || nodeIdx >= gltf.LogicalNodes.Count) continue;
                var node = gltf.LogicalNodes[nodeIdx];
                if (node.Mesh == null) continue;
                int meshLogIdx = node.Mesh.LogicalIndex;
                // Check all primitives of this mesh
                for (int pi = 0; ; pi++)
                {
                    if (!primToFlat.TryGetValue((meshLogIdx, pi), out int fmi)) break;
                    ve.Binds.Add(new ExpressionBind
                    {
                        MeshIndex        = fmi,
                        MorphTargetIndex = morphIdx,
                        Weight           = weight,
                    });
                }
            }
            if (ve.Binds.Count > 0)
                model.Expressions[exprName] = ve;
        }

        // ── VRM 0.x fallback: blendShapeMaster (if no VRM 1.0 expressions) ──
        if (model.Expressions.Count == 0)
            ParseVrm0Expressions(path, model, primToFlat);

        return model;
    }

    // ── Primitive extraction ──────────────────────────────────────────────────

    private static VrmMesh ExtractPrimitive(MeshPrimitive prim, ModelRoot gltf,
                                            Dictionary<int, VrmTexture> imageCache)
    {
        var vm = new VrmMesh();

        // Positions
        var posAcc = prim.GetVertexAccessor("POSITION");
        if (posAcc != null)
        {
            var verts = posAcc.AsVector3Array();
            vm.Positions = new float[verts.Count * 3];
            for (int i = 0; i < verts.Count; i++)
            {
                vm.Positions[i * 3]     = verts[i].X;
                vm.Positions[i * 3 + 1] = verts[i].Y;
                vm.Positions[i * 3 + 2] = verts[i].Z;
            }
        }

        // Normals
        var nrmAcc = prim.GetVertexAccessor("NORMAL");
        if (nrmAcc != null)
        {
            var normals = nrmAcc.AsVector3Array();
            vm.Normals = new float[normals.Count * 3];
            for (int i = 0; i < normals.Count; i++)
            {
                vm.Normals[i * 3]     = normals[i].X;
                vm.Normals[i * 3 + 1] = normals[i].Y;
                vm.Normals[i * 3 + 2] = normals[i].Z;
            }
        }

        // UVs
        var uvAcc = prim.GetVertexAccessor("TEXCOORD_0");
        if (uvAcc != null)
        {
            var uvs = uvAcc.AsVector2Array();
            vm.UVs = new float[uvs.Count * 2];
            for (int i = 0; i < uvs.Count; i++)
            {
                vm.UVs[i * 2]     = uvs[i].X;
                vm.UVs[i * 2 + 1] = uvs[i].Y;
            }
        }

        // Indices
        var idxAcc = prim.GetIndexAccessor();
        if (idxAcc != null)
        {
            var idx = idxAcc.AsIndicesArray();
            vm.Indices = new uint[idx.Count];
            for (int i = 0; i < idx.Count; i++) vm.Indices[i] = idx[i];
        }

        // Morph targets
        int vertexCount = vm.Positions.Length / 3;
        for (int m = 0; m < prim.MorphTargetsCount; m++)
        {
            var morphAttrs = prim.GetMorphTargetAccessors(m);
            if (morphAttrs != null && morphAttrs.TryGetValue("POSITION", out var deltaAcc))
            {
                var deltas = deltaAcc.AsVector3Array();
                var arr    = new float[vertexCount * 3];
                int count  = Math.Min(deltas.Count, vertexCount);
                for (int i = 0; i < count; i++)
                {
                    arr[i * 3]     = deltas[i].X;
                    arr[i * 3 + 1] = deltas[i].Y;
                    arr[i * 3 + 2] = deltas[i].Z;
                }
                vm.MorphDeltaPositions.Add(arr);
            }
            else
            {
                vm.MorphDeltaPositions.Add(new float[vertexCount * 3]);
            }
        }

        // Skinning: JOINTS_0 (u8×4 per vertex) and WEIGHTS_0 (float×4 per vertex)
        var jointsAcc  = prim.GetVertexAccessor("JOINTS_0");
        var weightsAcc = prim.GetVertexAccessor("WEIGHTS_0");
        if (jointsAcc != null && weightsAcc != null)
        {
            var joints  = jointsAcc.AsVector4Array();   // each component is a joint index
            var weights = weightsAcc.AsVector4Array();
            vm.Joints0  = new byte[vertexCount * 4];
            vm.Weights0 = new float[vertexCount * 4];
            for (int i = 0; i < vertexCount; i++)
            {
                vm.Joints0[i * 4]     = (byte)(int)joints[i].X;
                vm.Joints0[i * 4 + 1] = (byte)(int)joints[i].Y;
                vm.Joints0[i * 4 + 2] = (byte)(int)joints[i].Z;
                vm.Joints0[i * 4 + 3] = (byte)(int)joints[i].W;
                vm.Weights0[i * 4]     = weights[i].X;
                vm.Weights0[i * 4 + 1] = weights[i].Y;
                vm.Weights0[i * 4 + 2] = weights[i].Z;
                vm.Weights0[i * 4 + 3] = weights[i].W;
            }
        }

        // Texture from material base color
        var mat = prim.Material;
        if (mat != null)
        {
            var ch = mat.FindChannel("BaseColor");
            if (ch.HasValue)
            {
                var tex = ch.Value.Texture;
                if (tex?.PrimaryImage != null)
                {
                    var img      = tex.PrimaryImage;
                    int imgLogIdx = img.LogicalIndex;
                    if (!imageCache.TryGetValue(imgLogIdx, out var vrmTex))
                    {
                        vrmTex = DecodeToRawBgra(img.Content.Content);
                        imageCache[imgLogIdx] = vrmTex;
                    }
                    vm.Texture = vrmTex;
                }

#pragma warning disable CS0618
                var factor = ch.Value.Parameter;
#pragma warning restore CS0618
                if (factor != default)
                {
                    vm.BaseColorR = (byte)(factor.X * 255f);
                    vm.BaseColorG = (byte)(factor.Y * 255f);
                    vm.BaseColorB = (byte)(factor.Z * 255f);
                    vm.BaseColorA = (byte)(factor.W * 255f);
                }
            }
        }

        return vm;
    }

    // Decode PNG/JPEG bytes to premultiplied BGRA raw bytes (SkiaSharp for decode only — not for rendering)
    private static VrmTexture DecodeToRawBgra(ReadOnlyMemory<byte> data)
    {
        using var stream = new MemoryStream(data.ToArray());
        using var codec  = SKCodec.Create(stream);
        if (codec == null) return new VrmTexture(new byte[4], 1, 1);

        int w = codec.Info.Width, h = codec.Info.Height;
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bmp = new SKBitmap(info);
        codec.GetPixels(info, bmp.GetPixels());

        var pixels = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), pixels, 0, pixels.Length);
        return new VrmTexture(pixels, w, h);
    }

    // ── Read VRMC_vrm expressions from GLB JSON chunk (VRM 1.0) ──────────────

    private static Dictionary<string, List<(int nodeIdx, int morphIdx, float weight)>>
        ReadVrmExpressions(string path)
    {
        var result = new Dictionary<string, List<(int, int, float)>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var fs  = File.OpenRead(path);
            using var br  = new BinaryReader(fs);
            uint magic    = br.ReadUInt32();
            uint version  = br.ReadUInt32();
            br.ReadUInt32();
            if (magic != 0x46546C67 || version != 2) return result;
            uint jsonLen  = br.ReadUInt32();
            uint jsonType = br.ReadUInt32();
            if (jsonType != 0x4E4F534A) return result;
            byte[] jsonBytes = br.ReadBytes((int)jsonLen);

            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;
            if (!root.TryGetProperty("extensions", out var ext)) return result;
            if (!ext.TryGetProperty("VRMC_vrm", out var vrmc)) return result;
            if (!vrmc.TryGetProperty("expressions", out var expRoot)) return result;

            ParseExpressionGroup(expRoot, "preset", result);
            ParseExpressionGroup(expRoot, "custom",  result);
        }
        catch { /* malformed GLB JSON chunk — expressions will be empty, VRM 0.x fallback runs */ }
        return result;
    }

    private static void ParseExpressionGroup(
        JsonElement expRoot, string groupName,
        Dictionary<string, List<(int, int, float)>> result)
    {
        if (!expRoot.TryGetProperty(groupName, out var group)) return;
        foreach (var expr in group.EnumerateObject())
        {
            var binds = new List<(int, int, float)>();
            if (expr.Value.TryGetProperty("morphTargetBinds", out var bindArr))
            {
                foreach (var bind in bindArr.EnumerateArray())
                {
                    if (!bind.TryGetProperty("node",  out var nodeEl)) continue;
                    if (!bind.TryGetProperty("index", out var idxEl))  continue;
                    float w = 1f;
                    if (bind.TryGetProperty("weight", out var wEl)) w = wEl.GetSingle();
                    binds.Add((nodeEl.GetInt32(), idxEl.GetInt32(), w));
                }
            }
            if (binds.Count > 0) result[expr.Name] = binds;
        }
    }

    // ── VRM 0.x fallback: blendShapeMaster → expressions ─────────────────────
    // VRM 0.x encodes mouth shapes as preset "a","i","u","e","o" (weight 0-100).
    // Maps to the same keys ("aa","ih","ou","ee","oh") our viseme system uses.

    private static readonly Dictionary<string, string> Vrm0PresetMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = "aa", ["i"] = "ih", ["u"] = "ou", ["e"] = "ee", ["o"] = "oh"
        };
    private static readonly Dictionary<string, string> Vrm0NameMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = "aa", ["I"] = "ih", ["U"] = "ou", ["E"] = "ee", ["O"] = "oh"
        };

    private static void ParseVrm0Expressions(
        string path, VrmModel model, Dictionary<(int, int), int> primToFlat)
    {
        try
        {
            using var fs  = File.OpenRead(path);
            using var br  = new BinaryReader(fs);
            uint magic    = br.ReadUInt32();
            uint version  = br.ReadUInt32();
            br.ReadUInt32();
            if (magic != 0x46546C67 || version != 2) return;
            uint jsonLen  = br.ReadUInt32();
            if (br.ReadUInt32() != 0x4E4F534A) return;
            byte[] jsonBytes = br.ReadBytes((int)jsonLen);

            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;
            if (!root.TryGetProperty("extensions",      out var ext)) return;
            if (!ext.TryGetProperty("VRM",              out var vrm)) return;
            if (!vrm.TryGetProperty("blendShapeMaster", out var bsm)) return;
            if (!bsm.TryGetProperty("blendShapeGroups", out var groups)) return;

            foreach (var group in groups.EnumerateArray())
            {
                string? exprKey = null;
                if (group.TryGetProperty("presetName", out var pnEl) &&
                    Vrm0PresetMap.TryGetValue(pnEl.GetString() ?? "", out var k0))
                    exprKey = k0;
                if (exprKey == null && group.TryGetProperty("name", out var nameEl) &&
                    Vrm0NameMap.TryGetValue(nameEl.GetString() ?? "", out var k1))
                    exprKey = k1;
                if (exprKey == null) continue;

                if (!group.TryGetProperty("binds", out var bindsEl)) continue;
                var ve = new VrmExpression();
                foreach (var bind in bindsEl.EnumerateArray())
                {
                    if (!bind.TryGetProperty("mesh",  out var meshEl)) continue;
                    if (!bind.TryGetProperty("index", out var idxEl))  continue;
                    int   meshLogIdx = meshEl.GetInt32();
                    int   morphIdx   = idxEl.GetInt32();
                    float weight     = 1f;
                    if (bind.TryGetProperty("weight", out var wEl))
                        weight = wEl.GetSingle() / 100f;  // VRM 0.x: weight is 0-100

                    for (int pi = 0; ; pi++)
                    {
                        if (!primToFlat.TryGetValue((meshLogIdx, pi), out int fmi)) break;
                        ve.Binds.Add(new ExpressionBind
                        {
                            MeshIndex        = fmi,
                            MorphTargetIndex = morphIdx,
                            Weight           = weight,
                        });
                    }
                }
                if (ve.Binds.Count > 0)
                    model.Expressions[exprKey] = ve;
            }
        }
        catch { }
    }
}
