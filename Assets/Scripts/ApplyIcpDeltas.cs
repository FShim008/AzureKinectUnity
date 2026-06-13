// ApplyICPDeltas.cs
// Safe ICP applier:
// - Loads ICP matrices from FULL PATH (no CalibrationUtility dir surprises)
// - STRICTLY applies ICP via SetIcpDelta / SetIcpCorrection only (never overwrites calib)
// - Disables ICP per-camera if file missing/parse fails/safety gates fail (prevents stale delta)
// - Smooths translation/rotation with exponential smoothing

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

public class ApplyICPDeltas : MonoBehaviour
{
    [Header("Hotkeys")]
    public Key ToggleIcpKey = Key.I;
    public Key ResetKey = Key.O;

    [Header("ICP Delta Files")]
    [Tooltip("Directory containing ICP delta matrices. If empty, uses <Project>/Assets/CalibrationFiles (CalibrationUtility dir).")]
    public string IcpDeltaDirectoryOverride = "";
    [Tooltip("File pattern for ICP deltas. Example: icp-<cam>-<base>.txt")]
    public string IcpDeltaFilePattern = "icp-{0}-{1}.txt";

    [Header("Base Camera")]
    public int BaseCameraNum = 1;

    [Header("Targets")]
    public List<KinectDevice> Devices = new List<KinectDevice>();

    [Header("Apply Rate (Hz)")]
    [Range(0.1f, 10f)] public float ApplyHz = 1.0f;

    [Header("Smoothing")]
    [Tooltip("Time constant for exponential smoothing. Smaller = faster response.")]
    [Range(0.01f, 5f)] public float SmoothTime = 0.6f;

    [Header("Safety Gates (always active)")]
    [Tooltip("Reject ICP update if delta translation magnitude exceeds this (meters).")]
    public float MaxTranslationMeters = 0.12f;
    [Tooltip("Reject ICP update if delta rotation exceeds this (degrees).")]
    public float MaxRotationDegrees = 8f;

    [Header("Logging")]
    public bool Verbose = true;
    [Tooltip("Print per-camera reject reasons every N apply ticks (set 1 for full spam while debugging).")]
    public int LogRejectDetailsEveryNTicks = 10;

    private bool _icpEnabled;
    private float _nextApplyTime;
    private int _applyCounter;

    private class DeviceState
    {
        public Matrix4x4 currentSmoothed = Matrix4x4.identity;
        public bool hasValue = false;
        public int lastRejectTick = -9999;
        public string lastRejectReason = "";
    }

    private readonly Dictionary<int, DeviceState> _stateByCam = new Dictionary<int, DeviceState>();

    private void Start()
    {
        if (Devices == null || Devices.Count == 0)
            Devices = FindObjectsByType<KinectDevice>(FindObjectsInactive.Include, FindObjectsSortMode.None).ToList();

        foreach (var d in Devices)
        {
            if (d == null) continue;
            int cam = d.EffectiveCameraNumber;
            if (!_stateByCam.ContainsKey(cam))
                _stateByCam[cam] = new DeviceState();
        }

        if (Verbose)
            Debug.Log($"[ApplyICPDeltas] Found {Devices.Count} KinectDevice components. ApplyHz={ApplyHz}, SmoothTime={SmoothTime}");
    }

    private void Update()
    {
        if (Keyboard.current == null) return;

        if (Keyboard.current[ResetKey].wasPressedThisFrame)
        {
            DisableAndReset();
            return;
        }

        if (Keyboard.current[ToggleIcpKey].wasPressedThisFrame)
        {
            _icpEnabled = !_icpEnabled;

            if (_icpEnabled)
            {
                _nextApplyTime = Time.time;
                if (Verbose) Debug.Log("[ApplyICPDeltas] ICP ENABLED.");
            }
            else
            {
                // Turn OFF ICP everywhere
                foreach (var d in Devices)
                    SetIcpOnDeviceStrict(d, Matrix4x4.identity, enable: false);

                if (Verbose) Debug.Log("[ApplyICPDeltas] ICP DISABLED.");
            }
        }

        if (!_icpEnabled) return;

        if (Time.time >= _nextApplyTime)
        {
            _nextApplyTime = Time.time + (1f / Mathf.Max(0.01f, ApplyHz));
            ApplySmoothedFromDisk();
        }
    }

    private void DisableAndReset()
    {
        _icpEnabled = false;
        _applyCounter = 0;

        foreach (var kv in _stateByCam)
        {
            kv.Value.currentSmoothed = Matrix4x4.identity;
            kv.Value.hasValue = false;
            kv.Value.lastRejectReason = "";
            kv.Value.lastRejectTick = -9999;
        }

        foreach (var d in Devices)
            SetIcpOnDeviceStrict(d, Matrix4x4.identity, enable: false);

        if (Verbose) Debug.Log("[ApplyICPDeltas] ICP RESET (disabled, cleared smoothing).");
    }

    private void ApplySmoothedFromDisk()
    {
        _applyCounter++;

        string dir = !string.IsNullOrWhiteSpace(IcpDeltaDirectoryOverride)
            ? IcpDeltaDirectoryOverride
            : CalibrationUtility.GetCalibrationDirectory();

        int applied = 0;
        int accepted = 0;
        int rejected = 0;
        int missing = 0;
        int noMethod = 0;

        float dt = Mathf.Max(Time.deltaTime, 1e-3f);
        float alpha = 1f - Mathf.Exp(-dt / Mathf.Max(0.001f, SmoothTime));

        foreach (var d in Devices)
        {
            if (d == null) continue;

            int cam = d.EffectiveCameraNumber;

            // Base camera: keep ICP off
            if (cam == BaseCameraNum)
            {
                SetIcpOnDeviceStrict(d, Matrix4x4.identity, enable: false);
                continue;
            }

            var st = GetOrCreateState(cam);

            string fileName = string.Format(IcpDeltaFilePattern, cam, BaseCameraNum);
            string fullPath = Path.Combine(dir, fileName);

            if (!File.Exists(fullPath))
            {
                missing++;
                Reject(st, $"Missing ICP file '{fileName}'", cam);
                SetIcpOnDeviceStrict(d, Matrix4x4.identity, enable: false);
                continue;
            }

            if (!TryLoadMatrixFromFullPath(fullPath, out var Mtarget))
            {
                rejected++;
                Reject(st, $"Failed parsing ICP file '{fileName}'", cam);
                SetIcpOnDeviceStrict(d, Matrix4x4.identity, enable: false);
                continue;
            }

            if (!PassesSafetyGates(Mtarget, out float tMag, out float rDeg))
            {
                rejected++;
                Reject(st, $"Clamp reject: |t|={tMag:F4}m (max {MaxTranslationMeters}), rot={rDeg:F2}° (max {MaxRotationDegrees})", cam);
                SetIcpOnDeviceStrict(d, Matrix4x4.identity, enable: false);
                continue;
            }

            Matrix4x4 Msmoothed = SmoothMatrix(st.hasValue ? st.currentSmoothed : Mtarget, Mtarget, alpha);
            st.currentSmoothed = Msmoothed;
            st.hasValue = true;

            if (!SetIcpOnDeviceStrict(d, Msmoothed, enable: true))
            {
                noMethod++;
                // If we can't apply ICP, avoid leaving any stale state
                SetIcpOnDeviceStrict(d, Matrix4x4.identity, enable: false);
                continue;
            }

            applied++;
            accepted++;
        }

        if (Verbose)
        {
            Debug.Log($"[ApplyICPDeltas] Tick {_applyCounter} Dir={dir} applied={applied}/{Devices.Count} accepted={accepted} rejected={rejected} missing={missing} noMethod={noMethod} alpha={alpha:F3}");
        }
    }

    private void Reject(DeviceState st, string reason, int cam)
    {
        st.lastRejectReason = reason;
        st.lastRejectTick = _applyCounter;

        if (Verbose && LogRejectDetailsEveryNTicks > 0 && (_applyCounter % LogRejectDetailsEveryNTicks == 0))
            Debug.LogWarning($"[ApplyICPDeltas] Cam {cam} rejected @tick {_applyCounter}: {reason}");
    }

    private DeviceState GetOrCreateState(int cam)
    {
        if (!_stateByCam.TryGetValue(cam, out var st))
        {
            st = new DeviceState();
            _stateByCam[cam] = st;
        }
        return st;
    }

    private bool PassesSafetyGates(Matrix4x4 M, out float translationMag, out float rotationDeg)
    {
        Vector3 t = ExtractTranslation(M);
        Quaternion q = ExtractRotation(M);

        translationMag = t.magnitude;
        rotationDeg = Quaternion.Angle(Quaternion.identity, q);

        if (float.IsNaN(translationMag) || float.IsNaN(rotationDeg)) return false;
        if (translationMag > MaxTranslationMeters) return false;
        if (rotationDeg > MaxRotationDegrees) return false;

        return true;
    }

    private static Matrix4x4 SmoothMatrix(Matrix4x4 current, Matrix4x4 target, float alpha)
    {
        Vector3 tc = ExtractTranslation(current);
        Quaternion qc = ExtractRotation(current);

        Vector3 tt = ExtractTranslation(target);
        Quaternion qt = ExtractRotation(target);

        Vector3 t = Vector3.Lerp(tc, tt, alpha);
        Quaternion q = Quaternion.Slerp(qc, qt, alpha);

        return Matrix4x4.TRS(t, q, Vector3.one);
    }

    private static Vector3 ExtractTranslation(Matrix4x4 M) => new Vector3(M.m03, M.m13, M.m23);

    private static Quaternion ExtractRotation(Matrix4x4 M)
    {
        // Unity column-major: forward = column 2, up = column 1
        Vector3 forward = new Vector3(M.m02, M.m12, M.m22);
        Vector3 up = new Vector3(M.m01, M.m11, M.m21);

        if (forward.sqrMagnitude < 1e-8f || up.sqrMagnitude < 1e-8f)
            return Quaternion.identity;

        return Quaternion.LookRotation(forward, up);
    }

    /// <summary>
    /// STRICT ICP apply:
    /// Only calls methods intended for ICP deltas.
    /// Never falls back to SetExternalCalibration (which would overwrite calib and "break" the scene).
    /// Returns true if an ICP setter was found and invoked.
    /// </summary>
    private bool SetIcpOnDeviceStrict(KinectDevice device, Matrix4x4 icpDelta, bool enable)
    {
        if (device == null) return false;

        var t = device.GetType();

        // Strict list: add alternatives ONLY if you truly have them in your KinectDevice.
        if (TryInvoke(t, device, "SetIcpDelta", new object[] { icpDelta, enable })) return true;
        if (TryInvoke(t, device, "SetIcpCorrection", new object[] { icpDelta, enable })) return true;

        if (Verbose && enable)
        {
            Debug.LogError($"[ApplyICPDeltas] KinectDevice '{device.name}' has no ICP setter (SetIcpDelta/SetIcpCorrection). " +
                           $"Refusing to overwrite calibration. ICP disabled for this device.");
        }

        return false;
    }

    private bool TryInvoke(Type type, object instance, string methodName, object[] args)
    {
        try
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var m in methods)
            {
                if (m.Name != methodName) continue;

                var p = m.GetParameters();
                if (p.Length != args.Length) continue;

                bool ok = true;
                for (int i = 0; i < p.Length; i++)
                {
                    if (args[i] == null) continue;

                    // Must be assignable
                    if (!p[i].ParameterType.IsAssignableFrom(args[i].GetType()))
                    {
                        ok = false;
                        break;
                    }
                }
                if (!ok) continue;

                m.Invoke(instance, args);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Loads a 4x4 matrix from a full path.
    /// Accepts 4 lines of 4 floats (space/tab/comma separated).
    /// Uses InvariantCulture so decimal parsing is stable.
    /// </summary>
    private static bool TryLoadMatrixFromFullPath(string fullPath, out Matrix4x4 M)
    {
        M = Matrix4x4.identity;

        try
        {
            var lines = File.ReadAllLines(fullPath)
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToArray();

            if (lines.Length < 4) return false;

            float[] nums = new float[16];
            int k = 0;

            for (int r = 0; r < 4; r++)
            {
                var parts = lines[r].Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) return false;

                for (int c = 0; c < 4; c++)
                {
                    nums[k++] = float.Parse(parts[c], CultureInfo.InvariantCulture);
                }
            }

            M.m00 = nums[0]; M.m01 = nums[1]; M.m02 = nums[2]; M.m03 = nums[3];
            M.m10 = nums[4]; M.m11 = nums[5]; M.m12 = nums[6]; M.m13 = nums[7];
            M.m20 = nums[8]; M.m21 = nums[9]; M.m22 = nums[10]; M.m23 = nums[11];
            M.m30 = nums[12]; M.m31 = nums[13]; M.m32 = nums[14]; M.m33 = nums[15];

            return true;
        }
        catch
        {
            return false;
        }
    }
}