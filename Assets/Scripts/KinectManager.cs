using UnityEngine;
using System.Collections.Generic;
using K4AdotNet.Sensor;

public class KinectManager : MonoBehaviour
{
    [Tooltip("The prefab containing KinectDevice, SkeletonTracker, and SkeletonRenderer components.")]
    public GameObject kinecPipelinePrefab;

    private List<GameObject> _kinectInstances = new List<GameObject>();

    void Start()
    {
        InitializeKinects();
    }

    private void InitializeKinects()
    {
        if (kinecPipelinePrefab == null)
        {
            Debug.LogError("Kinect Pipeline Prefab is not assigned.");
            return;
        }

        int deviceCount = Device.InstalledCount;
        Debug.Log($"Found {deviceCount} connected Azure Kinect devices.");

        if (deviceCount == 0)
        {
            Debug.LogError("No Azure Kinect devices found. Cannot initialize pipelines.");
            return;
        }
        
        for (int i = 0; i < deviceCount; i++)
        {
            GameObject pipelineInstance = Instantiate(kinecPipelinePrefab, transform);
            pipelineInstance.name = $"Kinect_Device_{i}";

            KinectDevice device = pipelineInstance.GetComponent<KinectDevice>();
            if (device != null)
                device.DeviceIndex = i;
            else
            {
                Debug.LogError($"Pipeline Prefab is missing {nameof(KinectDevice)} component. Cannot initialize pipeline.");
                Destroy(pipelineInstance);
                return;
            }

            SkeletonTracker tracker = pipelineInstance.GetComponent<SkeletonTracker>();
            SkeletonRenderer renderer = pipelineInstance.GetComponent<SkeletonRenderer>();
            if (tracker == null || renderer == null)
            {
                Debug.LogError($"Pipeline Prefab is missing required {nameof(SkeletonTracker)} or {nameof(SkeletonRenderer)} components. Cannot initialize pipeline.");
                Destroy(pipelineInstance);
                return;
            }
            
            tracker.OnSkeletonsProcessed += renderer.SkeletonTracker_SkeletonUpdated;
            Debug.Log($"Initialized pipeline for Device Index {i}. Events linked successfully.");
            _kinectInstances.Add(pipelineInstance);
        }
    }

    private void OnDestroy()
    {
        foreach (var instance in _kinectInstances)
        {
            if (instance != null)
            {
                SkeletonTracker tracker = instance.GetComponent<SkeletonTracker>();
                SkeletonRenderer renderer = instance.GetComponent<SkeletonRenderer>();
                if (tracker != null && renderer != null)
                    tracker.OnSkeletonsProcessed -= renderer.SkeletonTracker_SkeletonUpdated;
            }
        }
    }
}
