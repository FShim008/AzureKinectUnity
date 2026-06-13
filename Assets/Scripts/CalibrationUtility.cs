using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

public static class CalibrationUtility
{
    // If YOU calibrate all cameras directly to BaseCameraNum (recommended), keep this OFF.
    public static bool EnableTransitiveSweep = false;

    // Base camera number (your Kinect-0 if you use CameraNumber scheme 1..N)
    public static int BaseCameraNum = 1;

    // Your requested absolute folder
    public static string CalibrationDirectoryOverride =
        @"C:\Users\Human Mobility SP1\Documents\GitHub\AzureKinectUnity\Assets\CalibrationFiles";

    public static void SetCalibrationDirectory(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            Debug.LogWarning("[CalibrationUtility] SetCalibrationDirectory called with empty path. Ignoring.");
            return;
        }

        CalibrationDirectoryOverride = absolutePath;

        if (!Directory.Exists(CalibrationDirectoryOverride))
            Directory.CreateDirectory(CalibrationDirectoryOverride);

        Debug.Log($"[CalibrationUtility] Calibration directory set to: {CalibrationDirectoryOverride}");
    }

    public static string GetCalibrationDirectory()
    {
        if (!string.IsNullOrWhiteSpace(CalibrationDirectoryOverride))
            return CalibrationDirectoryOverride;

        return Path.Combine(Application.dataPath, "CalibrationFiles");
    }

    public static string GetCalibrationFileName(int fromCam, int toCam)
        => $"calib-{fromCam}-{toCam}.txt";

    public static string GetCalibrationFilePath(string fileName)
        => Path.Combine(GetCalibrationDirectory(), fileName);

    // -------------------- Required-pairs bookkeeping (optional) --------------------
    public class CalibrationPair
    {
        public int SourceNum;
        public int TargetNum;
        public bool IsComplete;
    }

    private static readonly List<CalibrationPair> _requiredCalibrations = new List<CalibrationPair>();

    public static void RegisterRequiredCalibration(int sourceNum, int targetNum)
    {
        if (sourceNum == targetNum) return;

        if (!_requiredCalibrations.Any(p => p.SourceNum == sourceNum && p.TargetNum == targetNum))
        {
            _requiredCalibrations.Add(new CalibrationPair
            {
                SourceNum = sourceNum,
                TargetNum = targetNum,
                IsComplete = false
            });

            Debug.Log($"[CalibrationUtility] Registered required direct calibration: {sourceNum}->{targetNum}. Total required: {_requiredCalibrations.Count}");
        }
    }

    /// <summary>
    /// Saves source->target AND also saves inverse target->source (if invertible).
    /// Also updates required-pair tracking and optionally runs sweep (if enabled).
    /// </summary>
    public static void MarkCalibrationComplete(int sourceNum, int targetNum, Matrix4x4 T_S_to_T)
    {
        // Save forward
        string forwardName = GetCalibrationFileName(sourceNum, targetNum);
        SaveMatrixToFile(T_S_to_T, forwardName);

        // Save inverse (target->source)
        if (TryInvertMatrix4x4(T_S_to_T, out var inv))
        {
            string inverseName = GetCalibrationFileName(targetNum, sourceNum);
            SaveMatrixToFile(inv, inverseName);
            Debug.Log($"[CalibrationUtility] Also saved inverse: {inverseName}");
        }
        else
        {
            Debug.LogWarning("[CalibrationUtility] Could not invert matrix, inverse file not saved.");
        }

        // Mark required pair complete if it exists
        var pair = _requiredCalibrations.FirstOrDefault(p => p.SourceNum == sourceNum && p.TargetNum == targetNum);
        if (pair != null) pair.IsComplete = true;

        if (_requiredCalibrations.Count > 0 && _requiredCalibrations.All(p => p.IsComplete))
        {
            Debug.Log("--- ALL REQUIRED DIRECT CALIBRATIONS ARE COMPLETE. ---");
            if (EnableTransitiveSweep)
            {
                Debug.Log("--- Triggering Transitive Sweep (EnableTransitiveSweep=true) ---");
                ComputeAllFinalTransforms();
            }
            else
            {
                Debug.Log("--- Transitive Sweep is OFF (EnableTransitiveSweep=false). Skipping. ---");
                _requiredCalibrations.Clear();
            }
        }
    }

    // -------------------- Load / Save --------------------
    public static bool TryLoadMatrixFromFile(string fileName, out Matrix4x4 matrix)
    {
        string filePath = GetCalibrationFilePath(fileName);
        matrix = Matrix4x4.identity;

        if (!File.Exists(filePath))
            return false;

        try
        {
            string[] lines = File.ReadAllLines(filePath);
            var culture = System.Globalization.CultureInfo.InvariantCulture;

            for (int i = 0; i < 4; i++)
            {
                if (i >= lines.Length) return false;

                string[] values = lines[i].Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (values.Length < 4) return false;

                for (int j = 0; j < 4; j++)
                {
                    if (!float.TryParse(values[j], System.Globalization.NumberStyles.Float, culture, out float val))
                        return false;

                    matrix[i, j] = val;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CalibrationUtility] Error reading calibration file {filePath}: {ex.Message}");
            return false;
        }
    }

    public static Matrix4x4 LoadMatrixFromFile(string fileName)
    {
        if (TryLoadMatrixFromFile(fileName, out Matrix4x4 m))
            return m;
        return Matrix4x4.identity;
    }

    public static void SaveMatrixToFile(Matrix4x4 matrix, string fileName)
    {
        string directoryPath = GetCalibrationDirectory();
        if (!Directory.Exists(directoryPath))
            Directory.CreateDirectory(directoryPath);

        string filePath = Path.Combine(directoryPath, fileName);
        SaveMatrixToPath(matrix, filePath);
    }

    public static void SaveMatrixToPath(Matrix4x4 matrix, string fullPath)
    {
        try
        {
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var culture = System.Globalization.CultureInfo.InvariantCulture;

            using (var sw = new StreamWriter(fullPath, false))
            {
                for (int i = 0; i < 4; i++)
                {
                    sw.WriteLine(
                        $"{matrix[i, 0].ToString(culture)} {matrix[i, 1].ToString(culture)} {matrix[i, 2].ToString(culture)} {matrix[i, 3].ToString(culture)}"
                    );
                }
            }

            Debug.Log($"[CalibrationUtility] Calibration matrix saved to: {fullPath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[CalibrationUtility] Failed saving matrix to {fullPath}: {ex}");
        }
    }

    public static bool TryInvertMatrix4x4(Matrix4x4 m, out Matrix4x4 inv)
    {
        float det =
            m.m00 * (m.m11 * m.m22 - m.m12 * m.m21) -
            m.m01 * (m.m10 * m.m22 - m.m12 * m.m20) +
            m.m02 * (m.m10 * m.m21 - m.m11 * m.m20);

        if (Mathf.Abs(det) < 1e-8f)
        {
            inv = Matrix4x4.identity;
            return false;
        }

        inv = m.inverse;
        return true;
    }

    // -------------------- Transitive sweep (optional) --------------------
    public static void ComputeAllFinalTransforms()
    {
        if (_requiredCalibrations.Count == 0)
        {
            Debug.LogWarning("[CalibrationUtility] Transitive sweep requested, but no required calibrations were registered.");
            return;
        }

        int maxCameraNum = _requiredCalibrations.SelectMany(p => new[] { p.SourceNum, p.TargetNum }).Max();
        var directTransforms = LoadAllDirectCalibrations(maxCameraNum);

        for (int sourceNum = 1; sourceNum <= maxCameraNum; sourceNum++)
        {
            if (sourceNum == BaseCameraNum) continue;

            if (TryComputePath(sourceNum, BaseCameraNum, directTransforms, out Matrix4x4 T_S_to_Base))
            {
                string finalFileName = GetCalibrationFileName(sourceNum, BaseCameraNum);
                SaveMatrixToFile(T_S_to_Base, finalFileName);
                Debug.Log($"[CalibrationUtility] Transitive calibration computed & saved: {finalFileName}");
            }
            else
            {
                Debug.LogError($"[CalibrationUtility] Failed to find path from Camera {sourceNum} to Camera {BaseCameraNum}.");
            }
        }

        Debug.Log("--- [CalibrationUtility] Transitive Calibration Sweep Finished ---");
        _requiredCalibrations.Clear();
    }

    private static bool TryComputePath(
        int sourceNum,
        int targetNum,
        Dictionary<(int, int), Matrix4x4> directTransforms,
        out Matrix4x4 result,
        List<int> visited = null)
    {
        if (sourceNum == targetNum)
        {
            result = Matrix4x4.identity;
            return true;
        }

        visited ??= new List<int> { sourceNum };

        if (directTransforms.TryGetValue((sourceNum, targetNum), out Matrix4x4 directMatrix))
        {
            result = directMatrix;
            return true;
        }

        var outgoingTargets = directTransforms.Keys
            .Where(k => k.Item1 == sourceNum)
            .Select(k => k.Item2)
            .ToList();

        foreach (int intermediate in outgoingTargets)
        {
            if (visited.Contains(intermediate))
                continue;

            Matrix4x4 T_S_to_I = directTransforms[(sourceNum, intermediate)];

            visited.Add(intermediate);
            if (TryComputePath(intermediate, targetNum, directTransforms, out Matrix4x4 T_I_to_T, visited))
            {
                result = T_I_to_T * T_S_to_I;
                visited.Remove(intermediate);
                return true;
            }
            visited.Remove(intermediate);
        }

        result = Matrix4x4.identity;
        return false;
    }

    private static Dictionary<(int, int), Matrix4x4> LoadAllDirectCalibrations(int maxCameraNum)
    {
        var transforms = new Dictionary<(int, int), Matrix4x4>();
        string directoryPath = GetCalibrationDirectory();

        if (!Directory.Exists(directoryPath))
            return transforms;

        var files = Directory.GetFiles(directoryPath, "calib-*-*.txt");

        foreach (var f in files)
        {
            string fileName = Path.GetFileName(f);
            string nameNoExt = Path.GetFileNameWithoutExtension(fileName);
            string[] parts = nameNoExt.Split('-'); // calib-S-T

            if (parts.Length == 3 &&
                int.TryParse(parts[1], out int sourceNum) &&
                int.TryParse(parts[2], out int targetNum))
            {
                if (sourceNum <= maxCameraNum && targetNum <= maxCameraNum && sourceNum != targetNum)
                {
                    if (TryLoadMatrixFromFile(fileName, out Matrix4x4 matrix))
                        transforms[(sourceNum, targetNum)] = matrix;
                }
            }
        }

        return transforms;
    }

    // -------------------- Rigid transform solver --------------------
    public static Matrix4x4 ComputeTransformationMatrix(List<Vector3> fromPoints, List<Vector3> toPoints)
    {
        if (fromPoints.Count != toPoints.Count || fromPoints.Count < 3)
        {
            Debug.LogError("[CalibrationUtility] Point counts must match and be >= 3 for calibration.");
            return Matrix4x4.identity;
        }

        int N = fromPoints.Count;

        Vector3 fromCentroid = Vector3.zero;
        Vector3 toCentroid = Vector3.zero;
        for (int i = 0; i < N; i++)
        {
            fromCentroid += fromPoints[i];
            toCentroid += toPoints[i];
        }
        fromCentroid /= N;
        toCentroid /= N;

        var X = DenseMatrix.Create(N, 3, 0);
        var Y = DenseMatrix.Create(N, 3, 0);
        for (int i = 0; i < N; i++)
        {
            var fx = fromPoints[i] - fromCentroid;
            var ty = toPoints[i] - toCentroid;
            X[i, 0] = fx.x; X[i, 1] = fx.y; X[i, 2] = fx.z;
            Y[i, 0] = ty.x; Y[i, 1] = ty.y; Y[i, 2] = ty.z;
        }

        var H = X.Transpose() * Y;
        var svd = H.Svd(true);
        var U = svd.U;
        var VT = svd.VT;
        var V = VT.Transpose();

        var R = V * U.Transpose();

        if (R.Determinant() < 0)
        {
            V.SetColumn(2, V.Column(2).Multiply(-1));
            R = V * U.Transpose();
        }

        var fc = Vector<double>.Build.Dense(new double[] { fromCentroid.x, fromCentroid.y, fromCentroid.z });
        var rc = R * fc;

        var t = new Vector3(
            toCentroid.x - (float)rc[0],
            toCentroid.y - (float)rc[1],
            toCentroid.z - (float)rc[2]
        );

        Matrix4x4 T = Matrix4x4.identity;
        T[0, 0] = (float)R[0, 0]; T[0, 1] = (float)R[0, 1]; T[0, 2] = (float)R[0, 2];
        T[1, 0] = (float)R[1, 0]; T[1, 1] = (float)R[1, 1]; T[1, 2] = (float)R[1, 2];
        T[2, 0] = (float)R[2, 0]; T[2, 1] = (float)R[2, 1]; T[2, 2] = (float)R[2, 2];

        T[0, 3] = t.x;
        T[1, 3] = t.y;
        T[2, 3] = t.z;

        return T;
    }
}
