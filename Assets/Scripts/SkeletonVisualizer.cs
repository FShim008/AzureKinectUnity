using UnityEngine;
using Microsoft.Azure.Kinect.BodyTracking;
using System.Collections.Generic;

public class SkeletonVisualizer : MonoBehaviour
{
    [SerializeField] private SkeletonTracker skeletonTracker;
    
    [Header("Visualization Settings")]
    [SerializeField] private bool showJoints = true;
    [SerializeField] private bool showBones = true;
    [SerializeField] private GameObject jointPrefab;
    [SerializeField] private Material boneMaterial;
    [SerializeField] private float jointSize = 0.05f;
    [SerializeField] private float boneWidth = 0.02f;

    private Dictionary<uint, SkeletonVisualData> activeSkeletons = new Dictionary<uint, SkeletonVisualData>();

    // Bone connections based on Azure Kinect body tracking (FIXED joint names)
    private static readonly (JointId, JointId)[] BoneConnections = new (JointId, JointId)[]
    {
        (JointId.Pelvis, JointId.SpineNavel),
        (JointId.SpineNavel, JointId.SpineChest),
        (JointId.SpineChest, JointId.Neck),
        (JointId.Neck, JointId.Head),

        (JointId.SpineChest, JointId.ClavicleLeft),
        (JointId.ClavicleLeft, JointId.ShoulderLeft),
        (JointId.ShoulderLeft, JointId.ElbowLeft),
        (JointId.ElbowLeft, JointId.WristLeft),
        (JointId.WristLeft, JointId.HandLeft),
        (JointId.HandLeft, JointId.HandTipLeft),
        (JointId.WristLeft, JointId.ThumbLeft),

        (JointId.SpineChest, JointId.ClavicleRight),
        (JointId.ClavicleRight, JointId.ShoulderRight),
        (JointId.ShoulderRight, JointId.ElbowRight),
        (JointId.ElbowRight, JointId.WristRight),
        (JointId.WristRight, JointId.HandRight),
        (JointId.HandRight, JointId.HandTipRight),
        (JointId.WristRight, JointId.ThumbRight),

        (JointId.Pelvis, JointId.HipLeft),
        (JointId.HipLeft, JointId.KneeLeft),
        (JointId.KneeLeft, JointId.AnkleLeft),
        (JointId.AnkleLeft, JointId.FootLeft),

        (JointId.Pelvis, JointId.HipRight),
        (JointId.HipRight, JointId.KneeRight),
        (JointId.KneeRight, JointId.AnkleRight),
        (JointId.AnkleRight, JointId.FootRight)
    };

    void Start()
    {
        if (skeletonTracker != null)
        {
            skeletonTracker.OnSkeletonsUpdated += VisualizeSkeleton;
        }
    }

    void VisualizeSkeleton(List<SkeletonData> skeletons)
    {
        HashSet<uint> currentBodyIds = new HashSet<uint>();

        foreach (var skeleton in skeletons)
        {
            currentBodyIds.Add(skeleton.BodyId);

            if (!activeSkeletons.ContainsKey(skeleton.BodyId))
            {
                CreateSkeletonVisual(skeleton.BodyId);
            }

            UpdateSkeletonVisual(skeleton);
        }

        // Remove skeletons that are no longer tracked
        List<uint> toRemove = new List<uint>();
        foreach (var kvp in activeSkeletons)
        {
            if (!currentBodyIds.Contains(kvp.Key))
            {
                toRemove.Add(kvp.Key);
            }
        }

        foreach (var bodyId in toRemove)
        {
            RemoveSkeletonVisual(bodyId);
        }
    }

    void CreateSkeletonVisual(uint bodyId)
    {
        var visualData = new SkeletonVisualData
        {
            RootObject = new GameObject($"Skeleton_{bodyId}"),
            JointObjects = new Dictionary<JointId, GameObject>(),
            BoneRenderers = new Dictionary<(JointId, JointId), LineRenderer>()
        };

        visualData.RootObject.transform.SetParent(transform);

        // Create joint objects
        if (showJoints)
        {
            for (int i = 0; i < (int)JointId.Count; i++)
            {
                JointId jointId = (JointId)i;
                GameObject joint = jointPrefab != null ?
                    Instantiate(jointPrefab, visualData.RootObject.transform) :
                    GameObject.CreatePrimitive(PrimitiveType.Sphere);

                joint.name = jointId.ToString();
                joint.transform.localScale = UnityEngine.Vector3.one * jointSize;
                joint.transform.SetParent(visualData.RootObject.transform);

                visualData.JointObjects[jointId] = joint;
            }
        }

        // Create bone renderers
        if (showBones)
        {
            foreach (var bone in BoneConnections)
            {
                GameObject boneObj = new GameObject($"Bone_{bone.Item1}_to_{bone.Item2}");
                boneObj.transform.SetParent(visualData.RootObject.transform);

                LineRenderer lineRenderer = boneObj.AddComponent<LineRenderer>();
                lineRenderer.startWidth = boneWidth;
                lineRenderer.endWidth = boneWidth;
                lineRenderer.positionCount = 2;

                if (boneMaterial != null)
                    lineRenderer.material = boneMaterial;
                else
                {
                    lineRenderer.material = new Material(Shader.Find("Sprites/Default"));
                    lineRenderer.startColor = Color.green;
                    lineRenderer.endColor = Color.green;
                }

                visualData.BoneRenderers[bone] = lineRenderer;
            }
        }

        activeSkeletons[bodyId] = visualData;
    }

    void UpdateSkeletonVisual(SkeletonData skeleton)
    {
        if (!activeSkeletons.ContainsKey(skeleton.BodyId))
            return;

        var visualData = activeSkeletons[skeleton.BodyId];

        // Update joint positions
        if (showJoints)
        {
            foreach (var joint in skeleton.Joints)
            {
                if (visualData.JointObjects.ContainsKey(joint.Key))
                {
                    visualData.JointObjects[joint.Key].transform.position = joint.Value.Position;
                    visualData.JointObjects[joint.Key].transform.rotation = joint.Value.Orientation;

                    // Color code by confidence
                    var renderer = visualData.JointObjects[joint.Key].GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        switch (joint.Value.Confidence)
                        {
                            case JointConfidenceLevel.High:
                                renderer.material.color = Color.green;
                                break;
                            case JointConfidenceLevel.Medium:
                                renderer.material.color = Color.yellow;
                                break;
                            case JointConfidenceLevel.Low:
                                renderer.material.color = Color.red;
                                break;
                            default:
                                renderer.material.color = Color.gray;
                                break;
                        }
                    }
                }
            }
        }

        // Update bone connections
        if (showBones)
        {
            foreach (var bone in BoneConnections)
            {
                if (visualData.BoneRenderers.ContainsKey(bone) &&
                    skeleton.Joints.ContainsKey(bone.Item1) &&
                    skeleton.Joints.ContainsKey(bone.Item2))
                {
                    var lineRenderer = visualData.BoneRenderers[bone];
                    lineRenderer.SetPosition(0, skeleton.Joints[bone.Item1].Position);
                    lineRenderer.SetPosition(1, skeleton.Joints[bone.Item2].Position);
                }
            }
        }
    }

    void RemoveSkeletonVisual(uint bodyId)
    {
        if (activeSkeletons.ContainsKey(bodyId))
        {
            Destroy(activeSkeletons[bodyId].RootObject);
            activeSkeletons.Remove(bodyId);
        }
    }

    void OnDestroy()
    {
        if (skeletonTracker != null)
        {
            skeletonTracker.OnSkeletonsUpdated -= VisualizeSkeleton;
        }
        foreach (var kvp in activeSkeletons)
        {
            if (kvp.Value.RootObject != null)
                Destroy(kvp.Value.RootObject);
        }
        activeSkeletons.Clear();
    }
}

public class SkeletonVisualData
{
    public GameObject RootObject;
    public Dictionary<JointId, GameObject> JointObjects;
    public Dictionary<(JointId, JointId), LineRenderer> BoneRenderers;
}