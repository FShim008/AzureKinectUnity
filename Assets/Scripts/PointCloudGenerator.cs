using K4AdotNet.Sensor;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public struct PointCloudData
{
    public List<Vector3> Points;
    public List<Color32> Colors;
    public int Count;
}

public interface IPointCloudSource
{
    event Action<PointCloudData> OnPointCloudGenerated;
}

public class PointCloudGenerator : MonoBehaviour, IPointCloudSource
{
    [SerializeField] private KinectDevice deviceComponent;
    [SerializeField] private SkeletonTracker skeletonTracker;

    [Header("Point Cloud Settings")]
    public float minDepth = 0.3f;
    public float maxDepth = 3.5f;
    public int skipPixels = 2;

    [Header("Performance")]
    [SerializeField] private int processEveryNthFrame = 1;
    private int frameCounter = 0;

    [Header("Filtering")]
    public bool FilterToHumanRegion = false;
    public Key ToggleFilterKey = Key.P;

    [Header("Geometry Mode")]
    public bool UseDepthGeometry = true;

    [Header("Color Mode")]
    public bool UseRealRgbFromColorCamera = true;

    [Header("World-Space Output")]
    public bool ApplyDeviceTransformToWorld = true;

    [Header("Color Hole Fill (Option A)")]
    public bool EnableMappedColorInpaint = true;

    [Range(0, 12)] public int InpaintIterations = 2;

    [Tooltip("IMPORTANT: set this OFF to avoid making things worse.")]
    public bool TreatZeroRgbAsInvalid = false;

    [Tooltip("Use 4-neighborhood for less bleeding.")]
    public bool UseEightNeighbors = false;

    [Header("If true, DO NOT emit points whose mapped color is invalid.")]
    public bool DropPointsWithInvalidMappedColor = true;

    [Header("Debug")]
    public int LogEveryNFrames = 0;

    private Transformation _transformation;

    private Image _xyzDepthImage;
    private Image _colorInDepthImage;

    private int _depthW = 0;
    private int _depthH = 0;

    private readonly List<Vector3> _points = new List<Vector3>(200000);
    private readonly List<Color32> _colors = new List<Color32>(200000);

    public event Action<PointCloudData> OnPointCloudGenerated;

    // managed mapped-color + mask
    private byte[] _mappedColorBGRA;
    private byte[] _mappedColorBGRA_Tmp;
    private byte[] _validMask;
    private byte[] _validMask_Tmp;

    private void Start()
    {
        if (deviceComponent == null)
        {
            Debug.LogError($"[{gameObject.name}] Missing KinectDevice.");
            enabled = false;
            return;
        }
        StartCoroutine(WaitForDeviceInitialization());
    }

    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current[ToggleFilterKey].wasPressedThisFrame)
        {
            FilterToHumanRegion = !FilterToHumanRegion;
            Debug.Log($"[{gameObject.name}] FilterToHumanRegion = {FilterToHumanRegion}");
        }
    }

    private IEnumerator WaitForDeviceInitialization()
    {
        yield return new WaitUntil(() => deviceComponent.IsInitialized);

        try
        {
            _transformation = new Transformation(in deviceComponent.calibration);
            deviceComponent.OnCaptureReady += ProcessFrame;
            Debug.Log($"[{gameObject.name}] Subscribed to DeviceIndex={deviceComponent.DeviceIndex} CameraNumber={deviceComponent.EffectiveCameraNumber}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{gameObject.name}] Failed to create Transformation: {ex}");
            enabled = false;
        }
    }

    private void EnsureBuffers(Capture capture)
    {
        if (capture.DepthImage == null) return;

        int w = capture.DepthImage.WidthPixels;
        int h = capture.DepthImage.HeightPixels;

        if (_xyzDepthImage != null && _depthW == w && _depthH == h)
            return;

        DisposeBuffers();

        _depthW = w;
        _depthH = h;

        _xyzDepthImage = new Image(ImageFormat.Custom, w, h, w * 6);
        _colorInDepthImage = new Image(ImageFormat.ColorBgra32, w, h, w * 4);

        int pixCount = w * h;
        int bytes = pixCount * 4;
        _mappedColorBGRA = new byte[bytes];
        _mappedColorBGRA_Tmp = new byte[bytes];
        _validMask = new byte[pixCount];
        _validMask_Tmp = new byte[pixCount];

        Debug.Log($"[{gameObject.name}] Allocated buffers {w}x{h}.");
    }

    private void DisposeBuffers()
    {
        _xyzDepthImage?.Dispose(); _xyzDepthImage = null;
        _colorInDepthImage?.Dispose(); _colorInDepthImage = null;
        _depthW = 0; _depthH = 0;

        _mappedColorBGRA = null;
        _mappedColorBGRA_Tmp = null;
        _validMask = null;
        _validMask_Tmp = null;
    }

    private void ProcessFrame(object sender, CaptureEventArgs e)
    {
        frameCounter++;
        if (frameCounter % processEveryNthFrame != 0) return;
        if (_transformation == null) return;

        var capture = e.Capture;

        try
        {
            if (capture.DepthImage == null) return;
            EnsureBuffers(capture);

            if (!UseDepthGeometry) return;

            _transformation.DepthImageToPointCloud(capture.DepthImage, CalibrationGeometry.Depth, _xyzDepthImage);

            byte[] colorForSampling = null;
            byte[] maskForSampling = null;

            if (UseRealRgbFromColorCamera && capture.ColorImage != null)
            {
                _transformation.ColorImageToDepthCamera(capture.DepthImage, capture.ColorImage, _colorInDepthImage);

                CopyImageToManagedBGRA(_colorInDepthImage, _mappedColorBGRA);
                BuildValidityMask(_mappedColorBGRA, _validMask, _depthW, _depthH, TreatZeroRgbAsInvalid);

                if (EnableMappedColorInpaint && InpaintIterations > 0)
                {
                    InpaintDilation(
                        _mappedColorBGRA, _mappedColorBGRA_Tmp,
                        _validMask, _validMask_Tmp,
                        _depthW, _depthH,
                        InpaintIterations,
                        UseEightNeighbors
                    );
                }

                colorForSampling = _mappedColorBGRA;
                maskForSampling = _validMask; // <- IMPORTANT: pass final validity
            }

            Image bodyIndexMap = skeletonTracker?.BodyIndexMap;
            bool bodyMapUsable =
                FilterToHumanRegion &&
                bodyIndexMap != null &&
                bodyIndexMap.WidthPixels == _depthW &&
                bodyIndexMap.HeightPixels == _depthH &&
                bodyIndexMap.StrideBytes >= _depthW;

            GeneratePointCloud_DepthGeometry(
                _xyzDepthImage,
                colorForSampling,
                maskForSampling,
                DropPointsWithInvalidMappedColor,
                bodyMapUsable ? bodyIndexMap : null
            );

            if (ApplyDeviceTransformToWorld)
                ApplyDeviceTransform();

            if (LogEveryNFrames > 0 && (frameCounter % LogEveryNFrames == 0))
                Debug.Log($"[PC] Cam {deviceComponent.EffectiveCameraNumber} pts={_points.Count}");

            OnPointCloudGenerated?.Invoke(new PointCloudData
            {
                Points = _points,
                Colors = _colors,
                Count = _points.Count
            });
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{gameObject.name}] ProcessFrame exception: {ex}");
        }
    }

    private static unsafe void CopyImageToManagedBGRA(Image img, byte[] dst)
    {
        int w = img.WidthPixels;
        int h = img.HeightPixels;
        int stride = img.StrideBytes;
        int rowBytes = w * 4;

        byte* srcBase = (byte*)img.Buffer.ToPointer();
        fixed (byte* dstBase = dst)
        {
            for (int y = 0; y < h; y++)
            {
                byte* srcRow = srcBase + y * stride;
                byte* dstRow = dstBase + y * rowBytes;
                Buffer.MemoryCopy(srcRow, dstRow, rowBytes, rowBytes);
            }
        }
    }

    private static void BuildValidityMask(byte[] bgra, byte[] mask, int w, int h, bool treatZeroRgbInvalid)
    {
        int pixCount = w * h;
        for (int i = 0; i < pixCount; i++)
        {
            int o = i * 4;
            byte b = bgra[o + 0];
            byte g = bgra[o + 1];
            byte r = bgra[o + 2];
            byte a = bgra[o + 3];

            bool valid = a > 0;
            if (valid && treatZeroRgbInvalid)
                valid = (r | g | b) != 0;

            mask[i] = (byte)(valid ? 1 : 0);
        }
    }

    private static void InpaintDilation(
        byte[] srcBGRA, byte[] tmpBGRA,
        byte[] srcMask, byte[] tmpMask,
        int w, int h,
        int iterations,
        bool use8
    )
    {
        byte[] curBGRA = srcBGRA;
        byte[] curMask = srcMask;
        byte[] nxtBGRA = tmpBGRA;
        byte[] nxtMask = tmpMask;

        int[] dx4 = { -1, 1, 0, 0 };
        int[] dy4 = { 0, 0, -1, 1 };

        int[] dx8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
        int[] dy8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

        for (int it = 0; it < iterations; it++)
        {
            Buffer.BlockCopy(curBGRA, 0, nxtBGRA, 0, curBGRA.Length);
            Buffer.BlockCopy(curMask, 0, nxtMask, 0, curMask.Length);

            int filled = 0;

            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int idx = row + x;
                    if (curMask[idx] == 1) continue;

                    bool found = false;
                    int foundIdx = -1;

                    if (use8)
                    {
                        for (int k = 0; k < 8; k++)
                        {
                            int nx = x + dx8[k];
                            int ny = y + dy8[k];
                            if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                            int nIdx = ny * w + nx;
                            if (curMask[nIdx] == 1) { found = true; foundIdx = nIdx; break; }
                        }
                    }
                    else
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            int nx = x + dx4[k];
                            int ny = y + dy4[k];
                            if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                            int nIdx = ny * w + nx;
                            if (curMask[nIdx] == 1) { found = true; foundIdx = nIdx; break; }
                        }
                    }

                    if (!found) continue;

                    int oDst = idx * 4;
                    int oSrc = foundIdx * 4;
                    nxtBGRA[oDst + 0] = curBGRA[oSrc + 0];
                    nxtBGRA[oDst + 1] = curBGRA[oSrc + 1];
                    nxtBGRA[oDst + 2] = curBGRA[oSrc + 2];
                    nxtBGRA[oDst + 3] = curBGRA[oSrc + 3];

                    nxtMask[idx] = 1;
                    filled++;
                }
            }

            var tB = curBGRA; curBGRA = nxtBGRA; nxtBGRA = tB;
            var tM = curMask; curMask = nxtMask; nxtMask = tM;

            if (filled == 0) break;
        }

        if (!ReferenceEquals(curBGRA, srcBGRA))
            Buffer.BlockCopy(curBGRA, 0, srcBGRA, 0, srcBGRA.Length);
        if (!ReferenceEquals(curMask, srcMask))
            Buffer.BlockCopy(curMask, 0, srcMask, 0, srcMask.Length);
    }

    private void GeneratePointCloud_DepthGeometry(
        Image xyzImage,
        byte[] colorBGRAOrNull,
        byte[] validMaskOrNull,
        bool dropInvalidColor,
        Image bodyIndexImageOrNull
    )
    {
        _points.Clear();
        _colors.Clear();

        int width = xyzImage.WidthPixels;
        int height = xyzImage.HeightPixels;

        bool useBody = (bodyIndexImageOrNull != null);
        bool useRgb = (colorBGRAOrNull != null);

        int minZmm = Mathf.RoundToInt(minDepth * 1000f);
        int maxZmm = Mathf.RoundToInt(maxDepth * 1000f);

        unsafe
        {
            IntPtr xyzBuffer = xyzImage.Buffer;
            int xyzStride = xyzImage.StrideBytes;

            IntPtr bodyBuffer = useBody ? bodyIndexImageOrNull.Buffer : IntPtr.Zero;
            int bodyStride = useBody ? bodyIndexImageOrNull.StrideBytes : 0;

            fixed (byte* rgbBase = useRgb ? colorBGRAOrNull : null)
            fixed (byte* maskBase = (useRgb && validMaskOrNull != null) ? validMaskOrNull : null)
            {
                int rgbRowBytes = width * 4;

                for (int y = 0; y < height; y += Mathf.Max(1, skipPixels))
                {
                    IntPtr xyzRow = IntPtr.Add(xyzBuffer, y * xyzStride);
                    IntPtr bodyRow = useBody ? IntPtr.Add(bodyBuffer, y * bodyStride) : IntPtr.Zero;

                    byte* rgbRow = useRgb ? (rgbBase + y * rgbRowBytes) : null;
                    byte* maskRow = (useRgb && maskBase != null) ? (maskBase + y * width) : null;

                    for (int x = 0; x < width; x += Mathf.Max(1, skipPixels))
                    {
                        if (useBody)
                        {
                            byte bi = *(byte*)IntPtr.Add(bodyRow, x);
                            if (bi == byte.MaxValue) continue;
                        }

                        // If mapped color is still invalid -> DROP the point (removes black patches)
                        if (useRgb && dropInvalidColor && maskRow != null && maskRow[x] == 0)
                            continue;

                        int off = x * 6;
                        short x_mm = *(short*)IntPtr.Add(xyzRow, off);
                        short y_mm = *(short*)IntPtr.Add(xyzRow, off + 2);
                        short z_mm = *(short*)IntPtr.Add(xyzRow, off + 4);

                        if (z_mm <= 0) continue;
                        if (z_mm < minZmm || z_mm > maxZmm) continue;

                        Vector3 p = new Vector3(x_mm, -y_mm, z_mm) * 0.001f;
                        _points.Add(p);

                        if (useRgb)
                        {
                            int coff = x * 4;
                            byte b = rgbRow[coff + 0];
                            byte g = rgbRow[coff + 1];
                            byte r = rgbRow[coff + 2];

                            _colors.Add(new Color32(r, g, b, 255));
                        }
                        else
                        {
                            _colors.Add(new Color32(255, 255, 255, 255));
                        }
                    }
                }
            }
        }
    }

    private void ApplyDeviceTransform()
    {
        Matrix4x4 T = deviceComponent.DeviceTransform;
        if (T == Matrix4x4.identity) return;

        for (int i = 0; i < _points.Count; i++)
            _points[i] = T.MultiplyPoint3x4(_points[i]);
    }

    private void OnDestroy()
    {
        if (deviceComponent != null)
            deviceComponent.OnCaptureReady -= ProcessFrame;

        DisposeBuffers();

        _transformation?.Dispose();
        _transformation = null;
    }
}
