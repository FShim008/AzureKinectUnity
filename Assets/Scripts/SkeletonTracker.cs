using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;
using Microsoft.Azure.Kinect.BodyTracking;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

public class SkeletonTracker : MonoBehaviour
{
    [SerializeField] private KinectDataCapture dataCapture;

    [Header("Tracking Settings")]
    [SerializeField] private int maxBodies = 4;
    [SerializeField] private SensorOrientation sensorOrientation = SensorOrientation.Default;
    [SerializeField] private TrackerProcessingMode processingMode = TrackerProcessingMode.Gpu; // Changed to CPU by default
    [SerializeField] private int timeoutMs = 100; // Timeout for PopResult

    [Header("Debug")]
    [SerializeField] private bool verboseLogging = false;

    public event Action<List<SkeletonData>> OnSkeletonsUpdated;
    public event Action<Image> OnBodyIndexMapUpdated;
    public event Action<Frame, Image, List<SkeletonData>> OnPersonDataAvailable;

    private Tracker bodyTracker;
    private bool isTracking = false;
    private int frameCount = 0;
    private int errorCount = 0;

    private Frame latestFrame;
    private Image latestBodyIndexMap;
    private List<SkeletonData> latestSkeletons;

    void Start()
    {
        if (dataCapture != null)
        {
            InitializeBodyTracking();
            if (isTracking)
            {
                dataCapture.OnFrameReceived += ProcessFrameForTracking;
            }
        }
    }

    void InitializeBodyTracking()
    {
        try
        {
            Debug.Log($"[SkeletonTracker] Initializing body tracking in {processingMode} mode...");

            var trackerConfig = new TrackerConfiguration
            {
                SensorOrientation = sensorOrientation,
                ProcessingMode = processingMode
            };

            bodyTracker = Tracker.Create(dataCapture.GetCalibration(), trackerConfig);
            isTracking = true;

            Debug.Log($"[SkeletonTracker] ✓ Body tracking initialized successfully in {processingMode} mode");
        }
        catch (Exception e)
        {
            Debug.LogError($"[SkeletonTracker] Failed to initialize body tracking: {e.Message}\n" +
                $"Possible issues:\n" +
                $"1. Body Tracking SDK not installed\n" +
                $"2. Missing dnn_model_2_0_op11.onnx file\n" +
                $"3. Missing k4abt.dll or onnxruntime.dll\n" +
                $"4. If using GPU mode: Missing CUDA/cuDNN DLLs\n" +
                $"Try setting Processing Mode to 'Cpu' in Inspector");

            // Try CPU fallback if GPU failed
            if (processingMode == TrackerProcessingMode.Gpu)
            {
                Debug.Log("[SkeletonTracker] Attempting CPU fallback...");
                try
                {
                    var cpuConfig = new TrackerConfiguration
                    {
                        SensorOrientation = sensorOrientation,
                        ProcessingMode = TrackerProcessingMode.Cpu
                    };

                    bodyTracker = Tracker.Create(dataCapture.GetCalibration(), cpuConfig);
                    isTracking = true;
                    processingMode = TrackerProcessingMode.Cpu;

                    Debug.Log("[SkeletonTracker] ✓ CPU fallback successful");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[SkeletonTracker] CPU fallback also failed: {ex.Message}");
                }
            }
        }
    }

    async void ProcessFrameForTracking(KinectFrameData frameData)
    {
        if (!isTracking || bodyTracker == null || frameData.Capture == null)
            return;

        try
        {
            frameCount++;

            // Enqueue directly
            bodyTracker.EnqueueCapture(frameData.Capture.Reference());

            if (verboseLogging && frameCount % 100 == 0)
                Debug.Log($"[SkeletonTracker] Enqueued frame {frameCount}");

            Frame frame = null;
            try
            {
                frame = await Task.Run(() => bodyTracker.PopResult(TimeSpan.FromMilliseconds(timeoutMs)));
            }
            catch (TimeoutException)
            {
                if (verboseLogging)
                    Debug.Log($"[SkeletonTracker] PopResult timeout (frame {frameCount})");
                return;
            }

            if (frame != null)
            {
                using (frame)
                {
                    var skeletons = ExtractSkeletons(frame);
                    OnSkeletonsUpdated?.Invoke(skeletons);

                    var bodyIndexMap = frame.BodyIndexMap;
                    if (bodyIndexMap != null)
                        OnBodyIndexMapUpdated?.Invoke(bodyIndexMap.Reference());

                    latestFrame = frame.Reference();
                    latestBodyIndexMap = bodyIndexMap?.Reference();
                    latestSkeletons = skeletons;

                    OnPersonDataAvailable?.Invoke(latestFrame, latestBodyIndexMap, latestSkeletons);
                }
                errorCount = 0;
            }
        }
        catch (Exception e)
        {
            errorCount++;
            Debug.LogError($"[SkeletonTracker] Skeleton tracking error #{errorCount}: {e.Message}");
        }
    }

    List<SkeletonData> ExtractSkeletons(Frame frame)
    {
        var skeletons = new List<SkeletonData>();

        if (frame == null || frame.NumberOfBodies == 0)
            return skeletons;

        for (uint i = 0; i < frame.NumberOfBodies && i < maxBodies; i++)
        {
            Skeleton skeleton = frame.GetBodySkeleton(i);
            uint bodyId = frame.GetBodyId(i);

            var skeletonData = new SkeletonData
            {
                BodyId = bodyId,
                Joints = new Dictionary<JointId, JointData>()
            };

            for (int j = 0; j < (int)JointId.Count; j++)
            {
                // Explicitly use BodyTracking.Joint to avoid ambiguity
                Microsoft.Azure.Kinect.BodyTracking.Joint joint = skeleton.GetJoint(j);

                skeletonData.Joints[(JointId)j] = new JointData
                {
                    Position = new UnityEngine.Vector3(
                        joint.Position.X / 1000f,
                        -joint.Position.Y / 1000f,
                        joint.Position.Z / 1000f
                    ),
                    Orientation = new UnityEngine.Quaternion(
                        joint.Quaternion.X,
                        -joint.Quaternion.Y,
                        joint.Quaternion.Z,
                        joint.Quaternion.W
                    ),
                    Confidence = joint.ConfidenceLevel
                };
            }

            skeletons.Add(skeletonData);
        }

        return skeletons;
    }

    public void StopTracking()
    {
        isTracking = false;
    }

    public List<SkeletonData> GetLatestSkeletons()
    {
        if (latestSkeletons == null)
            return null;
        return new List<SkeletonData>(latestSkeletons);
    }


    private Image CreateImageFromBytes(ImageFormat format, int width, int height, int stride, byte[] data)
    {
        Image image = new Image(format, width, height, stride);
        data.CopyTo(image.Memory.Span);
        return image;
    }

    void OnDestroy()
    {
        isTracking = false;

        if (dataCapture != null)
        {
            dataCapture.OnFrameReceived -= ProcessFrameForTracking;
        }

        bodyTracker?.Dispose();

        Debug.Log($"[SkeletonTracker] Shutdown. Processed {frameCount} frames with {errorCount} errors");
    }
}

public class SkeletonData
{
    public uint BodyId;
    public Dictionary<JointId, JointData> Joints;
}

public class JointData
{
    public UnityEngine.Vector3 Position;
    public UnityEngine.Quaternion Orientation;
    public JointConfidenceLevel Confidence;
}
