using K4AdotNet.BodyTracking;
using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;

public class SkeletonEventArgs : EventArgs
{
    public SkeletonData? Skeleton { get; }
    public string DeviceSerialNumber { get; }
    public static readonly SkeletonEventArgs Empty = new(null, string.Empty);
    public SkeletonEventArgs(SkeletonData? skeleton, string serial)
    {
        Skeleton = skeleton;
        DeviceSerialNumber = serial;
    }
}

public static class GlobalBodyTracking
{
    public static bool Initialized = false;
    public static bool Initializing = false;
}

public class SkeletonTracker : MonoBehaviour
{
    [SerializeField] private KinectDevice _deviceComponent;
    private Tracker _tracker;
    public event EventHandler<SkeletonEventArgs> OnSkeletonsProcessed;
    public bool IsAvailable { get; private set; }

    private IEnumerator Start()
    {
        _deviceComponent = GetComponent<KinectDevice>();
        if (_deviceComponent == null)
        {
            Debug.LogError($"[{gameObject.name}] Missing required KinectDevice component.");
            yield break;
        }

        if (!GlobalBodyTracking.Initialized && !GlobalBodyTracking.Initializing)
        {
            GlobalBodyTracking.Initializing = true;
            Debug.Log("Initializing Body Tracking Runtime");
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
                Debug.LogError($"Cannot initialize body tracking: {result.Item2}");
                yield break;
            }
        }
        yield return new WaitUntil(() => GlobalBodyTracking.Initialized);

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
            Debug.Log($"[{gameObject.name}] Tracker initialized and subscribed to capture events.");
        }
    }

    private void OnDestroy()
    {
        if (_deviceComponent != null)
            _deviceComponent.OnCaptureReady -= EnqueueCaptureForProcessing;
        _tracker?.Dispose();
    }

    private void EnqueueCaptureForProcessing(object sender, CaptureEventArgs e)
    {
        if (IsAvailable && e.Capture != null)
        {
            using var capture = e.Capture;
            if (!(capture.DepthImage is null))
                _tracker.TryEnqueueCapture(capture);
        }
    }

    private void Update()
    {
        if (!IsAvailable || _tracker == null) return;

        if (_tracker.QueueSize > 0)
        {
            if (_tracker.TryPopResult(out var bodyFrame))
            {
                //Debug.Log($"[{gameObject.name}] LOG 3: Tracker result popped (Queue Size: {_tracker.QueueSize})");
                using (bodyFrame)
                {
                    if (bodyFrame.BodyCount > 0)
                    {
                        //Debug.Log($"[{gameObject.name}] LOG 4: Found body count: {bodyFrame.BodyCount}");
                        SkeletonData skeletonData = ConvertBodyFrameToSkeletonData(bodyFrame);
                        OnSkeletonsProcessed?.Invoke(this, new SkeletonEventArgs(skeletonData, _deviceComponent.SerialNumber));
                    }
                    else
                        OnSkeletonsProcessed?.Invoke(this, SkeletonEventArgs.Empty);
                }
            }
        }
    }

    private SkeletonData ConvertBodyFrameToSkeletonData(BodyFrame bodyFrame)
    {
        Matrix4x4 transformMatrix = _deviceComponent.DeviceTransform;
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
                Vector3 position = new Vector3(k4aJoint.PositionMm.X, -k4aJoint.PositionMm.Y, k4aJoint.PositionMm.Z) * 0.001f;
                Quaternion orientation = new Quaternion(
                    k4aJoint.Orientation.X,
                    -k4aJoint.Orientation.Y,
                    -k4aJoint.Orientation.Z,
                    k4aJoint.Orientation.W
                );
                orientation = transformMatrix.rotation * orientation;
                joints[j] = new JointData
                {
                    Position = position,
                    Orientation = orientation,
                    ConfidenceLevel = k4aJoint.ConfidenceLevel
                };
            }
            
            skeletons[bodyIndex] = new Skeleton
            {
                BodyId = bid,
                Joints = joints,
                Position = transformMatrix.MultiplyPoint(new Vector3(positionMm.X, -positionMm.Y, positionMm.Z) * 0.001f),
                Orientation = transformMatrix.rotation * new Quaternion(rootJoint.Orientation.X, -rootJoint.Orientation.Y, -rootJoint.Orientation.Z, rootJoint.Orientation.W)
            };
        }
        return new SkeletonData { Skeletons = skeletons };
    }
}