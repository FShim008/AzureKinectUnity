// StudyKeypointUdpSender.cs
// DROP THIS INTO THE CO-PI's AzureKinectUnity PROJECT: Assets/Scripts/StudyKeypointUdpSender.cs
// (NOT into the study project — this runs on the capture PC alongside the cameras.)
//
// Bridges the AzureKinectUnity tracking pipeline to the "When and Where to Warn" study app.
// It subscribes to EVERY SkeletonTracker in the scene (one per camera; all already calibrated into
// the Camera-1 world frame), picks the participant in each camera, fuses the 6 study joints across
// cameras by per-joint confidence, and UDP-sends ONE CSV line per frame to the Study PC:
//
//   timestamp, hX,hY,hZ, cX,cY,cZ, lhX,lhY,lhZ, rhX,rhY,rhZ, lfX,lfY,lfZ, rfX,rfY,rfZ
//   (seconds, then 6 joints in METERS, order = Head, Chest, LeftHand, RightHand, LeftFoot, RightFoot)
//
// This matches CollisionFeedback.Core.KeypointDeserializer / TrackingStream_Spec.md exactly, so the
// study's UdpKeypointSource (port 9000) parses it with no changes.
//
// Wiring: create an empty GameObject "StudyBridge", add this component, set StudyPcIp to the Study
// PC's IPv4 (or 127.0.0.1 if same machine) and Port = 9000. Press Play with the cameras running and
// calibration loaded (key 'L').

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using K4AdotNet.BodyTracking;

public class StudyKeypointUdpSender : MonoBehaviour
{
    [Header("Destination (the Study / Unity PC)")]
    public string StudyPcIp = "127.0.0.1"; // set to the Study PC IPv4 if on a different machine
    public int Port = 9000;

    [Header("Send rate (Hz cap)")]
    [Range(15f, 60f)] public float SendHz = 30f;

    // The 6 study joints, in the study's fixed order (Head, Chest, LeftHand, RightHand, LeftFoot, RightFoot).
    // To retarget a limb, change a value here. Notes:
    //  - Chest uses SpineChest (the vest/torso site).
    //  - "Foot" maps to Ankle (lower leg, near the shin tactor). Swap to KneeLeft/KneeRight if you want
    //    the keypoint at the ~0.40 m height of the low obstacle O1 (knee/shin). Swap Hand->Wrist if the
    //    hand tip is jittery.
    static readonly JointType[] StudyJoints =
    {
        JointType.Head,
        JointType.SpineChest,
        JointType.HandLeft,
        JointType.HandRight,
        JointType.AnkleLeft,
        JointType.AnkleRight,
    };

    sealed class CamCache { public SkeletonData Data; public bool Has; }
    readonly Dictionary<string, CamCache> _cams = new();
    SkeletonTracker[] _trackers;

    UdpClient _udp;
    float _accum;
    bool _dirty; // a new frame arrived since the last send -> never repeat a stale frame (spec requirement)

    void OnEnable()
    {
        _udp = new UdpClient();
        _trackers = FindObjectsByType<SkeletonTracker>(FindObjectsSortMode.None);
        foreach (var t in _trackers) t.OnSkeletonsProcessed += OnSkeletons;
        Debug.Log($"[StudyKeypointUdpSender] Bridging {_trackers.Length} camera(s) -> {StudyPcIp}:{Port}");
    }

    void OnDisable()
    {
        if (_trackers != null)
            foreach (var t in _trackers) if (t != null) t.OnSkeletonsProcessed -= OnSkeletons;
        _udp?.Dispose();
        _udp = null;
    }

    // Fires on the main thread from SkeletonTracker.Update, once per camera per frame.
    void OnSkeletons(object sender, SkeletonEventArgs e)
    {
        if (e?.Skeleton == null) return;
        string key = string.IsNullOrEmpty(e.DeviceSerialNumber) ? sender.GetHashCode().ToString() : e.DeviceSerialNumber;
        if (!_cams.TryGetValue(key, out var cam)) { cam = new CamCache(); _cams[key] = cam; }
        cam.Data = e.Skeleton.Value;
        cam.Has = true;
        _dirty = true;
    }

    void Update()
    {
        _accum += Time.unscaledDeltaTime;
        if (_accum < 1f / Mathf.Max(1f, SendHz)) return;
        if (!_dirty) return; // no fresh tracking this interval -> send nothing (do not repeat last value)
        _accum = 0f;
        _dirty = false;
        TrySendFused();
    }

    void TrySendFused()
    {
        var accPos = new Vector3[StudyJoints.Length];
        var accW = new float[StudyJoints.Length];
        bool any = false;

        foreach (var kv in _cams)
        {
            var cam = kv.Value;
            if (!cam.Has || cam.Data.Skeletons == null || cam.Data.Skeletons.Length == 0) continue;
            if (!TryPickParticipant(cam.Data, out var skel)) continue;
            any = true;

            for (int s = 0; s < StudyJoints.Length; s++)
            {
                JointData jd = skel.Joints[(int)StudyJoints[s]];
                float w = ConfWeight(jd.ConfidenceLevel);
                if (w <= 0f) continue;                 // ignore unconfident estimates
                accPos[s] += jd.Position * w;
                accW[s] += w;
            }
        }
        if (!any) return;

        var outPos = new Vector3[StudyJoints.Length];
        for (int s = 0; s < StudyJoints.Length; s++)
        {
            if (accW[s] <= 0f) return;                 // a required joint had no confident view -> skip frame
            outPos[s] = accPos[s] / accW[s];           // confidence-weighted fusion across cameras
        }

        Send(outPos);
    }

    // One participant per camera: the body with the most total confidence over the 6 study joints.
    // (Single-participant study; a spotter at the edge will have a lower-confidence/partial skeleton.)
    static bool TryPickParticipant(SkeletonData data, out Skeleton best)
    {
        best = default;
        float bestScore = -1f;
        foreach (var s in data.Skeletons)
        {
            if (s.Joints == null || s.Joints.Length < 32) continue;
            float score = 0f;
            foreach (var j in StudyJoints) score += ConfWeight(s.Joints[(int)j].ConfidenceLevel);
            if (score > bestScore) { bestScore = score; best = s; }
        }
        return bestScore > 0f;
    }

    static float ConfWeight(JointConfidenceLevel c) => c switch
    {
        JointConfidenceLevel.High => 3f,
        JointConfidenceLevel.Medium => 2f,
        JointConfidenceLevel.Low => 1f,
        _ => 0f, // None / unknown -> ignore
    };

    void Send(Vector3[] joints)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(180);
        double tSec = GlobalHostClock.NowUsec() / 1_000_000.0;
        sb.Append(tSec.ToString("F5", inv));
        for (int i = 0; i < joints.Length; i++)
            sb.Append(',').Append(joints[i].x.ToString("F5", inv))
              .Append(',').Append(joints[i].y.ToString("F5", inv))
              .Append(',').Append(joints[i].z.ToString("F5", inv));
        sb.Append('\n');

        byte[] bytes = Encoding.ASCII.GetBytes(sb.ToString());
        try { _udp.Send(bytes, bytes.Length, StudyPcIp, Port); }
        catch (Exception ex) { Debug.LogWarning($"[StudyKeypointUdpSender] send failed: {ex.Message}"); }
    }
}
