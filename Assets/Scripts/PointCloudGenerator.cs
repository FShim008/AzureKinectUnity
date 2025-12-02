using K4AdotNet.Sensor;
using MathNet.Numerics;
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
    [Tooltip("Minimum depth threshold in meters.")]
    public float minDepth = 0.3f;
    [Tooltip("Maximum depth threshold in meters.")]
    public float maxDepth = 3.5f;
    [Tooltip("Skip factor (e.g., 4 skips 4x4 pixels).")]
    public int skipPixels = 4;
    [Header("Performance")]
    [SerializeField] private int processEveryNthFrame = 1;
    private int frameCounter = 0;

    [Header("Filtering")]
    [Tooltip("If checked, the point cloud will only include points belonging to a tracked person.")]
    public bool FilterToHumanRegion = false;
    public Key ToggleFilterKey = Key.P;

    private Transformation _transformation;
    private Image _xyzImage;
    private Image _registeredDepthImage;
    private Image _registeredBodyIndexMap;

    // Data buffers to reuse memory across frames
    private readonly List<Vector3> _points = new List<Vector3>();
    private readonly List<Color32> _colors = new List<Color32>();

    public event Action<PointCloudData> OnPointCloudGenerated;

    private void Start()
    {
        if (deviceComponent == null)
        {
            Debug.LogError($"[{gameObject.name}] Missing required {nameof(KinectDevice)} component.");
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
            Debug.Log($"[{gameObject.name}] Point Cloud Filter Toggled. Filter to Human Region: {FilterToHumanRegion}");
        }
    }

    private IEnumerator WaitForDeviceInitialization()
    {
        yield return new WaitUntil(() => deviceComponent.IsInitialized);
        try
        {
            _transformation = new Transformation(in deviceComponent.calibration);
            var colorResolution = deviceComponent.Configuration.ColorResolution;
            int width = K4AdotNet.Sensor.ColorResolutions.WidthPixels(colorResolution);
            int height = K4AdotNet.Sensor.ColorResolutions.HeightPixels(colorResolution);
            _xyzImage = new Image(ImageFormat.Custom, width, height, width * 6);
            _registeredDepthImage = new Image(ImageFormat.Depth16, width, height, width * 2);
            _registeredBodyIndexMap = new Image(ImageFormat.Custom8, width, height, width);
            deviceComponent.OnCaptureReady += ProcessFrame;
            Debug.Log($"[{gameObject.name}] PointCloudGenerator initialized and subscribed to Device {deviceComponent.DeviceIndex}.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[{gameObject.name}] Failed to create K4AdotNet Transformation object: {ex.Message}");
            enabled = false;
        }
    }

    private void ProcessFrame(object sender, CaptureEventArgs e)
    {
        frameCounter++;
        if (frameCounter % processEveryNthFrame != 0)
            return;
        if (_transformation == null || _xyzImage == null || _registeredDepthImage == null) 
            return;
        Image bodyIndexMap = skeletonTracker?.BodyIndexMap;
        bool hasBodyIndexMap = bodyIndexMap != null && _registeredBodyIndexMap != null;

        using (var capture = e.Capture.DuplicateReference())
        {
            if (capture.DepthImage == null || capture.ColorImage == null) 
                return;
            _transformation.DepthImageToColorCamera(capture.DepthImage, _registeredDepthImage);
            if (hasBodyIndexMap)
                _transformation.DepthImageToColorCameraCustom(capture.DepthImage, bodyIndexMap, _registeredDepthImage, _registeredBodyIndexMap, TransformationInterpolation.Nearest, 255);
            _transformation.DepthImageToPointCloud(_registeredDepthImage, CalibrationGeometry.Color, _xyzImage);
            GeneratePointCloud(_xyzImage, capture.ColorImage, _registeredBodyIndexMap);
            ApplyDeviceTransform();
            OnPointCloudGenerated?.Invoke(new PointCloudData
            {
                Points = _points,
                Colors = _colors,
                Count = _points.Count
            });
        }
    }

    private void GeneratePointCloud(Image xyzImage, Image colorImage, Image bodyIndexImage)
    {
        _points.Clear();
        _colors.Clear();
        int width = xyzImage.WidthPixels;
        int height = xyzImage.HeightPixels;
        bool useBodyFilter = FilterToHumanRegion && bodyIndexImage != null;
        unsafe
        {
            var xyzBuffer = xyzImage.Buffer;
            var colorBuffer = colorImage.Buffer;
            int xyzStride = xyzImage.StrideBytes;
            int colorStride = colorImage.StrideBytes;
            IntPtr bodyIndexBuffer = useBodyFilter ? bodyIndexImage.Buffer : IntPtr.Zero;
            int bodyIndexStride = useBodyFilter ? bodyIndexImage.StrideBytes : 0;
            for (int y = 0; y < height; y += skipPixels)
            {
                IntPtr xyzRowPtr = IntPtr.Add(xyzBuffer, y * xyzStride);
                IntPtr colorRowPtr = IntPtr.Add(colorBuffer, y * colorStride);
                IntPtr bodyIndexRowPtr = useBodyFilter ? IntPtr.Add(bodyIndexBuffer, y * bodyIndexStride) : IntPtr.Zero;
                for (int x = 0; x < width; x += skipPixels)
                {
                    int xyzOffset = x * 6;
                    int colorOffset = x * 4;
                    if (useBodyFilter)
                    {
                        int bodyIndexOffset = x;
                        byte bodyIndex = *(byte*)IntPtr.Add(bodyIndexRowPtr, bodyIndexOffset);
                        if (bodyIndex == byte.MaxValue)
                            continue;
                    }
                    short x_mm = *(short*)IntPtr.Add(xyzRowPtr, xyzOffset);
                    short y_mm = *(short*)IntPtr.Add(xyzRowPtr, xyzOffset + 2);
                    short z_mm = *(short*)IntPtr.Add(xyzRowPtr, xyzOffset + 4);
                    // Filter points based on depth range (0 is invalid depth)
                    if (z_mm == 0 || z_mm < minDepth * 1000 || z_mm > maxDepth * 1000)
                        continue;
                    // K4A (mm) -> Unity (meters) conversion + Axis Swap
                    Vector3 unityPosition = new Vector3(x_mm, -y_mm, z_mm) * 0.001f;
                    // Get color (B, G, R, A)
                    byte b = *(byte*)IntPtr.Add(colorRowPtr, colorOffset);
                    byte g = *(byte*)IntPtr.Add(colorRowPtr, colorOffset + 1);
                    byte r = *(byte*)IntPtr.Add(colorRowPtr, colorOffset + 2);
                    Color32 color = new Color32(r, g, b, 255);
                    _points.Add(unityPosition);
                    _colors.Add(color);
                }
            }
        }
    }

    private void ApplyDeviceTransform()
    {
        if (deviceComponent.DeviceTransform == Matrix4x4.identity)
            return;
        Matrix4x4 T = deviceComponent.DeviceTransform;
        for (int i = 0; i < _points.Count; i++)
            _points[i] = T.MultiplyPoint(_points[i]);
    }

    private void OnDestroy()
    {
        if (deviceComponent != null)
            deviceComponent.OnCaptureReady -= ProcessFrame;
        _transformation?.Dispose();
        _xyzImage?.Dispose();
        _registeredDepthImage?.Dispose();
        _registeredBodyIndexMap?.Dispose();
    }
}