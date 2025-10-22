using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;
using System;
using System.Collections.Generic;

public class PersonMeshGenerator : MonoBehaviour
{
    [SerializeField] private KinectDataCapture dataCapture;
    [SerializeField] private SkeletonTracker skeletonTracker;

    [Header("Person Mesh Settings")]
    [SerializeField] private float minDepth = 0.3f;   // meters
    [SerializeField] private float maxDepth = 3.5f;   // meters
    [SerializeField] private int skipPixels = 2;      // sample grid step (2 -> every other pixel)

    [Header("Triangle stitching")]
    [SerializeField] private float maxEdgeLengthMeters = 0.15f; // max allowed edge length to form a triangle

    private Calibration calibration;
    private byte[] latestBodyIndexData = null;

    private KinectFrameData latestFrame = null;
    private readonly object frameLock = new object();

    public event Action<List<PersonMeshData>> OnPersonMeshesGenerated;

    void Start()
    {
        if (dataCapture == null || skeletonTracker == null)
        {
            Debug.LogError("[PersonMeshGenerator] Missing dataCapture or skeletonTracker reference.");
            enabled = false;
            return;
        }

        calibration = dataCapture.GetCalibration();
        dataCapture.OnFrameReceived += OnFrameReceived;
        skeletonTracker.OnBodyIndexMapUpdated += OnBodyIndexMapUpdated;
    }

    void OnDestroy()
    {
        dataCapture.OnFrameReceived -= OnFrameReceived;
        skeletonTracker.OnBodyIndexMapUpdated -= OnBodyIndexMapUpdated;
    }

    private void OnFrameReceived(KinectFrameData frame)
    {
        lock (frameLock)
        {
            latestFrame = frame;
        }
    }

    private void OnBodyIndexMapUpdated(Image bodyIndexImage)
    {
        if (bodyIndexImage == null) return;
        latestBodyIndexData = bodyIndexImage.Memory.ToArray();
    }

    void Update()
    {
        KinectFrameData frame;
        lock (frameLock)
        {
            frame = latestFrame;
            latestFrame = null;
        }

        if (frame == null || latestBodyIndexData == null) return;

        var skeletons = skeletonTracker.GetLatestSkeletons();
        if (skeletons == null || skeletons.Count == 0) return;

        var trackedIndices = new HashSet<byte>();
        for (byte i = 0; i < skeletons.Count; i++) trackedIndices.Add(i);

        var meshes = GeneratePersonMeshes(frame, latestBodyIndexData, trackedIndices, skeletons);
        if (meshes.Count > 0)
            OnPersonMeshesGenerated?.Invoke(meshes);
    }

    private List<PersonMeshData> GeneratePersonMeshes(KinectFrameData frame, byte[] bodyIndexData, HashSet<byte> trackedIndices, List<SkeletonData> skeletons)
    {
        var result = new List<PersonMeshData>();
        int depthWidth = frame.DepthWidth;
        int depthHeight = frame.DepthHeight;

        if (bodyIndexData.Length != depthWidth * depthHeight)
        {
            Debug.LogWarning("[PersonMeshGenerator] BodyIndexMap size mismatch with depth resolution.");
            return result;
        }

        ushort[] depthData = new ushort[depthWidth * depthHeight];
        Buffer.BlockCopy(frame.DepthData, 0, depthData, 0, frame.DepthData.Length);

        byte[] colorBytes = frame.ColorData;
        int colorWidth = frame.ColorWidth;
        int colorHeight = frame.ColorHeight;

        var personVertexGrids = new Dictionary<byte, Dictionary<(int, int), int>>();
        var personMeshes = new Dictionary<byte, PersonMeshData>();

        foreach (var idx in trackedIndices)
        {
            personVertexGrids[idx] = new Dictionary<(int, int), int>();
            personMeshes[idx] = new PersonMeshData
            {
                BodyId = (idx < skeletons.Count) ? skeletons[idx].BodyId : 0,
                Vertices = new List<Vector3>(),
                Colors = new List<Color32>(),
                Triangles = new List<int>(),
                UVs = new List<Vector2>()
            };
        }

        for (int y = 0; y < depthHeight; y += skipPixels)
        {
            for (int x = 0; x < depthWidth; x += skipPixels)
            {
                int idx = y * depthWidth + x;
                byte bodyIdx = bodyIndexData[idx];

                if (bodyIdx == 255 || !trackedIndices.Contains(bodyIdx)) continue;

                ushort depth = depthData[idx];
                if (depth == 0) continue;

                float depthMeters = depth / 1000f;
                if (depthMeters < minDepth || depthMeters > maxDepth) continue;

                var depthPoint = new System.Numerics.Vector2(x, y);
                var p3 = calibration.TransformTo3D(depthPoint, depth, CalibrationDeviceType.Depth, CalibrationDeviceType.Depth);
                if (!p3.HasValue) continue;

                Vector3 position = new Vector3((float)p3.Value.X / 1000f, -(float)p3.Value.Y / 1000f, (float)p3.Value.Z / 1000f);

                var colorPoint = calibration.TransformTo2D(p3.Value, CalibrationDeviceType.Depth, CalibrationDeviceType.Color);
                Color32 color = new Color32(255, 255, 255, 255);
                Vector2 uv = Vector2.zero;

                if (colorPoint.HasValue)
                {
                    int cx = Mathf.Clamp(Mathf.RoundToInt((float)colorPoint.Value.X), 0, colorWidth - 1);
                    int cy = Mathf.Clamp(Mathf.RoundToInt((float)colorPoint.Value.Y), 0, colorHeight - 1);
                    int cIdx = (cy * colorWidth + cx) * 4;
                    if (cIdx + 3 < colorBytes.Length)
                    {
                        color = new Color32(colorBytes[cIdx + 2], colorBytes[cIdx + 1], colorBytes[cIdx], 255);
                        uv = new Vector2((float)cx / colorWidth, (float)cy / colorHeight);
                    }
                }

                var grid = personVertexGrids[bodyIdx];
                var meshData = personMeshes[bodyIdx];

                grid[(x, y)] = meshData.Vertices.Count;
                meshData.Vertices.Add(position);
                meshData.Colors.Add(color);
                meshData.UVs.Add(uv);
            }
        }

        // Stitch triangles
        foreach (var kv in personMeshes)
        {
            var meshData = kv.Value;
            var grid = personVertexGrids[kv.Key];

            for (int y = 0; y < depthHeight - skipPixels; y += skipPixels)
            {
                for (int x = 0; x < depthWidth - skipPixels; x += skipPixels)
                {
                    if (grid.TryGetValue((x, y), out int v00) &&
                        grid.TryGetValue((x + skipPixels, y), out int v10) &&
                        grid.TryGetValue((x, y + skipPixels), out int v01) &&
                        grid.TryGetValue((x + skipPixels, y + skipPixels), out int v11))
                    {
                        var p00 = meshData.Vertices[v00];
                        var p10 = meshData.Vertices[v10];
                        var p01 = meshData.Vertices[v01];
                        var p11 = meshData.Vertices[v11];

                        if (Vector3.Distance(p00, p01) < maxEdgeLengthMeters &&
                            Vector3.Distance(p00, p10) < maxEdgeLengthMeters &&
                            Vector3.Distance(p01, p10) < maxEdgeLengthMeters)
                        {
                            meshData.Triangles.Add(v00);
                            meshData.Triangles.Add(v01);
                            meshData.Triangles.Add(v10);
                        }

                        if (Vector3.Distance(p10, p11) < maxEdgeLengthMeters &&
                            Vector3.Distance(p10, p01) < maxEdgeLengthMeters &&
                            Vector3.Distance(p11, p01) < maxEdgeLengthMeters)
                        {
                            meshData.Triangles.Add(v10);
                            meshData.Triangles.Add(v01);
                            meshData.Triangles.Add(v11);
                        }
                    }
                }
            }

            if (meshData.Vertices.Count > 0)
                result.Add(meshData);
        }

        return result;
    }
}

public class PersonMeshData
{
    public uint BodyId;
    public List<Vector3> Vertices;
    public List<int> Triangles;
    public List<Vector2> UVs;
    public List<Color32> Colors;
}
