using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using K4AdotNet.BodyTracking;
using System;
using UnityEngine.InputSystem;
using System.IO;

public class DualCameraCalibrator : MonoBehaviour
{
    [Header("Calibration Targets")]
    public SkeletonTracker targetTracker; // Device Index T (Target)
    public SkeletonTracker sourceTracker; // Device Index S (Source)

    [Header("Calibration Parameters")]
    public int frameCollectionCount = 100;

    public JointType[] calibrationJoints = new JointType[] {
        JointType.SpineNavel, JointType.SpineChest,
        JointType.ShoulderLeft, JointType.ElbowLeft,
        JointType.ShoulderRight, JointType.ElbowRight,
        JointType.HipLeft, JointType.KneeLeft,
        JointType.HipRight, JointType.KneeRight
    };
    public JointConfidenceLevel minConfidence = JointConfidenceLevel.Medium;

    [Header("Control")]
    public Key startCalibrationKey = Key.C;

    private List<Vector3> _sourcePoints = new List<Vector3>();
    private List<Vector3> _targetPoints = new List<Vector3>();
    private int _framesCollected = 0;
    private bool _isCollecting = false;

    private KinectDevice _targetDevice;
    private KinectDevice _sourceDevice;
    private int _targetDeviceIndex;
    private int _sourceDeviceIndex;

    private void Start()
    {
        if (targetTracker == null || sourceTracker == null)
        {
            Debug.LogError("Calibration targets are not set. Drag the two SkeletonTracker components here.");
            enabled = false;
            return;
        }

        _targetDevice = targetTracker.GetComponent<KinectDevice>();
        _sourceDevice = sourceTracker.GetComponent<KinectDevice>();
        if (_targetDevice == null || _sourceDevice == null)
        {
            Debug.LogError("Calibration targets must have a KinectDevice component attached to their GameObject.");
            enabled = false;
            return;
        }

        _targetDeviceIndex = _targetDevice.DeviceIndex;
        _sourceDeviceIndex = _sourceDevice.DeviceIndex;
        CalibrationUtility.RegisterRequiredCalibration(
            _sourceDeviceIndex + 1, // Source Cam Num
            _targetDeviceIndex + 1  // Target Cam Num
        );

        targetTracker.OnSkeletonsProcessed += OnTargetSkeletonsProcessed;
        sourceTracker.OnSkeletonsProcessed += OnSourceSkeletonsProcessed;
        Debug.Log($"Calibrator ready. Press '{startCalibrationKey.ToString()}' to start data collection for Camera {_sourceDeviceIndex + 1} to Camera {_targetDeviceIndex + 1}.");
    }

    private void OnDestroy()
    {
        if (targetTracker != null) 
            targetTracker.OnSkeletonsProcessed -= OnTargetSkeletonsProcessed;
        if (sourceTracker != null) 
            sourceTracker.OnSkeletonsProcessed -= OnSourceSkeletonsProcessed;
    }

    private void Update()
    {
        if (Keyboard.current[startCalibrationKey].wasPressedThisFrame && !_isCollecting)
            StartCollection();
    }

    private void StartCollection()
    {
        _sourcePoints.Clear();
        _targetPoints.Clear();
        _framesCollected = 0;
        _isCollecting = true;
        Debug.Log("--- Starting Calibration Data Collection ---");
    }

    private SkeletonData? _latestTargetData;
    private SkeletonData? _latestSourceData;

    private void OnTargetSkeletonsProcessed(object sender, SkeletonEventArgs e)
    {
        _latestTargetData = e.Skeleton;
        TryCollectData();
    }

    private void OnSourceSkeletonsProcessed(object sender, SkeletonEventArgs e)
    {
        _latestSourceData = e.Skeleton;
        TryCollectData();
    }

    private void TryCollectData()
    {
        if (!_isCollecting || _framesCollected >= frameCollectionCount) 
            return;
        if (!_latestTargetData.HasValue || !_latestSourceData.HasValue) 
            return;

        Skeleton targetSkeleton = _latestTargetData.Value.Skeletons.FirstOrDefault();
        Skeleton sourceSkeleton = _latestSourceData.Value.Skeletons.FirstOrDefault();

        if (targetSkeleton.Joints == null || sourceSkeleton.Joints == null) 
            return;

        List<Vector3> frameSourcePoints = new List<Vector3>();
        List<Vector3> frameTargetPoints = new List<Vector3>();

        foreach (var jointType in calibrationJoints)
        {
            int index = (int)jointType;
            if (targetSkeleton.Joints.Length > index && sourceSkeleton.Joints.Length > index)
            {
                var targetJoint = targetSkeleton.Joints[index];
                var sourceJoint = sourceSkeleton.Joints[index];
                if (targetJoint.ConfidenceLevel >= minConfidence && sourceJoint.ConfidenceLevel >= minConfidence)
                {
                    frameTargetPoints.Add(targetJoint.Position);
                    frameSourcePoints.Add(sourceJoint.Position);
                }
            }
        }

        if (frameSourcePoints.Count >= 3)
        {
            _sourcePoints.AddRange(frameSourcePoints);
            _targetPoints.AddRange(frameTargetPoints);
            _framesCollected++;
        }

        if (_framesCollected >= frameCollectionCount)
        {
            PerformCalibration();
            _isCollecting = false;
        }

        _latestTargetData = null;
        _latestSourceData = null;
    }

    private void PerformCalibration()
    {
        Debug.Log("--- Calibration Started ---");
        Matrix4x4 calibrationMatrix = CalibrationUtility.ComputeTransformationMatrix(_sourcePoints, _targetPoints);
        if (calibrationMatrix != Matrix4x4.identity)
        {
            int sourceNum = _sourceDeviceIndex + 1;
            int targetNum = _targetDeviceIndex + 1;
            CalibrationUtility.MarkCalibrationComplete(sourceNum, targetNum, calibrationMatrix);
        }
        Debug.Log("--- Calibration Finished ---");
        //Debug.LogWarning("Calibration saved. Transitive check pending for all active calibrators.");
    }
}