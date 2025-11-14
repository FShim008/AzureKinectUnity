using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;
using System.Collections.Generic;

public class KinectResourceManager : MonoBehaviour
{
    private static KinectResourceManager instance;
    private Queue<System.IDisposable> pendingDisposals = new Queue<System.IDisposable>();
    private readonly object disposalLock = new object();

    void OnEnable()
    {
        if (instance == null)
        {
            instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(this);
        }
    }

    public static void ScheduleDisposal(System.IDisposable resource)
    {
        if (instance == null || resource == null) return;

        lock (instance.disposalLock)
        {
            instance.pendingDisposals.Enqueue(resource);
        }
    }

    void LateUpdate()
    {
        // Dispose resources on main thread
        lock (disposalLock)
        {
            while (pendingDisposals.Count > 0)
            {
                var resource = pendingDisposals.Dequeue();
                try
                {
                    resource?.Dispose();
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"Error disposing resource: {e.Message}");
                }
            }
        }
    }
}