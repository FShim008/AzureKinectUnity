using UnityEngine;
using System;
using System.Collections.Generic;

public class PersonPointCloud : MonoBehaviour, IPointCloudSource
{
    [SerializeField] private SkeletonTracker skeletonTracker;
    [SerializeField] private KinectDataCapture dataCapture;

    [Header("Point Cloud Settings")]
    [SerializeField] private float minDepth = 0.3f;
    [SerializeField] private float maxDepth = 3.5f;
    [SerializeField] private int skipPixels = 2;

    [Header("Performance")]
    [SerializeField] private bool throttleProcessing = true;
    [SerializeField] private int processEveryNthFrame = 1;

    private byte[] latestBodyIndexData = null;
    private int frameCounter = 0;

    public event Action<PointCloudData> OnPointCloudGenerated;

    void Start()
    {
        if (dataCapture != null && skeletonTracker != null)
        {
            dataCapture.OnFrameReceived += ProcessFrame;
            skeletonTracker.OnBodyIndexMapUpdated += OnBodyIndexMapUpdated;
        }
    }

    void OnBodyIndexMapUpdated(Microsoft.Azure.Kinect.Sensor.Image bodyIndexImage)
    {
        if (bodyIndexImage == null) return;
        latestBodyIndexData = bodyIndexImage.Memory.ToArray();
    }

    void ProcessFrame(KinectFrameData frameData)
    {
        frameCounter++;
        if (throttleProcessing && frameCounter % processEveryNthFrame != 0)
            return;

        if (latestBodyIndexData == null)
            return;

        var skeletons = skeletonTracker.GetLatestSkeletons();
        if (skeletons == null || skeletons.Count == 0)
            return;

        HashSet<byte> trackedIndices = new HashSet<byte>();
        for (byte i = 0; i < skeletons.Count; i++)
            trackedIndices.Add(i);

        // Use utility with pixel filter
        var pointCloud = KinectPointCloudUtility.GeneratePointCloud(
            frameData,
            dataCapture.GetCalibration(),
            minDepth,
            maxDepth,
            skipPixels,
            (x, y) => {
                int idx = y * frameData.DepthWidth + x;
                return idx < latestBodyIndexData.Length &&
                       trackedIndices.Contains(latestBodyIndexData[idx]);
            }
        );

        OnPointCloudGenerated?.Invoke(pointCloud);
    }

    void OnDestroy()
    {
        if (dataCapture != null)
            dataCapture.OnFrameReceived -= ProcessFrame;
        if (skeletonTracker != null)
            skeletonTracker.OnBodyIndexMapUpdated -= OnBodyIndexMapUpdated;
    }
}