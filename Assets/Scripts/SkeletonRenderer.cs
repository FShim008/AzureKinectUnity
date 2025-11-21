using K4AdotNet.BodyTracking;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering.Universal;

public class SkeletonRenderer : MonoBehaviour
{
    [SerializeField] private SkeletonTracker _tracker;

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

    private void Start()
    {
        _tracker = GetComponent<SkeletonTracker>();
        if (_tracker == null)
        {
            Debug.LogError($"[{gameObject.name}] Missing required {nameof(SkeletonTracker)} component. Cannot render skeleton.");
            return;
        }
        _tracker.OnSkeletonsProcessed += SkeletonTracker_SkeletonUpdated;
        Debug.Log($"[{gameObject.name}] Renderer successfully subscribed to Tracker events.");
    }

    private void OnDestroy()
    {
        if (_tracker != null)
            _tracker.OnSkeletonsProcessed -= SkeletonTracker_SkeletonUpdated;
    }

    #region Render objects

    private struct JointVisual
    {
        public Transform Transform;
        public Renderer Renderer;
    }
    [Header("Joint Confidence Colors")]
    public Color TrackedHighColor = Color.green;
    public Color TrackedMediumColor = Color.yellow;
    public Color TrackedLowColor = Color.red;
    public Color UntrackedColor = Color.gray;

    private IReadOnlyDictionary<JointType, JointVisual> _joints;
    private static IEnumerable<JointType> AllJointTypes => typeof(JointType).GetEnumValues().Cast<JointType>();

    private GameObject _root;
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
        var jointsDict = AllJointTypes
        .ToDictionary(
            jt => jt,
            jt =>
            {
                var joint = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(joint.GetComponent<Collider>()); // Remove physics collider

                joint.name = jt.ToString();
                joint.transform.parent = _root.transform;

                // Initial scale is temporary, but we set it here.
                joint.transform.localScale = 0.075f * Vector3.one;

                // NEW: Store both the Transform and the Renderer
                return new JointVisual
                {
                    Transform = joint.transform,
                    Renderer = joint.GetComponent<Renderer>()
                };
            });
        _joints = jointsDict;
        SetJointColor(TrackedHighColor, AllJointTypes.ToArray());
        SetJointScale(0.05f, JointType.Neck, JointType.Head, JointType.ClavicleLeft, JointType.ClavicleRight, JointType.EarLeft, JointType.EarRight);
        SetJointScale(0.033f, JointType.EyeLeft, JointType.EyeRight, JointType.Nose);
        SetJointColor(Color.cyan, JointType.EyeLeft, JointType.EyeRight);
        SetJointColor(Color.magenta, JointType.Nose);
        SetJointColor(Color.yellow, JointType.EarLeft, JointType.EarRight);
    }

    private void SetJointScale(float scale, params JointType[] jointTypes)
    {
        foreach (var jt in jointTypes)
        {
            if (_joints.TryGetValue(jt, out JointVisual visual))
                _joints[jt].Transform.localScale = scale * Vector3.one;
        }
    }

    private void SetJointColor(Color color, params JointType[] jointTypes)
    {
        foreach (var jt in jointTypes)
        {
            if (_joints.TryGetValue(jt, out JointVisual visual))
            {
                if(visual.Renderer != null)
                    visual.Renderer.material.color = color;
            }
        }
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
        _root.transform.localPosition = skeleton.Position;
        for (int i = 0; i < 32; i++)
        {
            JointType jt = (JointType)i;
            JointData jointData = skeleton.Joints[i];
            if (_joints.TryGetValue(jt, out JointVisual jointVisual))
            {
                jointVisual.Transform.localPosition = jointData.Position - skeleton.Position;
                Color jointColor = UntrackedColor;
                switch (jointData.ConfidenceLevel)
                {
                    case JointConfidenceLevel.High:
                        jointColor = TrackedHighColor;
                        break;
                    case JointConfidenceLevel.Medium:
                        jointColor = TrackedMediumColor;
                        break;
                    case JointConfidenceLevel.Low:
                        jointColor = TrackedLowColor;
                        break;
                    case JointConfidenceLevel.None:
                    default:
                        break;
                }
                if (jointVisual.Renderer != null)
                    jointVisual.Renderer.material.color = jointColor;
            }
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
        bone.Transform.localPosition = parentPos - skeleton.Position;
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
        _head.localPosition = headCenter - skeleton.Position;
        _head.localRotation = Quaternion.FromToRotation(Vector3.up, headCenter - headPos);
        _head.localScale = new Vector3(d, 2 * (headCenter - headPos).magnitude, d);
    }

    private void HideSkeleton()
    {
        _root.SetActive(false);
    }
}