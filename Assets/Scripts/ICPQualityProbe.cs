using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

public class IcpQualityProbe : MonoBehaviour
{
    [Header("Input")]
    public Key ProbeKey = Key.P;

    [Header("Base Camera")]
    public int BaseCameraNum = 1;

    [Header("Sampling")]
    [Tooltip("How many points to sample from each camera cloud for the error metric.")]
    public int SampleCountPerCam = 5000;

    [Header("Nearest Neighbor Approximation")]
    [Tooltip("Grid cell size in meters for approximate nearest neighbor search. Smaller = more accurate but slower.")]
    public float CellSizeMeters = 0.05f;

    [Tooltip("Search radius in grid cells (1 means 3x3x3 cells). Increase if overlap is weak.")]
    public int NeighborCellRadius = 1;

    [Header("Logging")]
    public bool Verbose = true;

    // camNum -> latest points
    private readonly Dictionary<int, List<Vector3>> _latestPoints = new Dictionary<int, List<Vector3>>();

    // Keep subscriptions so we can unsubscribe later
    private readonly List<PointCloudGenerator> _gens = new List<PointCloudGenerator>();

    // Reflection: PointCloudGenerator has private field "deviceComponent"
    private FieldInfo _deviceField;

    private void Start()
    {
        _deviceField = typeof(PointCloudGenerator).GetField("deviceComponent", BindingFlags.Instance | BindingFlags.NonPublic);
        if (_deviceField == null)
        {
            Debug.LogError("[ICPProbe] Could not find PointCloudGenerator private field 'deviceComponent'. Probe disabled.");
            enabled = false;
            return;
        }

        _gens.Clear();
        _gens.AddRange(FindObjectsByType<PointCloudGenerator>(FindObjectsInactive.Include, FindObjectsSortMode.None));

        if (_gens.Count == 0)
        {
            Debug.LogWarning("[ICPProbe] No PointCloudGenerator found in scene.");
            return;
        }

        foreach (var g in _gens)
        {
            if (g == null) continue;
            g.OnPointCloudGenerated += (pc) => OnCloud(g, pc);
        }

        if (Verbose)
            Debug.Log($"[ICPProbe] Subscribed to {_gens.Count} PointCloudGenerators. Press '{ProbeKey}' to print alignment metrics.");
    }

    private void OnDestroy()
    {
        // Can't reliably unsubscribe lambdas; keep it simple (Unity will destroy scene anyway).
        // If you want strict unsubscribe, replace lambdas with explicit handlers per generator.
    }

    private void Update()
    {
        if (Keyboard.current == null) return;

        if (Keyboard.current[ProbeKey].wasPressedThisFrame)
        {
            PrintMetrics();
        }
    }

    private void OnCloud(PointCloudGenerator gen, PointCloudData pc)
    {
        if (pc.Points == null || pc.Count <= 0) return;

        int camNum = GetCameraNumberFromGenerator(gen);
        if (camNum <= 0) return;

        // Store a copy (important: generator reuses the same list each frame and clears it)
        // Copy only points (colors not needed for error metric)
        var copy = new List<Vector3>(pc.Count);
        // pc.Count is authoritative
        int n = Mathf.Min(pc.Count, pc.Points.Count);
        for (int i = 0; i < n; i++) copy.Add(pc.Points[i]);

        _latestPoints[camNum] = copy;
    }

    private int GetCameraNumberFromGenerator(PointCloudGenerator gen)
    {
        try
        {
            var kd = _deviceField.GetValue(gen) as KinectDevice;
            if (kd != null) return kd.EffectiveCameraNumber;
        }
        catch { }

        // Fallback: try parent KinectDevice
        var parent = gen.GetComponentInParent<KinectDevice>();
        if (parent != null) return parent.EffectiveCameraNumber;

        return -1;
    }

    private void PrintMetrics()
    {
        if (!_latestPoints.TryGetValue(BaseCameraNum, out var basePts) || basePts == null || basePts.Count == 0)
        {
            Debug.LogWarning($"[ICPProbe] No base cloud for Cam{BaseCameraNum}. Wait for point clouds to stream, then press '{ProbeKey}'.");
            return;
        }

        // Build grid index for base points
        var grid = new GridIndex(basePts, CellSizeMeters);

        // Evaluate each camera vs base
        var cams = _latestPoints.Keys.OrderBy(x => x).ToList();

        foreach (var cam in cams)
        {
            if (cam == BaseCameraNum) continue;

            var src = _latestPoints[cam];
            if (src == null || src.Count == 0)
            {
                Debug.LogWarning($"[ICPProbe] Cam{cam} has no points yet.");
                continue;
            }

            var distances = ComputeNearestDistances(src, grid, SampleCountPerCam, NeighborCellRadius);
            if (distances.Count == 0)
            {
                Debug.LogWarning($"[ICPProbe] Cam{cam}->Base had 0 matched distances (maybe no overlap / too small NeighborCellRadius).");
                continue;
            }

            distances.Sort();
            float mean = distances.Average();
            float median = distances[distances.Count / 2];
            float p90 = distances[(int)Mathf.Clamp(Mathf.FloorToInt(0.90f * (distances.Count - 1)), 0, distances.Count - 1)];

            Debug.Log($"[ICPProbe] Cam{cam}->Base: mean={mean:F3}m median={median:F3}m p90={p90:F3}m (N={distances.Count})");
        }

        Debug.Log("[ICPProbe] Tip: toggle ICP OFF/ON and press K each time. Metrics should drop when ICP helps.");
    }

    private List<float> ComputeNearestDistances(List<Vector3> srcPts, GridIndex baseGrid, int sampleCount, int neighborRadius)
    {
        var dists = new List<float>(Mathf.Min(sampleCount, srcPts.Count));

        if (srcPts.Count == 0) return dists;

        int n = srcPts.Count;
        int step = Mathf.Max(1, n / Mathf.Max(1, sampleCount));

        // Deterministic subsampling (stable across presses)
        for (int i = 0; i < n && dists.Count < sampleCount; i += step)
        {
            Vector3 p = srcPts[i];
            if (baseGrid.TryFindNearest(p, neighborRadius, out float bestDist))
                dists.Add(bestDist);
        }

        return dists;
    }

    // ---------------- Grid Index ----------------
    private class GridIndex
    {
        private readonly float _cell;
        private readonly Dictionary<long, List<Vector3>> _cells = new Dictionary<long, List<Vector3>>(1 << 16);

        public GridIndex(List<Vector3> pts, float cellSizeMeters)
        {
            _cell = Mathf.Max(1e-4f, cellSizeMeters);

            foreach (var p in pts)
            {
                var key = Key(HashCoord(p));
                if (!_cells.TryGetValue(key, out var list))
                {
                    list = new List<Vector3>(64);
                    _cells[key] = list;
                }
                list.Add(p);
            }
        }

        public bool TryFindNearest(Vector3 q, int neighborRadius, out float bestDist)
        {
            bestDist = float.PositiveInfinity;

            var (ix, iy, iz) = HashCoord(q);

            bool found = false;
            int r = Mathf.Max(0, neighborRadius);

            for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dz = -r; dz <= r; dz++)
                    {
                        long k = Key((ix + dx, iy + dy, iz + dz));
                        if (!_cells.TryGetValue(k, out var list)) continue;

                        for (int i = 0; i < list.Count; i++)
                        {
                            float d = Vector3.Distance(q, list[i]);
                            if (d < bestDist)
                            {
                                bestDist = d;
                                found = true;
                            }
                        }
                    }

            return found;
        }

        private (int, int, int) HashCoord(Vector3 p)
        {
            int ix = Mathf.FloorToInt(p.x / _cell);
            int iy = Mathf.FloorToInt(p.y / _cell);
            int iz = Mathf.FloorToInt(p.z / _cell);
            return (ix, iy, iz);
        }

        // Pack 3 ints into one long key (simple hashing)
        private static long Key((int x, int y, int z) c)
        {
            unchecked
            {
                // Large primes for mixing
                long hx = (long)c.x * 73856093;
                long hy = (long)c.y * 19349663;
                long hz = (long)c.z * 83492791;
                return hx ^ hy ^ hz;
            }
        }
    }
}
