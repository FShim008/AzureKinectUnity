using K4AdotNet.BodyTracking;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class SkeletonRenderer : MonoBehaviour
{
    private void Awake()
    {
        _root = new GameObject();
        _root.name = "skeleton:root";
        _root.transform.parent = transform;
        _root.transform.localScale = Vector3.one;
        _root.transform.localPosition = Vector3.zero;
        _root.SetActive(false);
        CreateJoints();
        CreateBones();
        CreateHead();
    }

    #region Render objects

    private GameObject _root;
    private IReadOnlyDictionary<JointType, Transform> _joints;
    private IReadOnlyCollection<Bone> _bones;
    private Transform _head;

    private class Bone
    {
        private const float BONE_THICKNESS = 0.04f;

        public Bone(JointType parentJoint, JointType childJoint)
        {
            ParentJoint = parentJoint;
            ChildJoint = childJoint;
            var pos = new GameObject();
            pos.name = $"{parentJoint}->{childJoint}:pos";
            var bone = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            bone.name = $"{parentJoint}->{childJoint}:bone";
            bone.transform.parent = pos.transform;
            bone.transform.localScale = new Vector3(BONE_THICKNESS, 1.0f, BONE_THICKNESS);
            bone.transform.localPosition = 1.0f * Vector3.up;
            Transform = pos.transform;
        }
        public JointType ParentJoint { get; }
        public JointType ChildJoint { get; }
        public Transform Transform { get; }
    }

    private void CreateJoints()
    {
        _joints = JointTypes.All
            .ToDictionary(
                jt => jt,
                jt =>
                {
                    var joint = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    joint.name = jt.ToString();
                    joint.transform.parent = _root.transform;
                    joint.transform.localScale = 0.075f * Vector3.one;
                    return joint.transform;
                });
        SetJointColor(Color.green, typeof(JointType).GetEnumValues().Cast<JointType>().ToArray());
        SetJointScale(0.05f, JointType.Neck, JointType.Head, JointType.ClavicleLeft, JointType.ClavicleRight, JointType.EarLeft, JointType.EarRight);
        SetJointScale(0.033f, JointType.EyeLeft, JointType.EyeRight, JointType.Nose);
        SetJointColor(Color.cyan, JointType.EyeLeft, JointType.EyeRight);
        SetJointColor(Color.magenta, JointType.Nose);
        SetJointColor(Color.yellow, JointType.EarLeft, JointType.EarRight);
    }

    private void SetJointScale(float scale, params JointType[] jointTypes)
    {
        foreach (var jt in jointTypes)
            _joints[jt].localScale = scale * Vector3.one;
    }

    private void SetJointColor(Color color, params JointType[] jointTypes)
    {
        foreach (var jt in jointTypes)
            _joints[jt].GetComponent<Renderer>().material.color = color;
    }

    private void CreateBones()
    {
        _bones = BoneMapping.Bones
            .Select(pair => new Bone(pair.Item1, pair.Item2))
            .ToList();
        foreach (var b in _bones)
            b.Transform.parent = _root.transform;
    }

    private void CreateHead()
    {
        var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        head.GetComponent<Renderer>().material.color = new Color(0.8f, 0.8f, 0.8f);
        head.transform.parent = _root.transform;
        _head = head.transform;
    }

    #endregion

    private void OnEnable()
    {
        //Debug.Log("SkeletonRenderer is enabled");
        var skeletonTracker = this.transform.GetComponentInParent<SkeletonTracker>();
        if (skeletonTracker != null)
        {
            skeletonTracker.OnSkeletonsProcessed += SkeletonTracker_SkeletonUpdated;
            //Debug.Log("Updated the updated event in Renderer, enable");
        }
        else
            Debug.LogError("Failed to find the Skeleton Tracker in the parents");
    }

    private void OnDisable()
    {
        //Debug.Log("SkeletonRenderer is disabled");
        var skeletonTracker = this.transform.GetComponentInParent<SkeletonTracker>();
        if (skeletonTracker != null)
        {
            skeletonTracker.OnSkeletonsProcessed -= SkeletonTracker_SkeletonUpdated;
            //Debug.Log("Updated the updated event in Renderer, disable");
        }
    }

    public void SkeletonTracker_SkeletonUpdated(object sender, SkeletonEventArgs e)
    {
        if (e.Skeleton == null)
            HideSkeleton();
        else
        {
            //Debug.Log("Skeleton rendering for device: " + e.DeviceSerialNumber);
            RenderSkeleton(e.Skeleton.Value);
        }
    }

    private void RenderSkeleton(SkeletonData skeletonData)
    {
        if (skeletonData.Skeletons.Length == 0)
        {
            HideSkeleton();
            return;
        }

        var skeleton = skeletonData.Skeletons[0];

        for (int i = 0; i < 32; i++)
        {
            JointType jt = (JointType)i;
            JointData jointData = skeleton.Joints[i];

            if (_joints.TryGetValue(jt, out Transform jointTransform))
                jointTransform.localPosition = jointData.Position;
        }

        foreach (var bone in _bones)
            PositionBone(bone, skeleton);

        PositionHead(skeleton);
        _root.SetActive(true);
    }

    private static void PositionBone(Bone bone, Skeleton skeleton)
    {
        var parentJointData = skeleton.Joints[(int)bone.ParentJoint];
        var childJointData = skeleton.Joints[(int)bone.ChildJoint];
        var parentPos = parentJointData.Position;
        var direction = childJointData.Position - parentPos;
        bone.Transform.localPosition = parentPos;
        bone.Transform.localScale = new Vector3(1f, direction.magnitude * 0.5f, 1f);
        bone.Transform.localRotation = Quaternion.FromToRotation(Vector3.up, direction);
    }

    private void PositionHead(Skeleton skeleton)
    {
        var headPos = skeleton.Joints[(int)JointType.Head].Position;
        var earPosR = skeleton.Joints[(int)JointType.EarRight].Position;
        var earPosL = skeleton.Joints[(int)JointType.EarLeft].Position;
        var headCenter = 0.5f * (earPosR + earPosL);
        var d = (earPosR - earPosL).magnitude;
        _head.localPosition = headCenter;
        _head.localRotation = Quaternion.FromToRotation(Vector3.up, headCenter - headPos);
        _head.localScale = new Vector3(d, 2 * (headCenter - headPos).magnitude, d);
    }

    private void HideSkeleton()
    {
        _root.SetActive(false);
    }
}