using K4AdotNet.Sensor;
using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

public class CaptureEventArgs : EventArgs
{
    public Capture Capture { get; }
    public CaptureEventArgs(Capture capture) => Capture = capture;
}

public class KinectDevice : MonoBehaviour
{
    [Header("Device Identity")]
    public int DeviceIndex = 0;

    [Tooltip("IMPORTANT: set fixed 1..N per physical Kinect to prevent calibration breaking when device order changes.")]
    public int CameraNumber = 0; // 0 => fallback to DeviceIndex+1
    public int EffectiveCameraNumber => (CameraNumber > 0) ? CameraNumber : (DeviceIndex + 1);

    [Header("Calibration Directory")]
    [Tooltip("If empty, defaults to <Project>/Assets/CalibrationFiles")]
    public string CalibrationDirectoryOverride = "";

    [Header("Global Calibration Toggle (No Bootstrap)")]
    [Tooltip("All devices share one global state. One device acts as leader and flips the state on key press.")]
    public bool UseGlobalToggle = true;

    [Tooltip("DeviceIndex of the leader that will detect the keypress and flip the global state.")]
    public int GlobalToggleLeaderDeviceIndex = 0;

    [Tooltip("Key that toggles calibration globally for all devices.")]
    public Key ToggleCalibrationKey = Key.L;

    [Tooltip("If true, tries to load calib-X-Base.txt at Start().")]
    public bool AutoLoadCalibrationOnStart = true;

    [Tooltip("If true, when turning calibration ON, files are reloaded from disk (recommended).")]
    public bool ReloadFromDiskEveryEnable = true;

    [Tooltip("If true, logs matrix file path + matrix translation when toggled/loaded.")]
    public bool VerboseCalibrationLogs = true;

    [Header("Apply Calibration As")]
    [Tooltip("If false: DeviceTransform is used by SkeletonTracker/PointCloud code (data-space transform). Keep Kinect GameObject transforms identity.\nIf true: apply matrix to this GameObject transform and set DeviceTransform = identity (prevents double-transform).")]
    public bool ApplyCalibrationToGameObjectTransform = false;

    [Header("Base Camera")]
    [Tooltip("Usually 1 (Kinect-0). Must match the base used in your calib-*-Base.txt files.")]
    public int BaseCameraNum = 1;

    [Header("ICP Refinement")]
    [Tooltip("If true: Final = ICP * Calib. If false: Final = Calib * ICP. Use whichever matches your ICP solver convention.")]
    public bool IcpPreMultiply = false;

    [Tooltip("If true, prints ICP delta magnitude when applied.")]
    public bool VerboseIcpLogs = true;

    [Tooltip("If true, base camera will also show +ICP in logs when ICP is enabled (transform remains identity).")]
    public bool ShowIcpOnBaseInLogs = false;

    public Matrix4x4 DeviceTransform { get; private set; } = Matrix4x4.identity;

    public DeviceConfiguration Configuration { get; private set; }
    public Calibration calibration;
    public string SerialNumber => _device?.SerialNumber ?? string.Empty;
    public bool IsInitialized { get; private set; }

    public event EventHandler<CaptureEventArgs> OnCaptureReady;

    private Device _device;

    private const DepthMode TrackingDepthMode = DepthMode.NarrowViewUnbinned;
    private const ColorResolution TrackingColorResolution = ColorResolution.R720p;
    private const FrameRate TrackingFrameRate = FrameRate.Thirty;

    // Per-device desired state (follows global if enabled)
    private bool _useCalibration = false;

    private Matrix4x4 _loadedCalibrationMatrix = Matrix4x4.identity;
    private bool _hasLoadedCustomMatrix = false;

    // ICP correction (optional)
    private Matrix4x4 _icpDelta = Matrix4x4.identity;
    private bool _icpEnabled = false;

    public enum TransformStateMode
    {
        IDENTITY,
        CALIBRATED
    }

    // -------- Global shared state (all instances) --------
    private static bool s_globalCalibrationEnabled = false;
    private static int s_lastToggleFrame = -1;
    private static bool s_globalInitDone = false;
    private static int s_globalBaseCameraNum = 1;
    private static string s_globalCalibDir = null;
    // -----------------------------------------------------

    /// <summary>
    /// Called by ApplyICPDeltas via reflection.
    /// Applies ICP delta on top of existing calibration (does NOT overwrite calibration file matrix).
    /// </summary>
    public void SetIcpDelta(Matrix4x4 icpDelta, bool enable)
    {
        _icpDelta = icpDelta;
        _icpEnabled = enable;

        if (VerboseIcpLogs && enable)
        {
            // Quick magnitude info
            var t = new Vector3(icpDelta.m03, icpDelta.m13, icpDelta.m23);
            float tNorm = t.magnitude;

            // rotation magnitude (approx angle)
            Quaternion q = icpDelta.rotation;
            q.ToAngleAxis(out float angleDeg, out _);
            if (float.IsNaN(angleDeg)) angleDeg = 0f;

            Debug.Log($"[{gameObject.name}] ICP delta set: |t|={tNorm:F4}m, ang={angleDeg:F2}deg, mode={(IcpPreMultiply ? "ICP*Calib" : "Calib*ICP")}");
        }

        ApplyCurrentTransformState();
    }

    /// <summary>
    /// External API: force IDENTITY / CALIBRATED on this device.
    /// Optional reload control to avoid double reload when doing global operations.
    /// </summary>
    public void SetTransformState(TransformStateMode mode, bool reloadIfEnabling)
    {
        if (mode == TransformStateMode.CALIBRATED)
        {
            _useCalibration = true;

            if (reloadIfEnabling && (ReloadFromDiskEveryEnable || !_hasLoadedCustomMatrix))
                ForceReloadCalibrationFromDisk();
        }
        else
        {
            _useCalibration = false;
        }

        ApplyCurrentTransformState();
    }

    public void SetTransformState(TransformStateMode mode)
        => SetTransformState(mode, reloadIfEnabling: true);

    private void Awake()
    {
        // Leader-only global init to prevent “first Awake wins”.
        if (DeviceIndex != GlobalToggleLeaderDeviceIndex) return;
        if (s_globalInitDone) return;

        s_globalInitDone = true;

        s_globalBaseCameraNum = BaseCameraNum;
        s_globalCalibDir = GetEffectiveCalibrationDirectory();

        CalibrationUtility.BaseCameraNum = s_globalBaseCameraNum;
        CalibrationUtility.SetCalibrationDirectory(s_globalCalibDir);

        if (VerboseCalibrationLogs)
            Debug.Log($"[KinectDevice] Global calibration init: Base={s_globalBaseCameraNum}, Dir={s_globalCalibDir}");
    }

    private void Start()
    {
        Debug.Log("KinectDevice Start() called on " + gameObject.name);

        EnsureGlobalInit();

        Configuration = new DeviceConfiguration
        {
            ColorResolution = TrackingColorResolution,
            ColorFormat = ImageFormat.ColorBgra32,
            DepthMode = TrackingDepthMode,
            CameraFps = TrackingFrameRate,
            WiredSyncMode = WiredSyncMode.Standalone
        };

        if (!Device.TryOpen(out _device, DeviceIndex))
        {
            Debug.LogError($"[{gameObject.name}] Cannot open device at index {DeviceIndex}");
            IsInitialized = false;
            return;
        }

        _device.GetCalibration(Configuration.DepthMode, Configuration.ColorResolution, out calibration);
        _device.StartCameras(Configuration);
        IsInitialized = true;

        Debug.Log($"[{gameObject.name}] Device opened. Serial={SerialNumber} DeviceIndex={DeviceIndex} CameraNumber={EffectiveCameraNumber}");

        if (AutoLoadCalibrationOnStart)
            ForceReloadCalibrationFromDisk();

        _useCalibration = UseGlobalToggle ? s_globalCalibrationEnabled : false;

        ApplyCurrentTransformState();
    }

    private void Update()
    {
        if (!IsInitialized || _device == null)
            return;

        // -------- Global toggle logic (no Bootstrap) --------
        if (UseGlobalToggle && Keyboard.current != null)
        {
            if (DeviceIndex == GlobalToggleLeaderDeviceIndex &&
                Keyboard.current[ToggleCalibrationKey].wasPressedThisFrame)
            {
                if (s_lastToggleFrame != Time.frameCount)
                {
                    s_lastToggleFrame = Time.frameCount;
                    s_globalCalibrationEnabled = !s_globalCalibrationEnabled;

                    if (s_globalCalibrationEnabled)
                    {
                        if (ReloadFromDiskEveryEnable)
                        {
                            var devices = FindObjectsByType<KinectDevice>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                            foreach (var d in devices)
                                d.ForceReloadCalibrationFromDisk();
                        }

                        Debug.Log("[KinectDevice Global] CALIBRATED ON (reloaded from disk once).");
                    }
                    else
                    {
                        Debug.Log("[KinectDevice Global] CALIBRATED OFF (IDENTITY).");
                    }
                }
            }

            bool desired = s_globalCalibrationEnabled;
            if (_useCalibration != desired)
            {
                _useCalibration = desired;
                ApplyCurrentTransformState();
            }
        }
        // ---------------------------------------------------

        // Capture dispatch (safe with multiple subscribers)
        if (_device.TryGetCapture(out var capture))
        {
            try
            {
                var handlers = OnCaptureReady;
                if (handlers != null)
                {
                    foreach (EventHandler<CaptureEventArgs> h in handlers.GetInvocationList())
                    {
                        Capture perHandler = null;
                        try
                        {
                            perHandler = capture.DuplicateReference();
                            h(this, new CaptureEventArgs(perHandler));
                        }
                        catch (Exception ex)
                        {
                            Debug.LogError($"[{gameObject.name}] OnCaptureReady handler error: {ex}");
                        }
                        finally
                        {
                            try { perHandler?.Dispose(); } catch { }
                        }
                    }
                }
            }
            finally
            {
                try { capture.Dispose(); } catch { }
            }
        }
    }

    private void EnsureGlobalInit()
    {
        if (s_globalInitDone) return;

        // Fallback init if leader didn't run (script order edge-case)
        s_globalBaseCameraNum = BaseCameraNum;
        s_globalCalibDir = GetEffectiveCalibrationDirectory();

        CalibrationUtility.BaseCameraNum = s_globalBaseCameraNum;
        CalibrationUtility.SetCalibrationDirectory(s_globalCalibDir);

        s_globalInitDone = true;

        if (VerboseCalibrationLogs)
        {
            Debug.LogWarning($"[KinectDevice] Global calibration init fallback: Base={s_globalBaseCameraNum}, Dir={s_globalCalibDir}. " +
                             $"(Check Script Execution Order; leader should ideally init first.)");
        }
    }

    private string GetEffectiveCalibrationDirectory()
    {
        if (!string.IsNullOrWhiteSpace(CalibrationDirectoryOverride))
            return CalibrationDirectoryOverride;

        return Path.Combine(Application.dataPath, "CalibrationFiles");
    }

    /// <summary>
    /// Load calib-(camNum)-(BaseCameraNum).txt into _loadedCalibrationMatrix
    /// </summary>
    private void LoadCalibrationFromDisk()
    {
        int camNum = EffectiveCameraNumber;

        int baseNum = s_globalBaseCameraNum;
        string dir = s_globalCalibDir ?? GetEffectiveCalibrationDirectory();

        if (camNum == baseNum)
        {
            _loadedCalibrationMatrix = Matrix4x4.identity;
            _hasLoadedCustomMatrix = true;
            return;
        }

        string fileName = CalibrationUtility.GetCalibrationFileName(camNum, baseNum);
        string expectedPath = Path.Combine(dir, fileName);

        // This assumes CalibrationUtility.TryLoadMatrixFromFile reads from its internally set directory.
        // Ensure global SetCalibrationDirectory ran (we do it in EnsureGlobalInit/leader Awake).
        if (CalibrationUtility.TryLoadMatrixFromFile(fileName, out var m))
        {
            _loadedCalibrationMatrix = m;
            _hasLoadedCustomMatrix = true;

            if (VerboseCalibrationLogs)
            {
                var t = new Vector3(m.m03, m.m13, m.m23);
                Debug.Log($"[{gameObject.name}] Loaded calibration: {expectedPath}  T=({t.x:F3},{t.y:F3},{t.z:F3})");
            }
        }
        else
        {
            _loadedCalibrationMatrix = Matrix4x4.identity;
            _hasLoadedCustomMatrix = false;

            if (VerboseCalibrationLogs)
                Debug.LogWarning($"[{gameObject.name}] Calibration file NOT found/parsed: {expectedPath}");
        }
    }

    public void ForceReloadCalibrationFromDisk()
    {
        LoadCalibrationFromDisk();
    }

    /// <summary>
    /// Calibrator can push solved matrix directly (optional).
    /// NOTE: This is NOT ICP. ICP should use SetIcpDelta.
    /// </summary>
    public void SetExternalCalibration(Matrix4x4 camToBase, bool enableNow = true)
    {
        _loadedCalibrationMatrix = camToBase;
        _hasLoadedCustomMatrix = true;
        if (enableNow) _useCalibration = true;

        ApplyCurrentTransformState();
    }

    private void ApplyCurrentTransformState()
    {
        int baseNum = s_globalBaseCameraNum;
        bool isBase = (EffectiveCameraNumber == baseNum);

        bool wantCalibrated = _useCalibration;
        bool canCalibrate = isBase ? true : _hasLoadedCustomMatrix;
        bool shouldUse = wantCalibrated && canCalibrate;

        Matrix4x4 finalM = Matrix4x4.identity;

        if (shouldUse && !isBase)
        {
            // Start with calibration
            Matrix4x4 calibM = _loadedCalibrationMatrix;

            // Apply ICP delta on top if enabled
            if (_icpEnabled)
            {
                finalM = IcpPreMultiply ? (_icpDelta * calibM) : (calibM * _icpDelta);
            }
            else
            {
                finalM = calibM;
            }
        }
        else
        {
            // identity when calibration off, and base camera always identity
            finalM = Matrix4x4.identity;
        }

        if (ApplyCalibrationToGameObjectTransform)
        {
            ApplyMatrixToUnityTransform(finalM);
            DeviceTransform = Matrix4x4.identity;
        }
        else
        {
            DeviceTransform = finalM;
        }

        bool showIcpFlag = _icpEnabled && (!isBase || ShowIcpOnBaseInLogs);

        Debug.Log($"[{gameObject.name}] Transform State: {(shouldUse ? "CALIBRATED" : "IDENTITY")}  (Cam={EffectiveCameraNumber}, Base={baseNum})" +
                  (showIcpFlag ? " +ICP" : ""));
    }

    private void ApplyMatrixToUnityTransform(Matrix4x4 m)
    {
        Vector3 pos = new Vector3(m.m03, m.m13, m.m23);
        Quaternion rot = m.rotation;

        transform.localPosition = pos;
        transform.localRotation = rot;
    }

    private void OnDestroy()
    {
        try { _device?.StopCameras(); } catch { }
        try { _device?.Dispose(); } catch { }
        _device = null;
    }
}
