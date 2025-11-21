using K4AdotNet;
using K4AdotNet.Sensor;
using System;
using System.IO;
using UnityEngine;

public class CaptureEventArgs : EventArgs
{
    public Capture Capture { get; }
    public CaptureEventArgs(Capture capture) => Capture = capture;
}

public class KinectDevice : MonoBehaviour
{
    public int DeviceIndex = 0;
    public Matrix4x4 DeviceTransform { get; private set; } = Matrix4x4.identity;
    public DeviceConfiguration Configuration { get; private set; }
    public Calibration calibration;
    public string SerialNumber => _device?.SerialNumber ?? string.Empty;
    public bool IsInitialized { get; private set; }

    private Device _device;
    private const DepthMode TrackingDepthMode = DepthMode.NarrowViewUnbinned;
    private const ColorResolution TrackingColorResolution = ColorResolution.R720p;
    private const FrameRate TrackingFrameRate = FrameRate.Thirty;

    public event EventHandler<CaptureEventArgs> OnCaptureReady;

    private void Start()
    {
        Configuration = new DeviceConfiguration
        {
            ColorResolution = TrackingColorResolution,
            ColorFormat = ImageFormat.ColorBgra32,
            DepthMode = TrackingDepthMode,
            CameraFps = TrackingFrameRate,
            WiredSyncMode = WiredSyncMode.Standalone
        };
        if (Device.TryOpen(out _device, DeviceIndex))
        {
            _device.GetCalibration(Configuration.DepthMode, Configuration.ColorResolution, out calibration);
            _device.StartCameras(Configuration);
            IsInitialized = true;
            Debug.Log($"[{gameObject.name}] Device {SerialNumber} opened and cameras started successfully.");
            LoadTransformationMatrix();
        }
        else
        {
            Debug.LogError($"[{gameObject.name}] Cannot open device at index {DeviceIndex}");
            IsInitialized = false;
        }
    }

    private void LoadTransformationMatrix()
    {
        int cameraIndex = DeviceIndex + 1;
        if (cameraIndex == 1)
        {
            DeviceTransform = Matrix4x4.identity;
            Debug.Log($"[{gameObject.name}] Device {DeviceIndex} (Camera 1) uses Identity transform.");
            return;
        }

        string fileName = $"calib-{cameraIndex}-1.txt";
        string expectedFilePath = Path.Combine(Application.dataPath, "CalibrationFiles", fileName);
        Debug.Log($"Attempting to load calibration for Device {DeviceIndex} from: {expectedFilePath}");
        
        DeviceTransform = CalibrationUtility.LoadMatrixFromFile(fileName);
        if (DeviceTransform != Matrix4x4.identity)
            Debug.Log($"[{gameObject.name}] Loaded custom transformation matrix. Camera {cameraIndex} is now calibrated to Camera 1.");
        else
            Debug.LogWarning($"[{gameObject.name}] Could not load calibration file: {fileName}. Device will use Identity Matrix (uncalibrated).");
    }

    private void Update()
    {
        if (!IsInitialized || _device == null) 
            return;
        if (_device.TryGetCapture(out var capture))
        {
            using (capture)
                OnCaptureReady?.Invoke(this, new CaptureEventArgs(capture.DuplicateReference()));
        }
    }

    private void OnDestroy()
    {
        _device?.StopCameras();
        _device?.Dispose();
        _device = null;
    }
}