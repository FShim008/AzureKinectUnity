using UnityEngine;
using System;

public class PointCloudGenerator : MonoBehaviour, IPointCloudSource
{
    [SerializeField] private KinectDataCapture dataCapture;

    [Header("Point Cloud Settings")]
    [SerializeField] private float minDepth = 0.3f;
    [SerializeField] private float maxDepth = 3.5f;
    [SerializeField] private int skipPixels = 4;

    [Header("Performance")]
    [SerializeField] private bool throttleProcessing = true;
    [SerializeField] private int processEveryNthFrame = 1;

    private int frameCounter = 0;

    public event Action<PointCloudData> OnPointCloudGenerated;

    void Start()
    {
        if (dataCapture != null)
        {
            dataCapture.OnFrameReceived += ProcessFrame;
        }
    }

    void ProcessFrame(KinectFrameData frameData)
    {
        frameCounter++;

        if (throttleProcessing && frameCounter % processEveryNthFrame != 0)
            return;

        var pointCloud = KinectPointCloudUtility.GeneratePointCloud(
            frameData,
            dataCapture.GetCalibration(),
            minDepth,
            maxDepth,
            skipPixels
        );

        OnPointCloudGenerated?.Invoke(pointCloud);
    }

    void OnDestroy()
    {
        if (dataCapture != null)
        {
            dataCapture.OnFrameReceived -= ProcessFrame;
        }
    }
}