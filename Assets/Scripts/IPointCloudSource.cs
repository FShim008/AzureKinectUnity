using System;
using System.Collections.Generic;
using UnityEngine;

public interface IPointCloudSource
{
    /// <summary>
    /// Event triggered when a new point cloud is generated.
    /// </summary>
    event Action<PointCloudData> OnPointCloudGenerated;
}

public class PointCloudData
{
    public List<UnityEngine.Vector3> Points = new List<UnityEngine.Vector3>();
    public List<Color32> Colors = new List<Color32>();
}