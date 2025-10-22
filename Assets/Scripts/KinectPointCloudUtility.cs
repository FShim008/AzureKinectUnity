using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;
using System;
using System.Collections.Generic;

public static class KinectPointCloudUtility
{
    // Pool of reusable PointCloudData objects
    private static Queue<PointCloudData> pointCloudPool = new Queue<PointCloudData>();
    private static readonly object poolLock = new object();

    public static PointCloudData GetPooledPointCloud()
    {
        lock (poolLock)
        {
            if (pointCloudPool.Count > 0)
            {
                var pc = pointCloudPool.Dequeue();
                pc.Points.Clear();
                pc.Colors.Clear();
                return pc;
            }
        }
        return new PointCloudData();
    }

    public static void ReturnToPool(PointCloudData pointCloud)
    {
        if (pointCloud == null) return;

        lock (poolLock)
        {
            if (pointCloudPool.Count < 5) // Keep max 5 in pool
            {
                pointCloud.Points.Clear();
                pointCloud.Colors.Clear();
                pointCloudPool.Enqueue(pointCloud);
            }
        }
    }

    public static PointCloudData GeneratePointCloud(
        KinectFrameData frameData,
        Calibration calibration,
        float minDepth,
        float maxDepth,
        int skipPixels,
        Func<int, int, bool> pixelFilter = null)
    {
        var pointCloud = GetPooledPointCloud();

        int depthWidth = frameData.DepthWidth;
        int depthHeight = frameData.DepthHeight;

        // Direct access to depth buffer (no intermediate copy)
        byte[] depthBytes = frameData.DepthData;

        int estimatedPoints = (depthWidth / skipPixels) * (depthHeight / skipPixels);
        if (pointCloud.Points.Capacity < estimatedPoints)
        {
            pointCloud.Points.Capacity = estimatedPoints;
            pointCloud.Colors.Capacity = estimatedPoints;
        }

        // Process without creating Image objects (they're expensive)
        for (int y = 0; y < depthHeight; y += skipPixels)
        {
            for (int x = 0; x < depthWidth; x += skipPixels)
            {
                // Apply optional pixel filter
                if (pixelFilter != null && !pixelFilter(x, y))
                    continue;

                int idx = y * depthWidth + x;
                int depthIdx = idx * sizeof(ushort);

                if (depthIdx + 1 >= depthBytes.Length)
                    continue;

                ushort depth = BitConverter.ToUInt16(depthBytes, depthIdx);
                float depthMeters = depth / 1000f;

                if (depthMeters < minDepth || depthMeters > maxDepth || depth == 0)
                    continue;

                // Convert to 3D using calibration
                var depthPoint = new System.Numerics.Vector2(x, y);
                var point3D = calibration.TransformTo3D(
                    depthPoint,
                    depth,
                    CalibrationDeviceType.Depth,
                    CalibrationDeviceType.Depth);

                if (!point3D.HasValue)
                    continue;

                UnityEngine.Vector3 position = new UnityEngine.Vector3(
                    point3D.Value.X / 1000f,
                    -point3D.Value.Y / 1000f,
                    point3D.Value.Z / 1000f
                );

                // Get color
                Color32 color = GetColorForPoint(calibration, point3D.Value, frameData);

                pointCloud.Points.Add(position);
                pointCloud.Colors.Add(color);
            }
        }

        return pointCloud;
    }

    private static Color32 GetColorForPoint(
        Calibration calibration,
        System.Numerics.Vector3 point3D,
        KinectFrameData frameData)
    {
        var colorPoint = calibration.TransformTo2D(
            point3D,
            CalibrationDeviceType.Depth,
            CalibrationDeviceType.Color);

        if (!colorPoint.HasValue)
            return Color.white;

        int cx = Mathf.Clamp(Mathf.RoundToInt(colorPoint.Value.X), 0, frameData.ColorWidth - 1);
        int cy = Mathf.Clamp(Mathf.RoundToInt(colorPoint.Value.Y), 0, frameData.ColorHeight - 1);

        int colorIdx = (cy * frameData.ColorWidth + cx) * 4;

        // Direct access without bounds check (already clamped)
        return new Color32(
            frameData.ColorData[colorIdx + 2],
            frameData.ColorData[colorIdx + 1],
            frameData.ColorData[colorIdx + 0],
            255
        );
    }
}