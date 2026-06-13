using System;
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class TR3DPointPacker : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("Drag your FusionPointCloud (PointCloudFusionSource) here.")]
    public PointCloudFusionSource fusionSource;

    [Header("Packing")]
    [Tooltip("Used when no Colors are available in the PointCloudData.")]
    [Range(0f, 1f)]
    public float constantIntensity = 1.0f;

    [Tooltip("If true, compute intensity from RGB when Colors are available.")]
    public bool useColorToIntensity = true;

    [Header("Debug")]
    [Tooltip("Log every N frames (0 disables).")]
    public int logEveryNFrames = 30;

    [Tooltip("Press 'B' to write a single frame.bin next to the project (same level as Assets).")]
    public bool enableBinDumpWithBKey = true;

    [Header("TR3D Export Settings")]
    [Tooltip("If true, saves as (x,y,z,r,g,b) (6 floats). If false, saves as (x,y,z,intensity) (4 floats).")]
    public bool saveAsRGB = true;

    [Tooltip("If true, swaps Unity Y-up to Z-up (x, z, y).")]
    public bool swapYZ = true;

    // latest packed buffer (Nx4): [x,y,z,intensity]
    // Note: We keep this buffer as 4 floats for simple internal logic/visualization if needed,
    // but we will pack differently when saving if saveAsRGB is true.
    public float[] LatestPackedPoints { get; private set; }
    public int LatestPointCount { get; private set; }
    public int LatestFrameId { get; private set; }
    // We also need to store the raw colors to support saving as RGB later
    private Color32[] _cachedColors;
    private Vector3[] _cachedPoints;

    private void OnEnable()
    {
        if (fusionSource == null)
        {
            Debug.LogError("[TR3DPointPacker] fusionSource is null. Drag your PointCloudFusionSource here.");
            enabled = false;
            return;
        }

        fusionSource.OnPointCloudGenerated -= OnFusedCloud;
        fusionSource.OnPointCloudGenerated += OnFusedCloud;
    }

    private void OnDisable()
    {
        if (fusionSource != null)
            fusionSource.OnPointCloudGenerated -= OnFusedCloud;
    }

    private void Update()
    {
        if (!enableBinDumpWithBKey) return;

#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null && kb.bKey.wasPressedThisFrame)
        {
            string path = Application.dataPath + $"/Saved_Frames/frame_{System.DateTime.Now:yyyyMMdd_HHmmss}.bin";
            SaveBinOnce(path);
        }
#else
        // If someone switches back to old input system, just disable this feature.
        // (No UnityEngine.Input usage to avoid runtime errors.)
#endif
    }

    private void OnFusedCloud(PointCloudData pc)
    {
        if (pc.Points == null || pc.Count <= 0) return;

        int n = Mathf.Min(pc.Count, pc.Points.Count);
        if (n <= 0) return;

        LatestPointCount = n;
        LatestFrameId++;

        // Cache raw data for saving logic
        // We reuse arrays if they are large enough to avoid GC allocs
        if (_cachedPoints == null || _cachedPoints.Length < n)
            _cachedPoints = new Vector3[Mathf.NextPowerOfTwo(n)];
        
        // Copy points
        for(int i=0; i<n; i++) _cachedPoints[i] = pc.Points[i];

        bool hasColors = (pc.Colors != null && pc.Colors.Count >= n);
        if (hasColors)
        {
            if (_cachedColors == null || _cachedColors.Length < n)
                _cachedColors = new Color32[Mathf.NextPowerOfTwo(n)];
            // Copy colors
            for(int i=0; i<n; i++) _cachedColors[i] = pc.Colors[i];
        }

        // For backwards compatibility / existing logic, we still populate LatestPackedPoints (Nx4)
        // allocate/reuse (keep >= needed to avoid realloc spikes)
        int needed = n * 4;
        if (LatestPackedPoints == null || LatestPackedPoints.Length < needed)
            LatestPackedPoints = new float[Mathf.NextPowerOfTwo(needed)];

        for (int i = 0; i < n; i++)
        {
            Vector3 p = pc.Points[i];
            int baseIdx = i * 4;

            LatestPackedPoints[baseIdx + 0] = p.x;
            LatestPackedPoints[baseIdx + 1] = p.y;
            LatestPackedPoints[baseIdx + 2] = p.z;

            float intensity = constantIntensity;

            if (useColorToIntensity && hasColors)
            {
                Color32 c = pc.Colors[i];
                intensity = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
            }

            LatestPackedPoints[baseIdx + 3] = intensity;
        }

        if (logEveryNFrames > 0 && (LatestFrameId % logEveryNFrames == 0))
        {
            Vector3 first = pc.Points[0];
            Debug.Log($"[TR3DPointPacker] Packed frame {LatestFrameId}, N={n}, first=({first.x:F2}, {first.y:F2}, {first.z:F2}), colors={(hasColors ? "yes" : "no")}");
        }
    }

    public void SaveBinOnce(string path)
    {
        if (LatestPointCount <= 0 || _cachedPoints == null)
        {
            Debug.LogError("[TR3DPointPacker] No packed data available to save.");
            return;
        }

        int n = LatestPointCount;
        int stride = saveAsRGB ? 6 : 4;
        int totalFloats = n * stride;
        int bytesLen = totalFloats * sizeof(float);
        
        // We pack into a temporary float array for writing
        float[] writeBuffer = new float[totalFloats];
        
        bool allowColor = saveAsRGB && (_cachedColors != null && _cachedColors.Length >= n);

        for (int i = 0; i < n; i++)
        {
            int baseIdx = i * stride;
            Vector3 p = _cachedPoints[i];

            // 1. Coordinate System
            // Unity is (X, Y, Z). TR3D/SUNRGBD often expects Z-up. 
            // Standard mapping Unity -> Z-up (Right Handed):
            // Unity X  ->  X
            // Unity Y  ->  Z
            // Unity Z  ->  Y (or -Y depending on exact convention, but typical swap is X->X, Y->Z, Z->Y)
            float x = p.x;
            float y = p.y;
            float z = p.z;

            if (swapYZ)
            {
                // Simple swap to make Y up -> Z up
                writeBuffer[baseIdx + 0] = x;
                writeBuffer[baseIdx + 1] = z;
                writeBuffer[baseIdx + 2] = y;
            }
            else
            {
                writeBuffer[baseIdx + 0] = x;
                writeBuffer[baseIdx + 1] = y;
                writeBuffer[baseIdx + 2] = z;
            }

            // 2. Color / Intensity
            if (saveAsRGB)
            {
                if (allowColor)
                {
                    Color32 c = _cachedColors[i];
                    // Normalize 0-255 to 0.0-1.0
                    writeBuffer[baseIdx + 3] = c.r / 255f;
                    writeBuffer[baseIdx + 4] = c.g / 255f;
                    writeBuffer[baseIdx + 5] = c.b / 255f;
                }
                else
                {
                    // Fallback to white or constant if no color but RGB requested
                    float val = constantIntensity;
                    writeBuffer[baseIdx + 3] = val;
                    writeBuffer[baseIdx + 4] = val;
                    writeBuffer[baseIdx + 5] = val;
                }
            }
            else
            {
                // Intensity mode (original 4th channel)
                // We re-calculate intensity or grab it from LatestPackedPoints, 
                // but since we are here, let's just re-calc for simplicity or grab from cache.
                // For consistency with original code:
                float intensity = constantIntensity;
                if (useColorToIntensity && allowColor)
                {
                    Color32 c = _cachedColors[i];
                    intensity = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
                }
                writeBuffer[baseIdx + 3] = intensity;
            }
        }

        byte[] bytes = new byte[bytesLen];
        Buffer.BlockCopy(writeBuffer, 0, bytes, 0, bytesLen);

        System.IO.File.WriteAllBytes(path, bytes);
        Debug.Log($"[TR3DPointPacker] Saved {LatestPointCount} points ({stride} dimensions) to: {path}\nFormat: {(saveAsRGB ? "XYZRGB" : "XYZI")}, SwapYZ: {swapYZ}");
    }
}

