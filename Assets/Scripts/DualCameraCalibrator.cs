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

    [Header("Filtering Thresholds")]
    [Tooltip("The ID of the joint to use for movement tracking and filtering.")]
    public JointType TrackingJoint = JointType.SpineNavel;
    [Tooltip("Minimum time (in milliseconds) required between adding two successful samples.")]
    public int TemporalThresholdMs = 100;
    [Tooltip("Minimum distance (in meters) the tracking joint must move between samples.")]
    public float MovementThresholdM = 0.1f;

    [Header("Start Calibration Key")]
    public Key startCalibrationKey = Key.C;

    private List<Vector3> _sourcePoints = new List<Vector3>();
    private List<Vector3> _targetPoints = new List<Vector3>();
    private int _framesCollected = 0;
    private bool _isCollecting = false;

    private KinectDevice _targetDevice;
    private KinectDevice _sourceDevice;
    private int _targetDeviceIndex;
    private int _sourceDeviceIndex;

    private long _lastSampleTimestamp = 0;
    private Vector3 _lastSampleTrackingPointS = Vector3.zero;
    private Vector3 _lastSampleTrackingPointT = Vector3.zero;

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
            _sourceDeviceIndex + 1,
            _targetDeviceIndex + 1
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
        if (Keyboard.current != null && Keyboard.current[startCalibrationKey].wasPressedThisFrame && !_isCollecting)
            StartCollection();
    }

    private void StartCollection()
    {
        _sourcePoints.Clear();
        _targetPoints.Clear();
        _framesCollected = 0;
        _isCollecting = true;
        _lastSampleTimestamp = 0;
        _lastSampleTrackingPointS = Vector3.zero;
        _lastSampleTrackingPointT = Vector3.zero;
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
        if (!TryGetTrackingJoints(targetSkeleton, sourceSkeleton, out Vector3 currentPointT, out Vector3 currentPointS))
            return;
        long currentTimestamp = _latestSourceData.Value.Timestamp;
        if (!PassesFilters(currentPointS, currentPointT, currentTimestamp))
        {
            _latestTargetData = null;
            _latestSourceData = null;
            return;
        }
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
            Debug.Log($"Sample ADDED. Total Frames Collected: {_framesCollected}/{frameCollectionCount}");
            _lastSampleTimestamp = currentTimestamp;
            _lastSampleTrackingPointS = currentPointS;
            _lastSampleTrackingPointT = currentPointT;
        }
        else
            Debug.LogWarning($"Frame skipped: Only found {frameSourcePoints.Count} joints with required confidence.");

        if (_framesCollected >= frameCollectionCount)
        {
            PerformCalibration();
            _isCollecting = false;
        }
        _latestTargetData = null;
        _latestSourceData = null;
    }

    private bool TryGetTrackingJoints(Skeleton targetSkeleton, Skeleton sourceSkeleton, out Vector3 pointT, out Vector3 pointS)
    {
        pointT = Vector3.zero;
        pointS = Vector3.zero;
        int index = (int)TrackingJoint;
        if (targetSkeleton.Joints.Length > index && sourceSkeleton.Joints.Length > index)
        {
            var targetJoint = targetSkeleton.Joints[index];
            var sourceJoint = sourceSkeleton.Joints[index];
            if (targetJoint.ConfidenceLevel >= minConfidence && sourceJoint.ConfidenceLevel >= minConfidence)
            {
                pointT = targetJoint.Position;
                pointS = sourceJoint.Position;
                return true;
            }
        }
        return false;
    }

    private bool PassesFilters(Vector3 currentPointS, Vector3 currentPointT, long currentTimestamp)
    {
        // Temporal Threshold Check
        if (_lastSampleTimestamp != 0 && (currentTimestamp - _lastSampleTimestamp) < TemporalThresholdMs)
            return false;
        // Movement Threshold Check
        if (_lastSampleTrackingPointS != Vector3.zero || _lastSampleTrackingPointT != Vector3.zero)
        {
            float movementS = Vector3.Distance(_lastSampleTrackingPointS, currentPointS);
            float movementT = Vector3.Distance(_lastSampleTrackingPointT, currentPointT);
            if (movementS < MovementThresholdM || movementT < MovementThresholdM)
                return false;
        }
        return true;
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
    }
}