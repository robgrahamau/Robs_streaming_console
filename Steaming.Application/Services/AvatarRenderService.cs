using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Steaming.Application.Models;

namespace Steaming.Application.Services;

// 30fps avatar render loop using Direct3D 11 (off-screen, no window required).
// Pipeline:
//   1. CPU morph target blending on bind-pose positions
//   2. CPU bone skinning with per-frame bone rotation overrides
//   3. D3D11 GPU rasterisation with depth buffer + toon shading
//   4. Staging texture readback → BGRA byte[] for NDI + preview
//
// Thread safety: _modelLock protects LoadModel vs RenderFrame races.
// Immediate context (_ctx) is only ever touched on the render thread.
// Device object creation (buffer, texture, SRV) is thread-safe in D3D11.
public sealed class AvatarRenderService : IDisposable
{
    public const int RenderWidth  = 540;
    public const int RenderHeight = 960;

    // ── Camera state (volatile — written from UI thread, read from render thread) ──
    public float CameraYaw      = 0f;
    public float CameraPitch    = 0.15f;
    public float CameraDistance = 1.5f;
    public float CameraPanX     = 0f;
    public float CameraPanY     = 1.2f;

    // ── Expression weights (volatile) ─────────────────────────────────────────
    public float ExprAa, ExprIh, ExprOu, ExprEe, ExprOh;

    // ── Diagnostics (set by render thread, read by UI) ────────────────────────
    public volatile float LastAmplitude;
    public volatile int   DiagExprCount;

    // ── Services ───────────────────────────────────────────────────────────────
    private readonly MicCaptureService _mic;
    private readonly NdiSendService    _ndi;

    // ── Model + render lock ────────────────────────────────────────────────────
    // LoadModel and RenderFrame must not run concurrently: LoadModel disposes
    // mesh GPU resources that RenderFrame uses. _modelLock serialises them.
    private readonly object _modelLock = new();
    private VrmModel? _model;

    // Per-frame bone transform computation (preallocated at LoadModel, reused each frame)
    private int[]       _bfsOrder          = [];
    private Matrix4x4[] _nodeWorldTransBuf = [];

    // ── Bone rotation overrides (written from UI thread, read from render thread) ─
    private readonly object _boneLock = new();
    private readonly Dictionary<string, Vector3> _boneRotDeg =
        new(StringComparer.OrdinalIgnoreCase);
    private Matrix4x4[] _restWorldMat = [];
    private Vector3[]   _restWorldPos = [];
    private readonly Dictionary<string, int[]> _ikChainIndices =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Vector3> _ikTargets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Quaternion> _ikBoneQuats =
        new(StringComparer.OrdinalIgnoreCase);
    private volatile FaceTrackingState _faceState = FaceTrackingState.Neutral;
    private int _headBoneIndex = -1;
    private int _neckBoneIndex = -1;
    private int _jawBoneIndex = -1;
    private int _leftEyeBoneIndex = -1;
    private int _rightEyeBoneIndex = -1;
    private readonly Dictionary<string, string> _expressionNameMap =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Double-buffered BGRA output ────────────────────────────────────────────
    private byte[] _frontBuffer = new byte[RenderWidth * RenderHeight * 4];
    private byte[] _backBuffer  = new byte[RenderWidth * RenderHeight * 4];
    private readonly object _swapLock = new();

    // ── Thread ─────────────────────────────────────────────────────────────────
    private Thread?       _thread;
    private volatile bool _running;
    private volatile bool _disposed;

    // ── D3D11 objects ──────────────────────────────────────────────────────────
    private ID3D11Device?           _device;
    private ID3D11DeviceContext?    _ctx;
    private ID3D11Texture2D?        _rtTex;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11Texture2D?        _dsTex;
    private ID3D11DepthStencilView? _dsv;
    private ID3D11Texture2D?        _stagingTex;
    private ID3D11VertexShader?     _vs;
    private ID3D11PixelShader?      _ps;
    private ID3D11InputLayout?      _il;
    private ID3D11Buffer?           _cbPerFrame;
    private ID3D11Buffer?           _cbPerMesh;
    private ID3D11RasterizerState?  _rsState;
    private ID3D11DepthStencilState? _dsState;
    private ID3D11SamplerState?     _sampler;
    private bool                    _d3dReady;

    // ── Per-mesh GPU resources ─────────────────────────────────────────────────
    private sealed class MeshGpu : IDisposable
    {
        public ID3D11Buffer?             Vb;
        public ID3D11Buffer?             Ib;
        public ID3D11ShaderResourceView? Srv;
        public int                       IndexCount;
        public Vector4                   BaseColor;
        public bool                      HasTexture;
        public int                       VertexCount;
        // Pre-allocated vertex CPU buffer — reused every frame, zero per-frame GC
        public Vertex[]  VbVertices = [];
        public byte[]    VbStaging  = [];
        public void Dispose() { Vb?.Dispose(); Ib?.Dispose(); Srv?.Dispose(); }
    }
    private MeshGpu[] _meshGpu = [];

    // ── Constant buffer layouts (must match HLSL cbuffers exactly) ────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct PerFrameCB      // 80 bytes
    {
        public Matrix4x4 WVP;      // 64 bytes (row_major in HLSL)
        public Vector3   LightDir; // 12 bytes
        public float     Pad0;     //  4 bytes
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerMeshCB       // 32 bytes
    {
        public Vector4 BaseColor;  // 16 bytes
        public int     HasTexture; //  4 bytes
        public float   Pad1, Pad2, Pad3; // 12 bytes
    }

    // ── Vertex layout (must match HLSL VSIn) ──────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex          // 32 bytes
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;
    }

    // ── HLSL source ───────────────────────────────────────────────────────────
    private const string HlslSource = @"
cbuffer PerFrame : register(b0)
{
    row_major float4x4 WVP;
    float3 LightDir;
    float  Pad0;
};
cbuffer PerMesh : register(b1)
{
    float4 BaseColor;
    int    HasTexture;
    float3 Pad1;
};
struct VSIn  { float3 Pos : POSITION; float3 Norm : NORMAL; float2 UV : TEXCOORD0; };
struct VSOut { float4 Pos : SV_POSITION; float3 Norm : NORMAL; float2 UV : TEXCOORD0; };

VSOut VSMain(VSIn v)
{
    VSOut o;
    o.Pos  = mul(float4(v.Pos, 1.0f), WVP);
    o.Norm = v.Norm;
    o.UV   = v.UV;
    return o;
}

Texture2D    AlbedoTex : register(t0);
SamplerState Sampler   : register(s0);

float4 PSMain(VSOut i) : SV_TARGET
{
    float4 c = HasTexture ? AlbedoTex.Sample(Sampler, i.UV) : BaseColor;
    if (c.a < 0.15f) discard;
    float3 n = normalize(i.Norm);
    float  d = dot(n, LightDir) * 0.5f + 0.5f;
    float  t = d > 0.65f ? 1.0f : (d > 0.35f ? 0.72f : 0.45f);
    c.rgb *= t;
    return c;
}
";

    // ─────────────────────────────────────────────────────────────────────────
    public AvatarRenderService(MicCaptureService mic, NdiSendService ndi)
    {
        _mic = mic;
        _ndi = ndi;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    // Called from any thread; protected by _modelLock against concurrent RenderFrame.
    public void LoadModel(VrmModel model)
    {
        lock (_modelLock)
        {
            foreach (var g in _meshGpu) g.Dispose();
            _meshGpu = [];
            _model   = null;  // clear first so RenderFrame bails if lock not held

            // Pre-compute BFS traversal order once — reused each frame zero-alloc
            var bfsOrder = new List<int>(model.Nodes.Count);
            var queue    = new Queue<int>(model.Nodes.Count);
            for (int i = 0; i < model.Nodes.Count; i++)
                if (model.Nodes[i].Parent < 0) queue.Enqueue(i);
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                bfsOrder.Add(idx);
                foreach (int ci in model.Nodes[idx].Children) queue.Enqueue(ci);
            }
            _bfsOrder          = bfsOrder.ToArray();
            _nodeWorldTransBuf = new Matrix4x4[model.Nodes.Count];
            _restWorldMat      = new Matrix4x4[model.Nodes.Count];
            _restWorldPos      = new Vector3[model.Nodes.Count];

            for (int i = 0; i < _bfsOrder.Length; i++)
            {
                int idx  = _bfsOrder[i];
                var node = model.Nodes[idx];
                _restWorldMat[idx] = node.Parent < 0
                    ? node.LocalTransform
                    : node.LocalTransform * _restWorldMat[node.Parent];
                _restWorldPos[idx] = new Vector3(
                    _restWorldMat[idx].M41, _restWorldMat[idx].M42, _restWorldMat[idx].M43);
            }

            _ikChainIndices.Clear();
            DetectIKChains(model);
            CacheTrackedBindings(model);

            _model = model;
            DiagExprCount = model.Expressions.Count;

            if (_d3dReady) CreateMeshGpu(model);
        }
    }

    public void Start()
    {
        if (_running || _disposed) return;
        _running = true;
        _thread  = new Thread(RenderLoop) { IsBackground = true, Name = "AvatarRender" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public bool TryGetPreviewFrame(byte[] buffer)
    {
        lock (_swapLock)
        {
            Buffer.BlockCopy(_frontBuffer, 0, buffer, 0, buffer.Length);
            return true;
        }
    }

    // ── Bone rotation overrides ───────────────────────────────────────────────

    public void SetBoneRotation(string boneName, Vector3 eulerDegrees)
    {
        lock (_boneLock) _boneRotDeg[boneName] = eulerDegrees;
    }

    public Vector3 GetBoneRotation(string boneName)
    {
        lock (_boneLock)
        {
            return _boneRotDeg.TryGetValue(boneName, out var v) ? v : Vector3.Zero;
        }
    }

    public void ClearAllBoneRotations()
    {
        lock (_boneLock)
        {
            _boneRotDeg.Clear();
            _ikTargets.Clear();
            _ikBoneQuats.Clear();
        }
    }

    public void SetIKTarget(string effectorName, Vector3 worldPos)
    {
        lock (_boneLock) _ikTargets[effectorName] = worldPos;
    }

    public void ClearIKTarget(string effectorName)
    {
        lock (_boneLock)
        {
            _ikTargets.Remove(effectorName);
            _ikBoneQuats.Clear();
        }
    }

    public void ClearAllIKTargets()
    {
        lock (_boneLock)
        {
            _ikTargets.Clear();
            _ikBoneQuats.Clear();
        }
    }

    public Vector3 GetIKTarget(string effectorName)
    {
        lock (_boneLock)
        {
            return _ikTargets.TryGetValue(effectorName, out var v) ? v : Vector3.Zero;
        }
    }

    public Vector3 GetBoneWorldPos(string name)
    {
        var m = _model;
        if (m == null) return Vector3.Zero;
        for (int i = 0; i < m.Nodes.Count; i++)
        {
            if (m.Nodes[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return new Vector3(_nodeWorldTransBuf[i].M41, _nodeWorldTransBuf[i].M42, _nodeWorldTransBuf[i].M43);
        }
        return Vector3.Zero;
    }

    public string[] GetIKEffectorNames()
    {
        lock (_boneLock) return _ikChainIndices.Keys.ToArray();
    }

    public string[] GetMappedExpressionKeys()
    {
        lock (_modelLock) return _expressionNameMap.Keys.ToArray();
    }

    // Returns joint node names from the primary skin (empty if no model loaded)
    public string[] GetBoneNames()
    {
        var m = _model;
        if (m == null || m.Skins.Count == 0) return [];
        return m.Skins[0].JointNodeIndices
            .Select(i => i < m.Nodes.Count ? m.Nodes[i].Name : "")
            .Where(n => !string.IsNullOrEmpty(n))
            .ToArray();
    }

    public void SetFaceTrackingState(FaceTrackingState state)
    {
        _faceState = state;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        ReleaseD3D();
    }

    // ── Render loop ───────────────────────────────────────────────────────────

    private void RenderLoop()
    {
        if (!InitD3D()) return;

        lock (_modelLock)
        {
            if (_model != null) CreateMeshGpu(_model);
        }

        const int TargetMs = 1000 / 30;
        while (_running && !_disposed)
        {
            var t0 = DateTime.UtcNow;
            RenderFrame();
            int wait = TargetMs - (int)(DateTime.UtcNow - t0).TotalMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    // ── D3D11 initialisation ──────────────────────────────────────────────────

    private bool InitD3D()
    {
        try
        {
            var result = D3D11.D3D11CreateDevice(
                null, DriverType.Hardware, DeviceCreationFlags.None,
                [FeatureLevel.Level_11_0],
                out _device, out _, out _ctx);

            if (result.Failure || _device == null || _ctx == null) return false;

            var hlslBytes = System.Text.Encoding.ASCII.GetBytes(HlslSource);

            Compiler.Compile(hlslBytes, "VSMain", "avatar", "vs_5_0",
                out var vsBlob, out var vsErrors);
            if (vsBlob == null)
                throw new Exception("VS compile: " +
                    (vsErrors != null ? System.Text.Encoding.ASCII.GetString(vsErrors.AsSpan()) : "unknown"));

            Compiler.Compile(hlslBytes, "PSMain", "avatar", "ps_5_0",
                out var psBlob, out var psErrors);
            if (psBlob == null)
                throw new Exception("PS compile: " +
                    (psErrors != null ? System.Text.Encoding.ASCII.GetString(psErrors.AsSpan()) : "unknown"));

            _vs = _device.CreateVertexShader(vsBlob.AsSpan());
            _ps = _device.CreatePixelShader(psBlob.AsSpan());

            var inputElements = new InputElementDescription[]
            {
                new("POSITION", 0, Format.R32G32B32_Float, 0,  0),
                new("NORMAL",   0, Format.R32G32B32_Float, 12, 0),
                new("TEXCOORD", 0, Format.R32G32_Float,    24, 0),
            };
            _il = _device.CreateInputLayout(inputElements, vsBlob.AsSpan());
            vsBlob.Dispose(); psBlob.Dispose();

            var rtDesc = new Texture2DDescription(
                Format.B8G8R8A8_UNorm, RenderWidth, RenderHeight,
                1, 1, BindFlags.RenderTarget);
            _rtTex = _device.CreateTexture2D(rtDesc);
            _rtv   = _device.CreateRenderTargetView(_rtTex);

            var dsTexDesc = new Texture2DDescription(
                Format.D32_Float, RenderWidth, RenderHeight,
                1, 1, BindFlags.DepthStencil);
            _dsTex = _device.CreateTexture2D(dsTexDesc);
            _dsv   = _device.CreateDepthStencilView(_dsTex);

            var stagingDesc = new Texture2DDescription(
                Format.B8G8R8A8_UNorm, RenderWidth, RenderHeight,
                1, 1, BindFlags.None,
                ResourceUsage.Staging, CpuAccessFlags.Read);
            _stagingTex = _device.CreateTexture2D(stagingDesc);

            _cbPerFrame = _device.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<PerFrameCB>(), BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic, CpuAccessFlags.Write));
            _cbPerMesh  = _device.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<PerMeshCB>(), BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic, CpuAccessFlags.Write));

            _rsState = _device.CreateRasterizerState(
                new RasterizerDescription(CullMode.None, FillMode.Solid));

            _dsState = _device.CreateDepthStencilState(new DepthStencilDescription
            {
                DepthEnable    = true,
                DepthWriteMask = DepthWriteMask.All,
                DepthFunc      = ComparisonFunction.Less,
                StencilEnable  = false,
            });

            _sampler = _device.CreateSamplerState(new SamplerDescription(
                Filter.MinMagMipLinear,
                TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
                0, 1, ComparisonFunction.Never, 0, float.MaxValue));

            _ctx.RSSetViewport(0, 0, RenderWidth, RenderHeight, 0f, 1f);
            _ctx.VSSetShader(_vs);
            _ctx.PSSetShader(_ps);
            _ctx.IASetInputLayout(_il);
            _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _ctx.RSSetState(_rsState);
            _ctx.OMSetDepthStencilState(_dsState);
            _ctx.VSSetConstantBuffer(0, _cbPerFrame);
            _ctx.VSSetConstantBuffer(1, _cbPerMesh);
            _ctx.PSSetConstantBuffer(1, _cbPerMesh);
            _ctx.PSSetSampler(0, _sampler);

            _d3dReady = true;
            return true;
        }
        catch
        {
            ReleaseD3D();
            return false;
        }
    }

    // ── Create GPU mesh resources (must hold _modelLock or be called before loop) ─

    private void CreateMeshGpu(VrmModel model)
    {
        if (_device == null) return;
        var gpus = new MeshGpu[model.Meshes.Count];
        for (int mi = 0; mi < model.Meshes.Count; mi++)
        {
            var mesh = model.Meshes[mi];
            var g    = new MeshGpu();
            gpus[mi] = g;

            int vc = mesh.Positions.Length / 3;
            g.VertexCount = vc;
            g.IndexCount  = mesh.Indices.Length;
            g.BaseColor   = new Vector4(
                mesh.BaseColorR / 255f, mesh.BaseColorG / 255f,
                mesh.BaseColorB / 255f, mesh.BaseColorA / 255f);

            if (vc == 0 || g.IndexCount == 0) continue;

            // Pre-allocated CPU vertex arrays — reused every frame, zero per-frame GC
            g.VbVertices = new Vertex[vc];
            int vertByteSize = vc * Marshal.SizeOf<Vertex>();
            g.VbStaging = new byte[vertByteSize];
            g.Vb = _device.CreateBuffer(new BufferDescription(
                (uint)vertByteSize, BindFlags.VertexBuffer,
                ResourceUsage.Dynamic, CpuAccessFlags.Write));

            {
                var ibHandle = GCHandle.Alloc(mesh.Indices, GCHandleType.Pinned);
                try
                {
                    g.Ib = _device.CreateBuffer(
                        new BufferDescription((uint)(g.IndexCount * sizeof(uint)), BindFlags.IndexBuffer),
                        new SubresourceData(ibHandle.AddrOfPinnedObject()));
                }
                finally { ibHandle.Free(); }
            }

            if (mesh.Texture.HasValue)
            {
                var vt     = mesh.Texture.Value;
                var handle = GCHandle.Alloc(vt.Pixels, GCHandleType.Pinned);
                try
                {
                    var texDesc  = new Texture2DDescription(
                        Format.B8G8R8A8_UNorm, (uint)vt.Width, (uint)vt.Height,
                        1, 1, BindFlags.ShaderResource);
                    var initData = new[] { new SubresourceData(handle.AddrOfPinnedObject(), (uint)(vt.Width * 4)) };
                    using var d3dTex = _device.CreateTexture2D(texDesc, initData);
                    g.Srv        = _device.CreateShaderResourceView(d3dTex);
                    g.HasTexture = true;
                }
                finally { handle.Free(); }
            }
        }
        _meshGpu = gpus;
    }

    // ── Render one frame ──────────────────────────────────────────────────────

    private void RenderFrame()
    {
        lock (_modelLock)
        {
            if (_ctx == null || !_d3dReady || _model == null) return;

            // Camera matrices (left-handed D3D11 convention)
            float yaw   = Volatile.Read(ref CameraYaw);
            float pitch = Volatile.Read(ref CameraPitch);
            float dist  = Volatile.Read(ref CameraDistance);
            float panX  = Volatile.Read(ref CameraPanX);
            float panY  = Volatile.Read(ref CameraPanY);

            var eye = new Vector3(
                panX + dist * MathF.Sin(yaw)  * MathF.Cos(pitch),
                panY + dist * MathF.Sin(pitch),
               -dist * MathF.Cos(yaw)         * MathF.Cos(pitch));
            var view = Matrix4x4.CreateLookAtLeftHanded(eye, new Vector3(panX, panY, 0f), Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(
                MathF.PI / 4f, (float)RenderWidth / RenderHeight, 0.01f, 100f);
            var mirrorX = Matrix4x4.CreateScale(-1f, 1f, 1f);

            var pfcb = new PerFrameCB
            {
                WVP      = mirrorX * view * proj,
                LightDir = Vector3.Normalize(new Vector3(0.5f, 1f, 0.3f)),
                Pad0     = 0f,
            };
            UpdateCB(_cbPerFrame!, pfcb);

            _ctx.OMSetRenderTargets(_rtv!, _dsv);
            _ctx.ClearRenderTargetView(_rtv!, new Vortice.Mathematics.Color4(0, 0, 0, 0));
            _ctx.ClearDepthStencilView(_dsv!, DepthStencilClearFlags.Depth, 1f, 0);

            var faceState = _faceState;
            LastAmplitude = _mic.Amplitude;

            var viseme = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["aa"] = Math.Max(faceState.MouthAa, Volatile.Read(ref ExprAa)),
                ["ih"] = Math.Max(faceState.MouthIh, Volatile.Read(ref ExprIh)),
                ["ou"] = Math.Max(faceState.MouthOu, Volatile.Read(ref ExprOu)),
                ["ee"] = Math.Max(faceState.MouthEe, Volatile.Read(ref ExprEe)),
                ["oh"] = Math.Max(faceState.MouthOh, Volatile.Read(ref ExprOh)),
                ["blinkLeft"] = faceState.EyeBlinkLeft,
                ["blinkRight"] = faceState.EyeBlinkRight,
                ["blink"] = Math.Max(faceState.EyeBlinkLeft, faceState.EyeBlinkRight),
                ["lookLeft"] = faceState.EyeLookLeft,
                ["lookRight"] = faceState.EyeLookRight,
                ["lookUp"] = faceState.EyeLookUp,
                ["lookDown"] = faceState.EyeLookDown,
                ["browUpLeft"] = faceState.BrowUpLeft,
                ["browUpRight"] = faceState.BrowUpRight,
                ["happyLeft"] = faceState.SmileLeft,
                ["happyRight"] = faceState.SmileRight,
                // Many VRM "happy" expressions include eye squint. Gate it against strong jaw-open
                // speech so mouth tracking does not accidentally close the eyes through happy/smile.
                ["happy"] = Math.Max(faceState.SmileLeft, faceState.SmileRight) * Math.Max(0f, 1f - faceState.JawOpen * 0.85f),
            };

            // Per-mesh morph target weights (from expression binds)
            var meshMorphW = new float[_model.Meshes.Count][];
            for (int mi = 0; mi < _model.Meshes.Count; mi++)
                meshMorphW[mi] = new float[_model.Meshes[mi].MorphDeltaPositions.Count];

            foreach (var (name, w) in viseme)
            {
                if (w < 0.001f) continue;
                if (!TryGetExpression(name, out var expr)) continue;
                foreach (var bind in expr.Binds)
                {
                    int mi = bind.MeshIndex, ti = bind.MorphTargetIndex;
                    if (mi < meshMorphW.Length && ti < meshMorphW[mi].Length)
                        meshMorphW[mi][ti] += w * bind.Weight;
                }
            }

            // Clamp accumulated morph weights to [0,1]. Overlapping visemes (e.g. aa+ou+oh
            // all firing) sum past 1.0 on shared targets and over-displace vertices —
            // that is the teeth/tongue clipping out through the chin skin.
            for (int mi = 0; mi < meshMorphW.Length; mi++)
            {
                var weights = meshMorphW[mi];
                for (int ti = 0; ti < weights.Length; ti++)
                    if (weights[ti] > 1f) weights[ti] = 1f;
            }

            lock (_boneLock)
            {
                if (_ikTargets.Count > 0) SolveAllIK();
                else _ikBoneQuats.Clear();
            }

            // Compute world transforms per-frame with bone rotation overrides applied.
            // BFS order ensures parents are always processed before children.
            lock (_boneLock)
            {
                const float Deg2Rad = MathF.PI / 180f;
                for (int i = 0; i < _bfsOrder.Length; i++)
                {
                    int idx  = _bfsOrder[i];
                    var node = _model.Nodes[idx];
                    var local = node.LocalTransform;
                    ApplyTrackedBoneRotation(faceState, idx, ref local);
                    if (_ikBoneQuats.TryGetValue(node.Name, out var ikQ))
                    {
                        if (Matrix4x4.Decompose(local, out var scale, out _, out var translation))
                        {
                            local = Matrix4x4.CreateScale(scale)
                                  * Matrix4x4.CreateFromQuaternion(ikQ)
                                  * Matrix4x4.CreateTranslation(translation);
                        }
                    }
                    else if (_boneRotDeg.TryGetValue(node.Name, out var rot))
                    {
                        // Apply rotation override in node's local space (post-multiply)
                        local *= Matrix4x4.CreateFromYawPitchRoll(
                            rot.Y * Deg2Rad, rot.X * Deg2Rad, rot.Z * Deg2Rad);
                    }
                    _nodeWorldTransBuf[idx] = node.Parent < 0
                        ? local
                        : local * _nodeWorldTransBuf[node.Parent];
                }
            }

            // Draw each mesh
            for (int mi = 0; mi < _model.Meshes.Count; mi++)
            {
                if (mi >= _meshGpu.Length) break;
                var mesh = _model.Meshes[mi];
                var g    = _meshGpu[mi];
                if (g.Vb == null || g.Ib == null || g.VertexCount == 0) continue;

                // Compute skin matrices for this mesh (per-frame, uses updated world transforms)
                var skinMats = ComputeSkinMatrices(mesh, _model);

                // CPU: morph targets + bone skinning → write to pre-allocated Vertex[]
                BuildVertices(mesh, meshMorphW[mi], skinMats, g.VbVertices);

                // Upload to GPU
                MemoryMarshal.AsBytes(g.VbVertices.AsSpan()).CopyTo(g.VbStaging);
                var mapped = _ctx.Map(g.Vb, 0, MapMode.WriteDiscard);
                Marshal.Copy(g.VbStaging, 0, mapped.DataPointer, g.VbStaging.Length);
                _ctx.Unmap(g.Vb, 0);

                var pmcb = new PerMeshCB { BaseColor = g.BaseColor, HasTexture = g.HasTexture ? 1 : 0 };
                UpdateCB(_cbPerMesh!, pmcb);

                _ctx.IASetVertexBuffer(0, g.Vb, (uint)Marshal.SizeOf<Vertex>());
                _ctx.IASetIndexBuffer(g.Ib, Format.R32_UInt, 0);
                if (g.Srv != null) _ctx.PSSetShaderResource(0, g.Srv);
                _ctx.DrawIndexed((uint)g.IndexCount, 0, 0);
                if (g.Srv != null) _ctx.PSSetShaderResource(0, (ID3D11ShaderResourceView)null!);
            }

            // Read back rendered frame
            _ctx.CopyResource(_stagingTex!, _rtTex!);
            var stg = _ctx.Map(_stagingTex!, 0, MapMode.Read);
            for (int row = 0; row < RenderHeight; row++)
            {
                nint src = stg.DataPointer + (nint)(row * stg.RowPitch);
                Marshal.Copy(src, _backBuffer, row * RenderWidth * 4, RenderWidth * 4);
            }
            _ctx.Unmap(_stagingTex!, 0);

            lock (_swapLock)
                (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);

            if (_ndi.IsAvailable)
                _ndi.SendFrame(_frontBuffer, RenderWidth, RenderHeight);
        }
    }

    // ── Compute per-mesh skin matrices using current _nodeWorldTransBuf ───────

    private Matrix4x4[] ComputeSkinMatrices(VrmMesh mesh, VrmModel model)
    {
        int si = mesh.SkinIndex;
        if (si < 0 || si >= model.Skins.Count) return [];
        var skin = model.Skins[si];
        var mats = new Matrix4x4[skin.JointNodeIndices.Length];
        for (int j = 0; j < mats.Length; j++)
        {
            int nodeIdx = skin.JointNodeIndices[j];
            Matrix4x4 world = nodeIdx < _nodeWorldTransBuf.Length
                ? _nodeWorldTransBuf[nodeIdx]
                : Matrix4x4.Identity;
            mats[j] = skin.InverseBindMatrices[j] * world;
        }
        return mats;
    }

    // ── CPU: morph targets + bone skinning → Vertex[] (writes in-place) ───────

    private static void BuildVertices(VrmMesh mesh, float[] morphWeights,
                                      Matrix4x4[] skinMats, Vertex[] verts)
    {
        int  vc   = mesh.Positions.Length / 3;
        bool skin = mesh.Joints0.Length == vc * 4 && skinMats.Length > 0;

        for (int i = 0; i < vc; i++)
        {
            float px = mesh.Positions[i * 3],
                  py = mesh.Positions[i * 3 + 1],
                  pz = mesh.Positions[i * 3 + 2];

            // Apply morph target deltas (before skinning)
            for (int t = 0; t < morphWeights.Length; t++)
            {
                float w = morphWeights[t];
                if (w < 0.0001f) continue;
                var d = mesh.MorphDeltaPositions[t];
                px += d[i * 3] * w;
                py += d[i * 3 + 1] * w;
                pz += d[i * 3 + 2] * w;
            }

            float nx = 0f, ny = 1f, nz = 0f;
            if (mesh.Normals.Length > i * 3 + 2)
            {
                nx = mesh.Normals[i * 3]; ny = mesh.Normals[i * 3 + 1]; nz = mesh.Normals[i * 3 + 2];
            }
            float u = mesh.UVs.Length > i * 2 + 1 ? mesh.UVs[i * 2]     : 0f;
            float v = mesh.UVs.Length > i * 2 + 1 ? mesh.UVs[i * 2 + 1] : 0f;

            if (skin)
            {
                var pos4 = new Vector4(px, py, pz, 1f);
                var nrm4 = new Vector4(nx, ny, nz, 0f);
                var bp   = Vector4.Zero;
                var bn   = Vector4.Zero;
                for (int j = 0; j < 4; j++)
                {
                    float wj = mesh.Weights0[i * 4 + j];
                    if (wj < 0.0001f) continue;
                    int ji = mesh.Joints0[i * 4 + j];
                    if (ji >= skinMats.Length) continue;
                    bp += Vector4.Transform(pos4, skinMats[ji]) * wj;
                    bn += Vector4.Transform(nrm4, skinMats[ji]) * wj;
                }
                px = bp.X; py = bp.Y; pz = bp.Z;
                float nlen = MathF.Sqrt(bn.X * bn.X + bn.Y * bn.Y + bn.Z * bn.Z);
                if (nlen > 0.0001f) { nx = bn.X / nlen; ny = bn.Y / nlen; nz = bn.Z / nlen; }
            }

            verts[i] = new Vertex
            {
                Position = new Vector3(px, py, pz),
                Normal   = new Vector3(nx, ny, nz),
                UV       = new Vector2(u, v),
            };
        }
    }

    // ── Update a dynamic constant buffer (safe — no unsafe block) ─────────────

    private void UpdateCB<T>(ID3D11Buffer cb, T data) where T : struct
    {
        var mapped = _ctx!.Map(cb, 0, MapMode.WriteDiscard);
        Marshal.StructureToPtr(data, mapped.DataPointer, false);
        _ctx.Unmap(cb, 0);
    }

    private void DetectIKChains(VrmModel model)
    {
        TryAddChain(model, "Hand", "_L_", ["_L_LowerArm", "_L_UpperArm"]);
        TryAddChain(model, "Hand", "_R_", ["_R_LowerArm", "_R_UpperArm"]);
        TryAddChain(model, "Foot", "_L_", ["_L_LowerLeg", "_L_UpperLeg"]);
        TryAddChain(model, "Foot", "_R_", ["_R_LowerLeg", "_R_UpperLeg"]);
    }

    private void TryAddChain(VrmModel model, string effKeyword, string sideKeyword, string[] midRootKeywords)
    {
        int eff  = FindNode(model, sideKeyword + effKeyword);
        int mid  = FindNode(model, midRootKeywords[0]);
        int root = FindNode(model, midRootKeywords[1]);
        if (eff >= 0 && mid >= 0 && root >= 0)
            _ikChainIndices[model.Nodes[eff].Name] = [eff, mid, root];
    }

    private static int FindNode(VrmModel model, string keyword)
    {
        for (int i = 0; i < model.Nodes.Count; i++)
        {
            if (model.Nodes[i].Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private void SolveAllIK()
    {
        _ikBoneQuats.Clear();
        foreach (var (effName, target) in _ikTargets)
        {
            if (!_ikChainIndices.TryGetValue(effName, out var chain)) continue;
            SolveTwoBoneIK(chain[2], chain[1], chain[0], target);
        }
    }

    private void SolveTwoBoneIK(int rootIdx, int midIdx, int effIdx, Vector3 target)
    {
        var model = _model;
        if (model == null) return;

        var p0 = new Vector3(
            _nodeWorldTransBuf[rootIdx].M41,
            _nodeWorldTransBuf[rootIdx].M42,
            _nodeWorldTransBuf[rootIdx].M43);

        float l1 = Vector3.Distance(_restWorldPos[rootIdx], _restWorldPos[midIdx]);
        float l2 = Vector3.Distance(_restWorldPos[midIdx], _restWorldPos[effIdx]);
        if (l1 < 1e-5f || l2 < 1e-5f) return;

        Vector3 toTarget = target - p0;
        float distance = toTarget.Length();
        if (distance < 1e-5f) return;

        distance = Math.Clamp(distance, MathF.Abs(l1 - l2) + 1e-4f, l1 + l2 - 1e-4f);
        Vector3 dir = toTarget / distance;
        Vector3 p2New = p0 + dir * distance;

        Vector3 restUpperDir = Vector3.Normalize(_restWorldPos[midIdx] - _restWorldPos[rootIdx]);
        Vector3 restLowerDir = Vector3.Normalize(_restWorldPos[effIdx] - _restWorldPos[midIdx]);
        Vector3 restPole = Vector3.Cross(restUpperDir, restLowerDir);
        if (restPole.LengthSquared() < 0.0001f)
        {
            bool isLeg = model.Nodes[rootIdx].Name.Contains("Leg", StringComparison.OrdinalIgnoreCase);
            restPole = isLeg ? new Vector3(0f, 0f, 1f) : new Vector3(0f, 0f, -1f);
        }
        else
        {
            restPole = Vector3.Normalize(restPole);
        }

        Vector3 polePerp = restPole - dir * Vector3.Dot(restPole, dir);
        if (polePerp.LengthSquared() < 0.0001f)
        {
            var crossY = Vector3.Cross(dir, Vector3.UnitY);
            polePerp = crossY.LengthSquared() > 0.001f
                ? Vector3.Normalize(crossY)
                : Vector3.Normalize(Vector3.Cross(dir, Vector3.UnitZ));
        }
        else
        {
            polePerp = Vector3.Normalize(polePerp);
        }

        float cosA = Math.Clamp((l1 * l1 + distance * distance - l2 * l2) / (2f * l1 * distance), -1f, 1f);
        float sinA = MathF.Sqrt(Math.Max(0f, 1f - cosA * cosA));
        Vector3 elbowDir = dir * cosA + polePerp * sinA;
        Vector3 p1New = p0 + elbowDir * l1;

        var rootWorldNew = SolveBoneWorldRotation(rootIdx, midIdx, p0, p1New, model);
        if (rootWorldNew == null) return;

        SolveBoneWorldRotation(midIdx, effIdx, p1New, p2New, model, rootWorldNew.Value);
    }

    private Quaternion? SolveBoneWorldRotation(
        int boneIdx,
        int childIdx,
        Vector3 fromPos,
        Vector3 toPos,
        VrmModel model,
        Quaternion? solvedParentWorld = null)
    {
        Vector3 restDir = Vector3.Normalize(_restWorldPos[childIdx] - _restWorldPos[boneIdx]);
        Vector3 desiredDir = Vector3.Normalize(toPos - fromPos);

        Quaternion worldDelta = QuaternionFromTo(restDir, desiredDir);
        if (!Matrix4x4.Decompose(_restWorldMat[boneIdx], out _, out Quaternion qRestWorld, out _))
            return null;

        Quaternion qWorldNew = worldDelta * qRestWorld;
        int parentIdx = model.Nodes[boneIdx].Parent;
        Quaternion qParentWorld;
        if (solvedParentWorld.HasValue)
        {
            qParentWorld = solvedParentWorld.Value;
        }
        else if (parentIdx >= 0)
        {
            if (!Matrix4x4.Decompose(_nodeWorldTransBuf[parentIdx], out _, out qParentWorld, out _))
                return null;
        }
        else
        {
            qParentWorld = Quaternion.Identity;
        }

        _ikBoneQuats[model.Nodes[boneIdx].Name] = Quaternion.Normalize(qWorldNew * Quaternion.Inverse(qParentWorld));
        return qWorldNew;
    }

    private static Quaternion QuaternionFromTo(Vector3 from, Vector3 to)
    {
        Vector3 fromNorm = Vector3.Normalize(from);
        Vector3 toNorm = Vector3.Normalize(to);
        float dot = Math.Clamp(Vector3.Dot(fromNorm, toNorm), -1f, 1f);
        if (dot > 0.9999f) return Quaternion.Identity;
        if (dot < -0.9999f)
        {
            Vector3 perp = MathF.Abs(fromNorm.X) < 0.9f
                ? Vector3.Normalize(Vector3.Cross(fromNorm, Vector3.UnitX))
                : Vector3.Normalize(Vector3.Cross(fromNorm, Vector3.UnitY));
            return new Quaternion(perp, 0f);
        }

        Vector3 axis = Vector3.Normalize(Vector3.Cross(fromNorm, toNorm));
        float angle = MathF.Acos(dot);
        return Quaternion.CreateFromAxisAngle(axis, angle);
    }

    private void CacheTrackedBindings(VrmModel model)
    {
        _headBoneIndex = FindNode(model, "head");
        _neckBoneIndex = FindNode(model, "neck");
        _jawBoneIndex = FindNode(model, "jaw");
        _leftEyeBoneIndex = FindNode(model, "_L_Eye");
        if (_leftEyeBoneIndex < 0) _leftEyeBoneIndex = FindNode(model, "leftEye");
        _rightEyeBoneIndex = FindNode(model, "_R_Eye");
        if (_rightEyeBoneIndex < 0) _rightEyeBoneIndex = FindNode(model, "rightEye");

        _expressionNameMap.Clear();
        CacheExpressionName(model, "aa", "aa", "a");
        CacheExpressionName(model, "ih", "ih", "i");
        CacheExpressionName(model, "ou", "ou", "u");
        CacheExpressionName(model, "ee", "ee", "e");
        CacheExpressionName(model, "oh", "oh", "o");
        CacheExpressionName(model, "blinkLeft", "blinkLeft", "blink_l", "blinkleft");
        CacheExpressionName(model, "blinkRight", "blinkRight", "blink_r", "blinkright");
        CacheExpressionName(model, "blink", "blink");
        CacheExpressionName(model, "lookLeft", "lookLeft", "lookleft");
        CacheExpressionName(model, "lookRight", "lookRight", "lookright");
        CacheExpressionName(model, "lookUp", "lookUp", "lookup");
        CacheExpressionName(model, "lookDown", "lookDown", "lookdown");
        CacheExpressionName(model, "browUpLeft", "browUpLeft", "browup_l");
        CacheExpressionName(model, "browUpRight", "browUpRight", "browup_r");
        CacheExpressionName(model, "happyLeft", "happyLeft", "smileLeft");
        CacheExpressionName(model, "happyRight", "happyRight", "smileRight");
        CacheExpressionName(model, "happy", "happy", "smile");
    }

    private void CacheExpressionName(VrmModel model, string semanticKey, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = model.Expressions.Keys.FirstOrDefault(k => k.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                _expressionNameMap[semanticKey] = match;
                return;
            }
        }
    }

    private bool TryGetExpression(string semanticKey, out VrmExpression expr)
    {
        expr = null!;
        var model = _model;
        if (model == null)
            return false;

        if (_expressionNameMap.TryGetValue(semanticKey, out var actualName)
            && actualName != null
            && model.Expressions.TryGetValue(actualName, out var mappedExpr))
        {
            expr = mappedExpr;
            return true;
        }

        if (model.Expressions.TryGetValue(semanticKey, out var directExpr))
        {
            expr = directExpr;
            return true;
        }

        return false;
    }

    private void ApplyTrackedBoneRotation(FaceTrackingState faceState, int nodeIndex, ref Matrix4x4 local)
    {
        if (!faceState.IsTracking)
            return;

        if (!Matrix4x4.Decompose(local, out var scale, out var baseRotation, out var translation))
            return;

        Quaternion trackedRotationDelta = Quaternion.Identity;
        if (nodeIndex == _headBoneIndex)
        {
            trackedRotationDelta = Quaternion.CreateFromYawPitchRoll(
                faceState.HeadYaw,
                faceState.HeadPitch,
                faceState.HeadRoll);
        }
        else if (nodeIndex == _neckBoneIndex)
        {
            trackedRotationDelta = Quaternion.CreateFromYawPitchRoll(
                faceState.NeckYaw,
                faceState.NeckPitch,
                faceState.NeckRoll);
        }
        else if (nodeIndex == _jawBoneIndex)
        {
            trackedRotationDelta = Quaternion.CreateFromYawPitchRoll(0f, faceState.JawOpen * 0.25f, 0f);
        }
        else if (nodeIndex == _leftEyeBoneIndex || nodeIndex == _rightEyeBoneIndex)
        {
            trackedRotationDelta = Quaternion.CreateFromYawPitchRoll(
                faceState.EyeLookRight * 0.18f - faceState.EyeLookLeft * 0.18f,
                faceState.EyeLookDown * 0.14f - faceState.EyeLookUp * 0.14f,
                0f);
        }

        if (trackedRotationDelta == Quaternion.Identity)
            return;

        local = Matrix4x4.CreateScale(scale)
              * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(baseRotation * trackedRotationDelta))
              * Matrix4x4.CreateTranslation(translation);
    }

    // ── D3D11 teardown ────────────────────────────────────────────────────────

    private void ReleaseD3D()
    {
        foreach (var g in _meshGpu) g.Dispose();
        _meshGpu = [];
        _sampler?.Dispose(); _dsState?.Dispose(); _rsState?.Dispose();
        _cbPerMesh?.Dispose(); _cbPerFrame?.Dispose();
        _il?.Dispose(); _ps?.Dispose(); _vs?.Dispose();
        _stagingTex?.Dispose(); _dsv?.Dispose(); _dsTex?.Dispose();
        _rtv?.Dispose(); _rtTex?.Dispose(); _ctx?.Dispose(); _device?.Dispose();
        _d3dReady = false;
    }
}
