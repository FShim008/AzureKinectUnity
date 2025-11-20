using K4AdotNet.BodyTracking;
using UnityEngine;

public struct JointData
{
    public Vector3 Position;
    public Quaternion Orientation;
    public JointConfidenceLevel ConfidenceLevel;
}

public struct Skeleton
{
    public uint BodyId;
    public JointData[] Joints;
    public Vector3 Position;
    public Quaternion Orientation;
}

public struct SkeletonData
{
    public Skeleton[] Skeletons;
    public long Timestamp;
}