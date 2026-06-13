using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using K4AdotNet.BodyTracking;

public class DualCameraCalibrator : MonoBehaviour
{
    [Header("Trackers (assign in Inspector)")]
    public SkeletonTracker sourceTracker;
    public SkeletonTracker targetTracker;

    [Header("Camera Identity")]
    public int SourceCameraNumber = 2;
    public int TargetCameraNumber = 1;

    [Header("Calibration Save Location (ABSOLUTE)")]
    public string CalibrationSaveDirectory =
        @"C:\Users\Human Mobility SP1\Documents\GitHub\AzureKinectUnity\Assets\CalibrationFiles";

    [Header("Input (Local Hotkey)")]
    public bool EnableLocalHotkey = true;
    public Key StartCollectKey = Key.C;

    [Header("Sampling")]
    public int RequiredSamples = 300;
    public float SampleIntervalSec = 0.08f;

    [Header("Joint Selection")]
    public int[] CalibrationJointIndices = new int[]
    {
        (int)JointType.Pelvis,
        (int)JointType.SpineNavel,
        (int)JointType.SpineChest,
        (int)JointType.ShoulderLeft,
        (int)JointType.ShoulderRight,
        (int)JointType.HipLeft,
        (int)JointType.HipRight,
    };

    [Range(0, 2)]
    public int MinConfidenceInt = 2;

    [Header("Pairing")]
    public float MaxTimeDiffMs = 250f;     // ✅ more forgiving for multi-cam
    public int MaxQueuedFrames = 120;      // ✅ bigger queue to tolerate jitter

    [Header("Robust Fit")]
    [Range(0f, 0.4f)]
    public float OutlierRejectFraction = 0.10f;

    private JointConfidenceLevel MinConfidence =>
        (JointConfidenceLevel)Mathf.Clamp(MinConfidenceInt, 0, 2);

    private struct Frame
    {
        public long timestampUsec;     // ✅ this is HOST time now (from SkeletonTracker)
        public Skeleton[] skeletons;
    }

    private readonly Queue<Frame> _sourceQ = new Queue<Frame>(256);
    private readonly Queue<Frame> _targetQ = new Queue<Frame>(256);

    private readonly List<Vector3> _sourcePoints = new List<Vector3>(4096);
    private readonly List<Vector3> _targetPoints = new List<Vector3>(4096);

    private bool _isCollecting = false;
    private bool _hasSolved = false;
    private int _accepted = 0;
    private double _lastAcceptedHostTime = -999.0;

    private int _noMatchSpamLimiter = 0;

    private void Start()
    {
        CalibrationUtility.SetCalibrationDirectory(CalibrationSaveDirectory);

        if (sourceTracker == null || targetTracker == null)
        {
            Debug.LogError($"[DualCameraCalibrator {SourceCameraNumber}->{TargetCameraNumber}] Missing trackers.");
            enabled = false;
            return;
        }

        CalibrationUtility.RegisterRequiredCalibration(SourceCameraNumber, TargetCameraNumber);

        sourceTracker.OnSkeletonsProcessed += OnSourceSkeletonsProcessed;
        targetTracker.OnSkeletonsProcessed += OnTargetSkeletonsProcessed;

        Debug.Log($"[DualCameraCalibrator {SourceCameraNumber}->{TargetCameraNumber}] Ready. Press '{StartCollectKey}' to start.");
    }

    private void OnDestroy()
    {
        if (sourceTracker != null) sourceTracker.OnSkeletonsProcessed -= OnSourceSkeletonsProcessed;
        if (targetTracker != null) targetTracker.OnSkeletonsProcessed -= OnTargetSkeletonsProcessed;
    }

    public void BeginCollect()
    {
        _sourcePoints.Clear();
        _targetPoints.Clear();
        _accepted = 0;

        _sourceQ.Clear();
        _targetQ.Clear();

        _isCollecting = true;
        _hasSolved = false;
        _lastAcceptedHostTime = -999.0;
        _noMatchSpamLimiter = 0;

        Debug.Log($"[DualCameraCalibrator {SourceCameraNumber}->{TargetCameraNumber}] START collecting {RequiredSamples} samples. " +
                  $"MinConfidence={MinConfidence}, MaxTimeDiffMs={MaxTimeDiffMs}, SampleIntervalSec={SampleIntervalSec}, MaxQueuedFrames={MaxQueuedFrames}");
    }

    private void Update()
    {
        if (EnableLocalHotkey && Keyboard.current != null &&
            Keyboard.current[StartCollectKey].wasPressedThisFrame)
        {
            if (!_isCollecting && !_hasSolved)
                BeginCollect();
            else
                Debug.Log($"[DualCameraCalibrator {SourceCameraNumber}->{TargetCameraNumber}] Already collecting ({_accepted}/{RequiredSamples}).");
        }

        if (_isCollecting && !_hasSolved)
            TryMatchAndCollect();
    }

    private void OnSourceSkeletonsProcessed(object sender, SkeletonEventArgs e)
    {
        EnqueueFrame(_sourceQ, e.Skeleton);
    }

    private void OnTargetSkeletonsProcessed(object sender, SkeletonEventArgs e)
    {
        EnqueueFrame(_targetQ, e.Skeleton);
    }

    private void EnqueueFrame(Queue<Frame> q, SkeletonData? dataOpt)
    {
        if (!dataOpt.HasValue) return;
        var data = dataOpt.Value;
        if (data.Skeletons == null || data.Skeletons.Length == 0) return;

        // ✅ Timestamp is HOST time now (set in SkeletonTracker)
        q.Enqueue(new Frame { timestampUsec = data.Timestamp, skeletons = data.Skeletons });

        while (q.Count > Mathf.Max(5, MaxQueuedFrames))
            q.Dequeue();
    }

    private void TryMatchAndCollect()
    {
        if (_accepted >= RequiredSamples)
        {
            SolveAndSave();
            return;
        }

        if (_sourceQ.Count == 0 || _targetQ.Count == 0) return;

        // pacing
        double now = Time.realtimeSinceStartupAsDouble;
        if (_lastAcceptedHostTime > 0 && (now - _lastAcceptedHostTime) < SampleIntervalSec)
            return;

        long maxDtUsec = (long)(MaxTimeDiffMs * 1000.0);

        var sArr = _sourceQ.ToArray();
        var tArr = _targetQ.ToArray();

        bool found = false;
        long bestAbsDt = long.MaxValue;
        Frame bestS = default, bestT = default;

        foreach (var s in sArr)
        {
            foreach (var t in tArr)
            {
                long dt = Math.Abs(s.timestampUsec - t.timestampUsec);
                if (dt <= maxDtUsec && dt < bestAbsDt)
                {
                    bestAbsDt = dt;
                    bestS = s;
                    bestT = t;
                    found = true;
                }
            }
        }

        if (!found)
        {
            // Debug: show head delta occasionally
            if ((_noMatchSpamLimiter++ % 60) == 0)
            {
                long headDtMs = Math.Abs(_sourceQ.Peek().timestampUsec - _targetQ.Peek().timestampUsec) / 1000;
                Debug.LogWarning($"[DualCameraCalibrator {SourceCameraNumber}->{TargetCameraNumber}] No match. " +
                                 $"Head dt={headDtMs}ms, Qsrc={_sourceQ.Count}, Qtgt={_targetQ.Count}. " +
                                 $"Try increasing MaxTimeDiffMs (current {MaxTimeDiffMs}).");
            }

            if (_sourceQ.Peek().timestampUsec < _targetQ.Peek().timestampUsec) _sourceQ.Dequeue();
            else _targetQ.Dequeue();
            return;
        }

        while (_sourceQ.Count > 0 && _sourceQ.Peek().timestampUsec < bestS.timestampUsec) _sourceQ.Dequeue();
        while (_targetQ.Count > 0 && _targetQ.Peek().timestampUsec < bestT.timestampUsec) _targetQ.Dequeue();

        if (_sourceQ.Count == 0 || _targetQ.Count == 0) return;

        var sFrame = _sourceQ.Dequeue();
        var tFrame = _targetQ.Dequeue();

        if (!TrySelectBestSkeleton(sFrame.skeletons, out var sSkel)) return;
        if (!TrySelectBestSkeleton(tFrame.skeletons, out var tSkel)) return;

        if (!TryBuildAveragedFramePoints(sSkel, tSkel, out var avgS, out var avgT))
            return;

        _sourcePoints.Add(avgS);
        _targetPoints.Add(avgT);
        _accepted++;
        _lastAcceptedHostTime = now;

        if (_accepted == 1 || _accepted % 25 == 0)
            Debug.Log($"[DualCameraCalibrator {SourceCameraNumber}->{TargetCameraNumber}] Sample ADDED {_accepted}/{RequiredSamples} (bestDt={bestAbsDt / 1000}ms)");

        if (_accepted >= RequiredSamples)
            SolveAndSave();
    }

    private bool TrySelectBestSkeleton(Skeleton[] skeletons, out Skeleton best)
    {
        best = default;
        int idx = (int)JointType.SpineNavel;

        bool found = false;
        float bestScore = -1;

        foreach (var sk in skeletons)
        {
            if (sk.Joints == null || sk.Joints.Length <= idx) continue;

            var j = sk.Joints[idx];
            if (j.ConfidenceLevel < MinConfidence) continue;

            float score = (float)j.ConfidenceLevel;
            if (!found || score > bestScore)
            {
                best = sk;
                bestScore = score;
                found = true;
            }
        }
        return found;
    }

    private bool TryBuildAveragedFramePoints(Skeleton s, Skeleton t, out Vector3 avgS, out Vector3 avgT)
    {
        avgS = Vector3.zero;
        avgT = Vector3.zero;
        int used = 0;

        foreach (int ji in CalibrationJointIndices)
        {
            if (s.Joints == null || t.Joints == null) return false;
            if (ji < 0 || ji >= 32) continue;

            var sj = s.Joints[ji];
            var tj = t.Joints[ji];

            if (sj.ConfidenceLevel < MinConfidence || tj.ConfidenceLevel < MinConfidence)
                continue;

            avgS += sj.Position;
            avgT += tj.Position;
            used++;
        }

        if (used < 3) return false;
        avgS /= used;
        avgT /= used;
        return true;
    }

    private void SolveAndSave()
    {
        if (_hasSolved) return;
        if (_sourcePoints.Count < 3 || _targetPoints.Count < 3)
        {
            Debug.LogWarning($"[DualCameraCalibrator {SourceCameraNumber}->{TargetCameraNumber}] Not enough points to solve.");
            return;
        }

        _hasSolved = true;
        _isCollecting = false;

        Debug.Log($"[DualCameraCalibrator {SourceCameraNumber}->{TargetCameraNumber}] Collected {_accepted}. Solving...");

        Matrix4x4 M = CalibrationUtility.ComputeTransformationMatrix(_sourcePoints, _targetPoints);

        if (OutlierRejectFraction > 0f && OutlierRejectFraction < 0.4f && _sourcePoints.Count >= 10)
        {
            int n = _sourcePoints.Count;
            var residuals = new List<(float err, int idx)>(n);

            for (int i = 0; i < n; i++)
            {
                Vector3 p = M.MultiplyPoint3x4(_sourcePoints[i]);
                float e = (p - _targetPoints[i]).sqrMagnitude;
                residuals.Add((e, i));
            }

            residuals.Sort((a, b) => a.err.CompareTo(b.err));
            int keep = Mathf.Max(3, Mathf.RoundToInt(n * (1f - OutlierRejectFraction)));

            var newS = new List<Vector3>(keep);
            var newT = new List<Vector3>(keep);
            for (int k = 0; k < keep; k++)
            {
                int idx = residuals[k].idx;
                newS.Add(_sourcePoints[idx]);
                newT.Add(_targetPoints[idx]);
            }

            M = CalibrationUtility.ComputeTransformationMatrix(newS, newT);
        }

        CalibrationUtility.MarkCalibrationComplete(SourceCameraNumber, TargetCameraNumber, M);

        Debug.Log($"[DualCameraCalibrator {SourceCameraNumber}->{TargetCameraNumber}] --- Saved calib-{SourceCameraNumber}-{TargetCameraNumber}.txt ---");
    }
}
