using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class PointCloudFusionSource : MonoBehaviour, IPointCloudSource
{
    [Header("Sources (5 cameras)")]
    [SerializeField] private MonoBehaviour[] pointCloudSources; // each implements IPointCloudSource

    private IPointCloudSource[] _sources;     // validated sources
    private CameraWindow[] _cam;              // per-camera state
    private bool _initialized = false;

    public event Action<PointCloudData> OnPointCloudGenerated;

    [Header("Per-camera sliding window fusion")]
    [Min(1)] public int WindowFrames = 5;
    [Min(0.0001f)] public float PerCameraVoxelSize = 0.05f;

    [Header("Optional: radius outlier removal (per camera after window)")]
    public bool EnableRadiusOutlierRemoval = true;
    [Min(0.0001f)] public float OutlierRadius = 0.08f;
    [Min(0)] public int MinNeighbors = 5;

    [Header("Optional: Spatial Filtering (Crop Play Area)")]
    public bool EnableSpatialFilter = false;
    [Tooltip("Min and Max X bounds (Left/Right)")]
    public float MinX = -5f;
    public float MaxX = 5f;
    [Tooltip("Min and Max Y bounds (Down/Up) - Use this to remove the ceiling!")]
    public float MinY = -5f;
    public float MaxY = 2.5f;
    [Tooltip("Min and Max Z bounds (Back/Forward)")]
    public float MinZ = -5f;
    public float MaxZ = 5f;

    [Header("Optional: final merge across cameras")]
    public bool EnableFinalVoxelMerge = false;
    [Min(0.0001f)] public float FinalVoxelSize = 0.05f;

    [Header("Statistical Outlier Removal (SOR)")]
    [Tooltip("Removes points whose mean distance to K neighbors is > stdMultiplier standard deviations from the global mean.")]
    public bool EnableSOR = false;
    [Min(1)] public int SOR_K = 10;
    [Min(0.1f)] public float SOR_StdMultiplier = 1.8f;
    [Tooltip("Only run SOR every N fused frames (1 = every frame, 20 = every 20 frames). Cached result reused on skipped frames.")]
    [Min(1)] public int SOR_EveryNFrames = 20;

    [Header("Bilateral Position Smoothing")]
    [Tooltip("Shifts each point toward its local neighborhood mean, weighted by distance. Preserves edges.")]
    public bool EnableBilateralSmooth = false;
    [Min(0.001f)] public float SmoothSpatialSigma = 0.03f;
    [Min(1)] public int SmoothIterations = 1;
    [Min(1)] public int SmoothK = 10;
    [Tooltip("Only run Bilateral smoothing every N fused frames (1 = every frame, 5 = every 5 frames).")]
    [Min(1)] public int SmoothEveryNFrames = 5;

    [Header("Normal-Weighted Smoothing (Advanced)")]
    [Tooltip("Estimates local normals via PCA and smooths only along the tangent plane. Preserves sharp edges.")]
    public bool EnableNormalSmooth = false;
    [Min(1)] public int NormalK = 15;
    [Min(0.001f)] public float NormalSmoothSigma = 0.03f;
    [Min(1)] public int NormalSmoothIterations = 1;

    // -------- internal structures --------

    private struct VoxelKey : IEquatable<VoxelKey>
    {
        public int x, y, z;
        public VoxelKey(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public bool Equals(VoxelKey other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is VoxelKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + x;
                h = h * 31 + y;
                h = h * 31 + z;
                return h;
            }
        }
    }

    private struct Accum
    {
        public Vector3 sumP;
        public Vector4 sumC; // rgba in float
        public int count;
    }

    private class CameraWindow
    {
        public Queue<Dictionary<VoxelKey, Accum>> frames = new Queue<Dictionary<VoxelKey, Accum>>();
        public Dictionary<VoxelKey, Accum> running = new Dictionary<VoxelKey, Accum>(1 << 16);

        public List<Vector3> outPoints = new List<Vector3>(200000);
        public List<Color32> outColors = new List<Color32>(200000);
    }

    // final output lists
    private readonly List<Vector3> _fusedPoints = new List<Vector3>(400000);
    private readonly List<Color32> _fusedColors = new List<Color32>(400000);

    // We need stable handlers to unsubscribe properly
    private Action<PointCloudData>[] _handlers;

    // --- Per-frame gating counters ---
    private int _sorFrameCounter = 0;
    private int _smoothFrameCounter = 0;

    // --- Preallocated scratch buffers for SOR & Bilateral (avoids per-frame GC pressure) ---
    private float[]   _sorMeanDists  = new float[16384];
    private bool[]    _sorKeepMask   = new bool[16384];
    private Vector3[] _smoothedPos   = new Vector3[16384];
    private Color32[] _smoothedCols  = new Color32[16384];
    // Reusable neighbor candidate list
    private readonly List<(int idx, float sqDist)> _neighborScratch = new List<(int, float)>(512);
    private readonly List<float> _distScratch = new List<float>(512);
    // Reusable grid (cleared & refilled each use — avoids Dictionary re-alloc)
    private readonly Dictionary<CellKey, List<int>> _sorGrid      = new Dictionary<CellKey, List<int>>(4096);
    private readonly Dictionary<CellKey, List<int>> _smoothGrid   = new Dictionary<CellKey, List<int>>(4096);
    // Pool of index lists returned to the grid dictionaries
    private readonly Stack<List<int>> _listPool = new Stack<List<int>>(512);

    private List<int> RentList()
    {
        return _listPool.Count > 0 ? _listPool.Pop() : new List<int>(16);
    }
    private void ReturnGrid(Dictionary<CellKey, List<int>> grid)
    {
        foreach (var kv in grid) { kv.Value.Clear(); _listPool.Push(kv.Value); }
        grid.Clear();
    }

    private void Awake()
    {
        EnsureInitialized();
    }

    private void OnEnable()
    {
        EnsureInitialized();
        SubscribeAll();
    }

    private void OnDisable()
    {
        UnsubscribeAll();
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;

        if (pointCloudSources == null || pointCloudSources.Length == 0)
        {
            Debug.LogError("[Fusion] No sources assigned (pointCloudSources is empty).");
            enabled = false;
            return;
        }

        _sources = new IPointCloudSource[pointCloudSources.Length];
        _cam = new CameraWindow[pointCloudSources.Length];
        _handlers = new Action<PointCloudData>[pointCloudSources.Length];

        int validCount = 0;

        for (int i = 0; i < pointCloudSources.Length; i++)
        {
            var mb = pointCloudSources[i];
            if (mb == null)
            {
                Debug.LogWarning($"[Fusion] pointCloudSources[{i}] is NULL (missing reference in Inspector).");
                _sources[i] = null;
                _cam[i] = null;
                _handlers[i] = null;
                continue;
            }

            var src = mb as IPointCloudSource;
            if (src == null)
            {
                Debug.LogWarning($"[Fusion] Source '{mb.name}' at index {i} does NOT implement IPointCloudSource.");
                _sources[i] = null;
                _cam[i] = null;
                _handlers[i] = null;
                continue;
            }

            _sources[i] = src;
            _cam[i] = new CameraWindow();
            validCount++;

            int idx = i;
            _handlers[i] = (pc) => OnCameraFrame(idx, pc);
        }

        if (validCount == 0)
        {
            Debug.LogError("[Fusion] No valid sources found. Fix your Inspector list.");
            enabled = false;
            return;
        }

        _initialized = true;
        Debug.Log($"[Fusion] Initialized. Valid sources = {validCount}/{pointCloudSources.Length}");
    }

    private void SubscribeAll()
    {
        if (!_initialized || _sources == null) return;

        for (int i = 0; i < _sources.Length; i++)
        {
            if (_sources[i] == null || _handlers[i] == null) continue;

            // safety: avoid duplicate subscriptions
            _sources[i].OnPointCloudGenerated -= _handlers[i];
            _sources[i].OnPointCloudGenerated += _handlers[i];
        }
    }

    private void UnsubscribeAll()
    {
        if (_sources == null || _handlers == null) return;

        for (int i = 0; i < _sources.Length; i++)
        {
            if (_sources[i] == null || _handlers[i] == null) continue;
            _sources[i].OnPointCloudGenerated -= _handlers[i];
        }
    }

    // NOTE: each camera triggers fusion update.
    private void OnCameraFrame(int camIndex, PointCloudData pc)
    {
        if (!_initialized) return;
        if (camIndex < 0 || camIndex >= _cam.Length) return;
        if (_cam[camIndex] == null) return;

        if (pc.Points == null || pc.Colors == null) return;

        int n = Mathf.Min(pc.Count, Mathf.Min(pc.Points.Count, pc.Colors.Count));
        if (n <= 0) return;

        var cw = _cam[camIndex];

        // 1) voxelize this frame (per-camera voxel average)
        var frameVox = VoxelAverage(pc.Points, pc.Colors, n, PerCameraVoxelSize);

        // 2) add to running
        AddAccum(cw.running, frameVox);

        // 3) push frame into queue
        cw.frames.Enqueue(frameVox);

        // 4) pop oldest if window exceeded
        int win = Mathf.Max(1, WindowFrames);
        while (cw.frames.Count > win)
        {
            var old = cw.frames.Dequeue();
            RemoveAccum(cw.running, old);
        }

        // 5) build per-camera stabilized output from running accumulator
        BuildAveragedPointCloud(cw.running, cw.outPoints, cw.outColors);

        // 6) optional outlier removal (per camera)
        if (EnableRadiusOutlierRemoval)
            RadiusOutlierRemovalInPlace(cw.outPoints, cw.outColors, OutlierRadius, MinNeighbors);

        // 7) fuse across cameras (concat per-camera stabilized clouds)
        FuseAllCamerasAndEmit();
    }

    private void FuseAllCamerasAndEmit()
    {
        _fusedPoints.Clear();
        _fusedColors.Clear();

        for (int i = 0; i < _cam.Length; i++)
        {
            if (_cam[i] == null) continue;
            _fusedPoints.AddRange(_cam[i].outPoints);
            _fusedColors.AddRange(_cam[i].outColors);
        }

        if (_fusedPoints.Count == 0) return;

        // --- Filter out points outside the defined spatial bounds ---
        if (EnableSpatialFilter)
        {
            int keepCount = 0;
            int total = _fusedPoints.Count;
            for (int i = 0; i < total; i++)
            {
                var p = _fusedPoints[i];
                if (p.x >= MinX && p.x <= MaxX &&
                    p.y >= MinY && p.y <= MaxY &&
                    p.z >= MinZ && p.z <= MaxZ)
                {
                    _fusedPoints[keepCount] = p;
                    _fusedColors[keepCount] = _fusedColors[i];
                    keepCount++;
                }
            }
            if (keepCount < total)
            {
                _fusedPoints.RemoveRange(keepCount, total - keepCount);
                _fusedColors.RemoveRange(keepCount, total - keepCount);
            }
            if (_fusedPoints.Count == 0) return;
        }

        // optional final voxel merge across all cameras
        if (EnableFinalVoxelMerge)
        {
            var merged = VoxelAverage(_fusedPoints, _fusedColors, _fusedPoints.Count, FinalVoxelSize);
            _fusedPoints.Clear();
            _fusedColors.Clear();
            BuildAveragedPointCloud(merged, _fusedPoints, _fusedColors);
        }

        // --- Post-processing pipeline (with per-frame gating) ---

        // Statistical Outlier Removal — run every SOR_EveryNFrames frames
        if (EnableSOR && _fusedPoints.Count > SOR_K)
        {
            _sorFrameCounter++;
            if (_sorFrameCounter >= SOR_EveryNFrames)
            {
                _sorFrameCounter = 0;
                StatisticalOutlierRemovalInPlace(_fusedPoints, _fusedColors, SOR_K, SOR_StdMultiplier);
            }
        }

        // Bilateral Position Smoothing — run every SmoothEveryNFrames frames
        if (EnableBilateralSmooth && _fusedPoints.Count > SmoothK)
        {
            _smoothFrameCounter++;
            if (_smoothFrameCounter >= SmoothEveryNFrames)
            {
                _smoothFrameCounter = 0;
                BilateralSmoothInPlace(_fusedPoints, _fusedColors, SmoothSpatialSigma, SmoothIterations, SmoothK);
            }
        }

        // Normal-Weighted Smoothing (always per-frame when enabled, but off by default)
        if (EnableNormalSmooth && _fusedPoints.Count > NormalK)
            NormalWeightedSmoothInPlace(_fusedPoints, _fusedColors, NormalK, NormalSmoothSigma, NormalSmoothIterations);

        OnPointCloudGenerated?.Invoke(new PointCloudData
        {
            Points = _fusedPoints,
            Colors = _fusedColors,
            Count = _fusedPoints.Count
        });
    }

    // ---------- Voxel average helpers ----------

    private static VoxelKey KeyFromPoint(Vector3 p, float vs)
    {
        int xi = Mathf.FloorToInt(p.x / vs);
        int yi = Mathf.FloorToInt(p.y / vs);
        int zi = Mathf.FloorToInt(p.z / vs);
        return new VoxelKey(xi, yi, zi);
    }

    private static Dictionary<VoxelKey, Accum> VoxelAverage(List<Vector3> pts, List<Color32> cols, int n, float voxelSize)
    {
        var dict = new Dictionary<VoxelKey, Accum>(Mathf.Min(n, 1 << 18));

        for (int i = 0; i < n; i++)
        {
            var p = pts[i];
            var c = cols[i];

            var k = KeyFromPoint(p, voxelSize);
            if (!dict.TryGetValue(k, out var a))
                a = default;

            a.sumP += p;
            a.sumC += new Vector4(c.r, c.g, c.b, c.a);
            a.count += 1;
            dict[k] = a;
        }
        return dict;
    }

    private static void AddAccum(Dictionary<VoxelKey, Accum> running, Dictionary<VoxelKey, Accum> frame)
    {
        foreach (var kv in frame)
        {
            if (!running.TryGetValue(kv.Key, out var a))
                a = default;
            a.sumP += kv.Value.sumP;
            a.sumC += kv.Value.sumC;
            a.count += kv.Value.count;
            running[kv.Key] = a;
        }
    }

    private static void RemoveAccum(Dictionary<VoxelKey, Accum> running, Dictionary<VoxelKey, Accum> frame)
    {
        foreach (var kv in frame)
        {
            if (!running.TryGetValue(kv.Key, out var a))
                continue;

            a.sumP -= kv.Value.sumP;
            a.sumC -= kv.Value.sumC;
            a.count -= kv.Value.count;

            if (a.count <= 0)
                running.Remove(kv.Key);
            else
                running[kv.Key] = a;
        }
    }

    private static void BuildAveragedPointCloud(Dictionary<VoxelKey, Accum> dict, List<Vector3> outPts, List<Color32> outCols)
    {
        outPts.Clear();
        outCols.Clear();

        outPts.Capacity = Mathf.Max(outPts.Capacity, dict.Count);
        outCols.Capacity = Mathf.Max(outCols.Capacity, dict.Count);

        foreach (var kv in dict)
        {
            var a = kv.Value;
            float inv = 1.0f / a.count;
            Vector3 p = a.sumP * inv;
            Vector4 c = a.sumC * inv;

            outPts.Add(p);
            outCols.Add(new Color32(
                (byte)Mathf.Clamp(c.x, 0, 255),
                (byte)Mathf.Clamp(c.y, 0, 255),
                (byte)Mathf.Clamp(c.z, 0, 255),
                (byte)Mathf.Clamp(c.w, 0, 255)
            ));
        }
    }

    // ---------- Radius outlier removal (grid hash) ----------

    private struct CellKey : IEquatable<CellKey>
    {
        public int x, y, z;
        public CellKey(int x, int y, int z) { this.x = x; this.y = y; this.z = z; }
        public bool Equals(CellKey other) => x == other.x && y == other.y && z == other.z;
        public override bool Equals(object obj) => obj is CellKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + x;
                h = h * 31 + y;
                h = h * 31 + z;
                return h;
            }
        }
    }

    private static void RadiusOutlierRemovalInPlace(List<Vector3> pts, List<Color32> cols, float radius, int minNeighbors)
    {
        if (pts.Count == 0) return;

        float cell = Mathf.Max(1e-5f, radius);
        float r2 = radius * radius;

        var grid = new Dictionary<CellKey, List<int>>(pts.Count / 4);

        CellKey CellOf(Vector3 p)
        {
            int cx = Mathf.FloorToInt(p.x / cell);
            int cy = Mathf.FloorToInt(p.y / cell);
            int cz = Mathf.FloorToInt(p.z / cell);
            return new CellKey(cx, cy, cz);
        }

        for (int i = 0; i < pts.Count; i++)
        {
            var ck = CellOf(pts[i]);
            if (!grid.TryGetValue(ck, out var list))
            {
                list = new List<int>(64);
                grid[ck] = list;
            }
            list.Add(i);
        }

        var keepPts = new List<Vector3>(pts.Count);
        var keepCols = new List<Color32>(pts.Count);

        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var ck = CellOf(p);

            int neighbors = 0;

            for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        var nk = new CellKey(ck.x + dx, ck.y + dy, ck.z + dz);
                        if (!grid.TryGetValue(nk, out var list)) continue;

                        for (int j = 0; j < list.Count; j++)
                        {
                            int idx = list[j];
                            if (idx == i) continue;

                            if ((pts[idx] - p).sqrMagnitude <= r2)
                            {
                                neighbors++;
                                if (neighbors >= minNeighbors) break;
                            }
                        }

                        if (neighbors >= minNeighbors) break;
                    }

            if (neighbors >= minNeighbors)
            {
                keepPts.Add(p);
                keepCols.Add(cols[i]);
            }
        }

        pts.Clear();
        cols.Clear();
        pts.AddRange(keepPts);
        cols.AddRange(keepCols);
    }

    // ---------- Statistical Outlier Removal ----------

    private void StatisticalOutlierRemovalInPlace(
        List<Vector3> pts, List<Color32> cols, int k, float stdMultiplier)
    {
        int n = pts.Count;
        if (n <= k) return;

        // Grow scratch buffers if needed (no alloc on steady state)
        if (_sorMeanDists.Length < n) { _sorMeanDists = new float[n * 2]; _sorKeepMask = new bool[n * 2]; }

        // Adaptive cell size from bounding-box density estimate
        Vector3 bmin = pts[0], bmax = pts[0];
        for (int i = 1; i < n; i++)
        {
            var p = pts[i];
            if (p.x < bmin.x) bmin.x = p.x; if (p.y < bmin.y) bmin.y = p.y; if (p.z < bmin.z) bmin.z = p.z;
            if (p.x > bmax.x) bmax.x = p.x; if (p.y > bmax.y) bmax.y = p.y; if (p.z > bmax.z) bmax.z = p.z;
        }
        Vector3 extent = bmax - bmin;
        float volume  = Mathf.Max(1e-10f, extent.x * extent.y * extent.z);
        float density = n / volume;
        float cell    = Mathf.Max(0.01f, Mathf.Pow(k / Mathf.Max(1f, density), 1f / 3f));

        // Build spatial grid — reuse pooled lists to avoid heap alloc
        ReturnGrid(_sorGrid);
        for (int i = 0; i < n; i++)
        {
            var p  = pts[i];
            var ck = new CellKey(Mathf.FloorToInt(p.x / cell),
                                 Mathf.FloorToInt(p.y / cell),
                                 Mathf.FloorToInt(p.z / cell));
            if (!_sorGrid.TryGetValue(ck, out var list))
            {
                list = RentList();
                _sorGrid[ck] = list;
            }
            list.Add(i);
        }

        // Per-point: collect K-nearest mean distance using reused scratch list
        for (int i = 0; i < n; i++)
        {
            var p  = pts[i];
            var ck = new CellKey(Mathf.FloorToInt(p.x / cell),
                                 Mathf.FloorToInt(p.y / cell),
                                 Mathf.FloorToInt(p.z / cell));

            _distScratch.Clear();
            int searchR = 1;
            while (_distScratch.Count < k && searchR <= 5)
            {
                _distScratch.Clear();
                for (int dz = -searchR; dz <= searchR; dz++)
                for (int dy = -searchR; dy <= searchR; dy++)
                for (int dx = -searchR; dx <= searchR; dx++)
                {
                    var nk = new CellKey(ck.x + dx, ck.y + dy, ck.z + dz);
                    if (!_sorGrid.TryGetValue(nk, out var list)) continue;
                    for (int j = 0; j < list.Count; j++)
                    {
                        if (list[j] == i) continue;
                        _distScratch.Add((pts[list[j]] - p).sqrMagnitude);
                    }
                }
                searchR++;
            }

            _distScratch.Sort();
            int take = Mathf.Min(k, _distScratch.Count);
            float sum = 0;
            for (int j = 0; j < take; j++) sum += Mathf.Sqrt(_distScratch[j]);
            _sorMeanDists[i] = take > 0 ? sum / take : float.MaxValue;
        }

        // Global mean & std
        double gSum = 0, gSumSq = 0;
        for (int i = 0; i < n; i++) { gSum += _sorMeanDists[i]; gSumSq += (double)_sorMeanDists[i] * _sorMeanDists[i]; }
        float gMean    = (float)(gSum / n);
        float variance = (float)(gSumSq / n - (double)gMean * gMean);
        float gStd     = Mathf.Sqrt(Mathf.Max(0, variance));
        float threshold = gMean + stdMultiplier * gStd;

        // Build keep mask (no new List allocation)
        int kept = 0;
        for (int i = 0; i < n; i++) { _sorKeepMask[i] = _sorMeanDists[i] <= threshold; if (_sorKeepMask[i]) kept++; }

        int removed = n - kept;
        if (removed > 0)
            Debug.Log($"[Fusion] SOR removed {removed}/{n} outlier points (threshold={threshold:F4}m)");

        // Compact in-place using mask (avoid extra List alloc)
        int write = 0;
        for (int i = 0; i < n; i++)
        {
            if (_sorKeepMask[i])
            {
                pts[write]  = pts[i];
                cols[write] = cols[i];
                write++;
            }
        }
        // Trim to kept count
        int excess = n - write;
        if (excess > 0) { pts.RemoveRange(write, excess); cols.RemoveRange(write, excess); }
    }

    // ---------- Bilateral Position Smoothing ----------

    private void BilateralSmoothInPlace(
        List<Vector3> pts, List<Color32> cols, float spatialSigma, int iterations, int kNeighbors)
    {
        int n = pts.Count;
        if (n <= kNeighbors) return;

        // Grow scratch buffers if needed
        if (_smoothedPos.Length < n) { _smoothedPos = new Vector3[n * 2]; _smoothedCols = new Color32[n * 2]; }

        float sigmaSquared2 = 2f * spatialSigma * spatialSigma;
        float cell = Mathf.Max(0.01f, spatialSigma * 2f);

        for (int iter = 0; iter < iterations; iter++)
        {
            // Build grid — reuse pooled lists
            ReturnGrid(_smoothGrid);
            for (int i = 0; i < n; i++)
            {
                var p  = pts[i];
                var ck = new CellKey(Mathf.FloorToInt(p.x / cell),
                                     Mathf.FloorToInt(p.y / cell),
                                     Mathf.FloorToInt(p.z / cell));
                if (!_smoothGrid.TryGetValue(ck, out var list)) { list = RentList(); _smoothGrid[ck] = list; }
                list.Add(i);
            }

            for (int i = 0; i < n; i++)
            {
                var p  = pts[i];
                var ck = new CellKey(Mathf.FloorToInt(p.x / cell),
                                     Mathf.FloorToInt(p.y / cell),
                                     Mathf.FloorToInt(p.z / cell));

                // Gather neighbors into reused scratch list
                _neighborScratch.Clear();
                for (int dz = -1; dz <= 1; dz++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var nk = new CellKey(ck.x + dx, ck.y + dy, ck.z + dz);
                    if (!_smoothGrid.TryGetValue(nk, out var list)) continue;
                    for (int j = 0; j < list.Count; j++)
                    {
                        float d2 = (pts[list[j]] - p).sqrMagnitude;
                        _neighborScratch.Add((list[j], d2));
                    }
                }

                _neighborScratch.Sort((a, b) => a.sqDist.CompareTo(b.sqDist));
                int take = Mathf.Min(kNeighbors, _neighborScratch.Count);

                Vector3 sumPos   = Vector3.zero;
                float   sumW     = 0;
                Vector4 sumColor = Vector4.zero;

                for (int j = 0; j < take; j++)
                {
                    float w = Mathf.Exp(-_neighborScratch[j].sqDist / sigmaSquared2);
                    sumPos += pts[_neighborScratch[j].idx] * w;
                    var c   = cols[_neighborScratch[j].idx];
                    sumColor += new Vector4(c.r, c.g, c.b, c.a) * w;
                    sumW += w;
                }

                if (sumW > 1e-8f)
                {
                    _smoothedPos[i]  = sumPos / sumW;
                    Vector4 avgC     = sumColor / sumW;
                    _smoothedCols[i] = new Color32(
                        (byte)Mathf.Clamp(avgC.x, 0, 255),
                        (byte)Mathf.Clamp(avgC.y, 0, 255),
                        (byte)Mathf.Clamp(avgC.z, 0, 255),
                        (byte)Mathf.Clamp(avgC.w, 0, 255));
                }
                else
                {
                    _smoothedPos[i]  = p;
                    _smoothedCols[i] = cols[i];
                }
            }

            // Write back from scratch buffers
            for (int i = 0; i < n; i++) { pts[i] = _smoothedPos[i]; cols[i] = _smoothedCols[i]; }
        }
    }

    // ---------- Normal-Weighted Smoothing ----------

    private static void NormalWeightedSmoothInPlace(
        List<Vector3> pts, List<Color32> cols, int kNeighbors, float spatialSigma, int iterations)
    {
        int n = pts.Count;
        if (n <= kNeighbors) return;

        float sigmaSquared2 = 2f * spatialSigma * spatialSigma;
        float cell = Mathf.Max(0.01f, spatialSigma * 2f);

        for (int iter = 0; iter < iterations; iter++)
        {
            // Build grid
            var grid = new Dictionary<CellKey, List<int>>(n / 4);
            for (int i = 0; i < n; i++)
            {
                var p = pts[i];
                var ck = new CellKey(
                    Mathf.FloorToInt(p.x / cell),
                    Mathf.FloorToInt(p.y / cell),
                    Mathf.FloorToInt(p.z / cell));
                if (!grid.TryGetValue(ck, out var list))
                {
                    list = new List<int>(32);
                    grid[ck] = list;
                }
                list.Add(i);
            }

            // 1) Estimate normals via PCA
            var normals = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var p = pts[i];
                var ck = new CellKey(
                    Mathf.FloorToInt(p.x / cell),
                    Mathf.FloorToInt(p.y / cell),
                    Mathf.FloorToInt(p.z / cell));

                // Gather neighbors
                var neighbors = new List<(int idx, float sqDist)>(kNeighbors * 8);
                for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            var nk = new CellKey(ck.x + dx, ck.y + dy, ck.z + dz);
                            if (!grid.TryGetValue(nk, out var list)) continue;
                            for (int j = 0; j < list.Count; j++)
                            {
                                if (list[j] == i) continue;
                                float d2 = (pts[list[j]] - p).sqrMagnitude;
                                neighbors.Add((list[j], d2));
                            }
                        }

                neighbors.Sort((a, b) => a.sqDist.CompareTo(b.sqDist));
                int take = Mathf.Min(kNeighbors, neighbors.Count);

                if (take < 3)
                {
                    normals[i] = Vector3.up; // fallback
                    continue;
                }

                // Compute centroid of neighborhood
                Vector3 centroid = p;
                for (int j = 0; j < take; j++)
                    centroid += pts[neighbors[j].idx];
                centroid /= (take + 1);

                // Build 3x3 covariance matrix
                // cov[row, col]
                float c00 = 0, c01 = 0, c02 = 0;
                float c11 = 0, c12 = 0, c22 = 0;

                // Include the point itself
                Vector3 d = p - centroid;
                c00 += d.x * d.x; c01 += d.x * d.y; c02 += d.x * d.z;
                c11 += d.y * d.y; c12 += d.y * d.z; c22 += d.z * d.z;

                for (int j = 0; j < take; j++)
                {
                    d = pts[neighbors[j].idx] - centroid;
                    c00 += d.x * d.x; c01 += d.x * d.y; c02 += d.x * d.z;
                    c11 += d.y * d.y; c12 += d.y * d.z; c22 += d.z * d.z;
                }

                // Find smallest eigenvector via power iteration on the inverse
                // Simpler: find the eigenvector of covariance with smallest eigenvalue
                // Use iterative approach: compute largest eigenvector, deflate, repeat
                // For normal estimation, we use the cross-product of two principal directions,
                // or more robustly, find the eigenvector with smallest eigenvalue.
                // Simplified: compute normal via cross product of two most spread directions.

                // Power iteration for largest eigenvector
                Vector3 v1 = PowerIteration(c00, c01, c02, c11, c12, c22, 20);
                // Deflate
                float lambda1 = CovDot(c00, c01, c02, c11, c12, c22, v1);
                float d00 = c00 - lambda1 * v1.x * v1.x;
                float d01 = c01 - lambda1 * v1.x * v1.y;
                float d02 = c02 - lambda1 * v1.x * v1.z;
                float d11 = c11 - lambda1 * v1.y * v1.y;
                float d12 = c12 - lambda1 * v1.y * v1.z;
                float d22 = c22 - lambda1 * v1.z * v1.z;

                Vector3 v2 = PowerIteration(d00, d01, d02, d11, d12, d22, 20);

                // Normal is cross product of the two principal directions
                Vector3 normal = Vector3.Cross(v1, v2).normalized;
                if (normal.sqrMagnitude < 0.01f) normal = Vector3.up;
                normals[i] = normal;
            }

            // 2) Smooth along tangent plane
            var smoothed = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var p = pts[i];
                var norm = normals[i];
                var ck = new CellKey(
                    Mathf.FloorToInt(p.x / cell),
                    Mathf.FloorToInt(p.y / cell),
                    Mathf.FloorToInt(p.z / cell));

                var neighbors = new List<(int idx, float sqDist)>(kNeighbors * 8);
                for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            var nk = new CellKey(ck.x + dx, ck.y + dy, ck.z + dz);
                            if (!grid.TryGetValue(nk, out var list)) continue;
                            for (int j = 0; j < list.Count; j++)
                            {
                                float d2 = (pts[list[j]] - p).sqrMagnitude;
                                neighbors.Add((list[j], d2));
                            }
                        }

                neighbors.Sort((a, b) => a.sqDist.CompareTo(b.sqDist));
                int take = Mathf.Min(kNeighbors, neighbors.Count);

                Vector3 displacement = Vector3.zero;
                float sumW = 0;

                for (int j = 0; j < take; j++)
                {
                    float w = Mathf.Exp(-neighbors[j].sqDist / sigmaSquared2);
                    Vector3 diff = pts[neighbors[j].idx] - p;
                    // Project displacement onto tangent plane (remove normal component)
                    Vector3 tangentDiff = diff - Vector3.Dot(diff, norm) * norm;
                    displacement += tangentDiff * w;
                    sumW += w;
                }

                if (sumW > 1e-8f)
                    smoothed[i] = p + displacement / sumW;
                else
                    smoothed[i] = p;
            }

            // Write back
            for (int i = 0; i < n; i++)
                pts[i] = smoothed[i];
        }
    }

    // Power iteration to find largest eigenvector of a 3x3 symmetric matrix
    private static Vector3 PowerIteration(
        float c00, float c01, float c02,
        float c11, float c12, float c22, int iters)
    {
        Vector3 v = new Vector3(1, 1, 1).normalized;
        for (int i = 0; i < iters; i++)
        {
            Vector3 mv = new Vector3(
                c00 * v.x + c01 * v.y + c02 * v.z,
                c01 * v.x + c11 * v.y + c12 * v.z,
                c02 * v.x + c12 * v.y + c22 * v.z);
            float mag = mv.magnitude;
            if (mag < 1e-10f) return Vector3.up;
            v = mv / mag;
        }
        return v;
    }

    // Compute v^T * C * v for eigenvalue
    private static float CovDot(
        float c00, float c01, float c02,
        float c11, float c12, float c22, Vector3 v)
    {
        Vector3 mv = new Vector3(
            c00 * v.x + c01 * v.y + c02 * v.z,
            c01 * v.x + c11 * v.y + c12 * v.z,
            c02 * v.x + c12 * v.y + c22 * v.z);
        return Vector3.Dot(v, mv);
    }
}
