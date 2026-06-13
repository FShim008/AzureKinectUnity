using K4AdotNet.Sensor;
using K4AdotNet.BodyTracking;
using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;

// ✅ FIX: avoid Debug ambiguity
using UDebug = UnityEngine.Debug;
using Stopwatch = System.Diagnostics.Stopwatch;

public class SkeletonEventArgs : EventArgs
{
    public SkeletonData? Skeleton { get; }
    public string DeviceSerialNumber { get; }

    // comparable across devices
    public long HostTimestampUsec { get; }
    public long DeviceTimestampUsec { get; }

    public static new readonly SkeletonEventArgs Empty = new(null, string.Empty, 0, 0);

    public SkeletonEventArgs(SkeletonData? skeleton, string serial, long hostUsec, long deviceUsec)
    {
        Skeleton = skeleton;
        DeviceSerialNumber = serial;
        HostTimestampUsec = hostUsec;
        DeviceTimestampUsec = deviceUsec;
    }
}

public static class GlobalBodyTracking
{
    public static bool Initialized = false;
    public static bool Initializing = false;
}

public static class GlobalHostClock
{
    private static readonly Stopwatch _sw = Stopwatch.StartNew();

    public static long NowUsec()
    {
        double sec = (double)_sw.ElapsedTicks / System.Diagnostics.Stopwatch.Frequency;
        return (long)(sec * 1_000_000.0);
    }
}

public class SkeletonTracker : MonoBehaviour
{
    private Tracker _tracker;

    [SerializeField] private KinectDevice _deviceComponent;

    public event EventHandler<SkeletonEventArgs> OnSkeletonsProcessed;
    public bool IsAvailable { get; private set; }

    private BodyFrame _latestBodyFrame;
    public Image BodyIndexMap => _latestBodyFrame?.BodyIndexMap;

    [Header("Debug")]
    public bool LogDeviceTransform = false;
    public int LogDeviceTransformEveryNFrames = 300;

    private IEnumerator Start()
    {
        if (_deviceComponent == null)
        {
            UDebug.LogError($"[{gameObject.name}] Missing required KinectDevice component.");
            yield break;
        }

        // 1) Init runtime once globally
        if (!GlobalBodyTracking.Initialized && !GlobalBodyTracking.Initializing)
        {
            GlobalBodyTracking.Initializing = true;
            UDebug.Log("Initializing Body Tracking Runtime");

            var task = Task.Run(() =>
            {
                bool initialized = K4AdotNet.Sdk.TryInitializeBodyTrackingRuntime(TrackerProcessingMode.Gpu, out var message);
                return Tuple.Create(initialized, message);
            });

            yield return new WaitUntil(() => task.IsCompleted);

            var result = task.Result;
            GlobalBodyTracking.Initialized = result.Item1;
            GlobalBodyTracking.Initializing = false;

            if (!GlobalBodyTracking.Initialized)
            {
                UDebug.LogError($"Cannot initialize body tracking: {result.Item2}");
                yield break;
            }
        }

        yield return new WaitUntil(() => GlobalBodyTracking.Initialized);

        // 2) Wait for device
        yield return new WaitUntil(() => _deviceComponent.IsInitialized);

        if (_deviceComponent.IsInitialized)
        {
            var calibration = _deviceComponent.calibration;
            var config = TrackerConfiguration.Default;
            config.ProcessingMode = TrackerProcessingMode.Gpu;
            config.ModelPath = K4AdotNet.Sdk.BODY_TRACKING_DNN_MODEL_FILE_NAME;

            _tracker = new Tracker(in calibration, config);

            _deviceComponent.OnCaptureReady += EnqueueCaptureForProcessing;

            IsAvailable = true;
            UDebug.Log($"[{gameObject.name}] Tracker initialized and subscribed to capture events. Serial={_deviceComponent.SerialNumber}");
        }
    }

    private void OnDestroy()
    {
        if (_deviceComponent != null)
            _deviceComponent.OnCaptureReady -= EnqueueCaptureForProcessing;

        try { _tracker?.Dispose(); } catch { }
        _tracker = null;

        try { _latestBodyFrame?.Dispose(); } catch { }
        _latestBodyFrame = null;
    }

    private void EnqueueCaptureForProcessing(object sender, CaptureEventArgs e)
    {
        if (!IsAvailable || _tracker == null)
            return;

        var src = e.Capture;
        if (src == null)
            return;

        Capture dup = null;
        try
        {
            dup = src.DuplicateReference();

            if (dup.DepthImage == null)
                return;

            _tracker.TryEnqueueCapture(dup);
        }
        catch (Exception ex)
        {
            UDebug.LogError($"[{gameObject.name}] EnqueueCaptureForProcessing failed: {ex}");
        }
        finally
        {
            try { dup?.Dispose(); } catch { }
        }
    }

    private void Update()
    {
        if (!IsAvailable || _tracker == null)
            return;

        while (_tracker.TryPopResult(out var bodyFrame))
        {
            long hostUsec = GlobalHostClock.NowUsec();

            long deviceUsec = 0;
            try { deviceUsec = bodyFrame.DeviceTimestamp.ValueUsec; } catch { }

            try
            {
                _latestBodyFrame?.Dispose();
                _latestBodyFrame = bodyFrame.DuplicateReference();
            }
            catch { }

            using (bodyFrame)
            {
                if (bodyFrame.BodyCount > 0)
                {
                    SkeletonData skeletonData = ConvertBodyFrameToSkeletonData(bodyFrame, hostUsec);
                    OnSkeletonsProcessed?.Invoke(this, new SkeletonEventArgs(skeletonData, _deviceComponent.SerialNumber, hostUsec, deviceUsec));
                }
                else
                {
                    OnSkeletonsProcessed?.Invoke(this, new SkeletonEventArgs(null, _deviceComponent.SerialNumber, hostUsec, deviceUsec));
                }
            }
        }
    }

    private SkeletonData ConvertBodyFrameToSkeletonData(BodyFrame bodyFrame, long hostTimestampUsec)
    {
        Matrix4x4 transformMatrix = _deviceComponent.DeviceTransform;

        if (LogDeviceTransform && LogDeviceTransformEveryNFrames > 0 && (Time.frameCount % LogDeviceTransformEveryNFrames == 0))
        {
            var t = new Vector3(transformMatrix.m03, transformMatrix.m13, transformMatrix.m23);
            var r = transformMatrix.rotation.eulerAngles;
            UDebug.Log($"[{gameObject.name}] DeviceTransform T=({t.x:F3},{t.y:F3},{t.z:F3}) R=({r.x:F1},{r.y:F1},{r.z:F1})");
        }

        Quaternion transformRotation = transformMatrix.rotation;
        var skeletons = new Skeleton[bodyFrame.BodyCount];

        for (int bodyIndex = 0; bodyIndex < bodyFrame.BodyCount; bodyIndex++)
        {
            bodyFrame.GetBodySkeleton(bodyIndex, out var k4aSkeleton);
            uint bid = (uint)bodyFrame.GetBodyId(bodyIndex).Value;

            var joints = new JointData[32];
            var rootJoint = k4aSkeleton[(int)JointType.SpineNavel];
            var positionMm = rootJoint.PositionMm;

            for (int j = 0; j < 32; j++)
            {
                var k4aJoint = k4aSkeleton[j];

                Vector3 rawPosition = new Vector3(
                    k4aJoint.PositionMm.X,
                    -k4aJoint.PositionMm.Y,
                    k4aJoint.PositionMm.Z
                ) * 0.001f;

                Quaternion rawOrientation = new Quaternion(
                    k4aJoint.Orientation.X,
                    -k4aJoint.Orientation.Y,
                    -k4aJoint.Orientation.Z,
                    k4aJoint.Orientation.W
                );

                Vector3 finalPosition = transformMatrix.MultiplyPoint3x4(rawPosition);
                Quaternion finalOrientation = transformRotation * rawOrientation;

                joints[j] = new JointData
                {
                    Position = finalPosition,
                    Orientation = finalOrientation,
                    ConfidenceLevel = k4aJoint.ConfidenceLevel
                };
            }

            skeletons[bodyIndex] = new Skeleton
            {
                BodyId = bid,
                Joints = joints,
                Position = transformMatrix.MultiplyPoint3x4(new Vector3(positionMm.X, -positionMm.Y, positionMm.Z) * 0.001f),
                Orientation = transformRotation * new Quaternion(rootJoint.Orientation.X, -rootJoint.Orientation.Y, -rootJoint.Orientation.Z, rootJoint.Orientation.W)
            };
        }

        // ✅ Timestamp is HOST time (comparable across devices)
        return new SkeletonData
        {
            Skeletons = skeletons,
            Timestamp = hostTimestampUsec
        };
    }
}
