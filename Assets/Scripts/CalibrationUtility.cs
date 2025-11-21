using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

public static class CalibrationUtility
{
    private const int BASE_CAMERA_NUM = 1;
    public class CalibrationPair
    {
        public int SourceNum;
        public int TargetNum;
        public bool IsComplete;
    }
    private static List<CalibrationPair> _requiredCalibrations = new List<CalibrationPair>();

    public static void RegisterRequiredCalibration(int sourceNum, int targetNum)
    {
        if (sourceNum != targetNum && !_requiredCalibrations.Any(p => p.SourceNum == sourceNum && p.TargetNum == targetNum))
        {
            _requiredCalibrations.Add(new CalibrationPair { SourceNum = sourceNum, TargetNum = targetNum, IsComplete = false });
            Debug.Log($"[Calibration Utility] Registered required direct calibration: {sourceNum}->{targetNum}. Total required: {_requiredCalibrations.Count}");
        }
    }

    public static void MarkCalibrationComplete(int sourceNum, int targetNum, Matrix4x4 T_S_to_T)
    {
        var pair = _requiredCalibrations.FirstOrDefault(p => p.SourceNum == sourceNum && p.TargetNum == targetNum);
        if (pair != null && !pair.IsComplete)
        {
            pair.IsComplete = true;
            string directFileName = $"calib-{sourceNum}-{targetNum}.txt";
            SaveMatrixToFile(T_S_to_T, directFileName);
            if (_requiredCalibrations.All(p => p.IsComplete))
            {
                Debug.Log("--- ALL REQUIRED DIRECT CALIBRATIONS ARE COMPLETE. Triggering Transitive Sweep. ---");
                ComputeAllFinalTransforms(); // Trigger the full sweep
            }
            else
            {
                int remaining = _requiredCalibrations.Count(p => !p.IsComplete);
                Debug.Log($"Direct calibration {sourceNum}->{targetNum} completed. {remaining} more direct calibrations needed before sweep.");
            }
        }
    }

    public static void ComputeAllFinalTransforms()
    {
        if (!_requiredCalibrations.Any())
        {
            Debug.LogWarning("Transitive sweep requested, but no required calibrations were registered.");
            return;
        }
        int maxCameraNum = _requiredCalibrations.SelectMany(p => new[] { p.SourceNum, p.TargetNum }).Max();
        var directTransforms = LoadAllDirectCalibrations(maxCameraNum);
        for (int sourceNum = BASE_CAMERA_NUM + 1; sourceNum <= maxCameraNum; sourceNum++)
        {
            Matrix4x4 T_S_to_1 = TryComputePath(sourceNum, BASE_CAMERA_NUM, directTransforms);
            if (T_S_to_1 != Matrix4x4.identity)
            {
                string finalFileName = $"calib-{sourceNum}-{BASE_CAMERA_NUM}.txt";
                SaveMatrixToFile(T_S_to_1, finalFileName);
                Debug.Log($"Transitive calibration T_{sourceNum}->{BASE_CAMERA_NUM} successfully computed and saved: {finalFileName}");
            }
            else
                Debug.LogError($"Failed to find a complete calibration path from Camera {sourceNum} to Camera {BASE_CAMERA_NUM}. Check direct calibrations.");
        }
        Debug.Log("--- Automated Transitive Calibration Sweep Finished ---");
        _requiredCalibrations.Clear();
    }

    private static Matrix4x4 TryComputePath(int sourceNum, int targetNum, Dictionary<(int, int), Matrix4x4> directTransforms, List<int> visited = null)
    {
        if (sourceNum == targetNum) 
            return Matrix4x4.identity;
        if (visited == null) 
            visited = new List<int> { sourceNum };
        if (directTransforms.TryGetValue((sourceNum, targetNum), out Matrix4x4 directMatrix))
            return directMatrix;
        var outgoingPaths = directTransforms.Keys
            .Where(k => k.Item1 == sourceNum)
            .Select(k => k.Item2)
            .ToList();
        foreach (int intermediateNum in outgoingPaths)
        {
            if (visited.Contains(intermediateNum)) 
                continue;
            Matrix4x4 T_S_to_I = directTransforms[(sourceNum, intermediateNum)];
            visited.Add(intermediateNum);
            Matrix4x4 T_I_to_T = TryComputePath(intermediateNum, targetNum, directTransforms, visited);
            visited.Remove(intermediateNum);
            if (T_I_to_T != Matrix4x4.identity)
                return T_I_to_T * T_S_to_I;
        }
        return Matrix4x4.identity;
    }

    private static Dictionary<(int, int), Matrix4x4> LoadAllDirectCalibrations(int maxCameraNum)
    {
        var transforms = new Dictionary<(int, int), Matrix4x4>();
        string directoryPath = Path.Combine(Application.dataPath, "CalibrationFiles");
        if (!Directory.Exists(directoryPath)) 
            return transforms;
        var files = Directory.GetFiles(directoryPath, "calib-*-*.txt");
        foreach (var filePath in files)
        {
            string fileName = Path.GetFileName(filePath);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
            string[] parts = nameWithoutExt.Split('-');
            if (parts.Length == 3 && int.TryParse(parts[1], out int sourceNum) && int.TryParse(parts[2], out int targetNum))
            {
                if (sourceNum <= maxCameraNum && targetNum <= maxCameraNum && sourceNum != targetNum)
                {
                    Matrix4x4 matrix = LoadMatrixFromFile(fileName);
                    if (matrix != Matrix4x4.identity)
                        transforms.TryAdd((sourceNum, targetNum), matrix);
                }
            }
        }
        return transforms;
    }

    public static Matrix4x4 LoadMatrixFromFile(string fileName)
    {
        string directoryPath = Path.Combine(Application.dataPath, "CalibrationFiles");
        string filePath = Path.Combine(directoryPath, fileName);
        Matrix4x4 matrix = Matrix4x4.identity;
        if (!File.Exists(filePath)) 
            return matrix;
        try
        {
            string[] lines = File.ReadAllLines(filePath);
            var culture = System.Globalization.CultureInfo.InvariantCulture;
            for (int i = 0; i < 4; i++)
            {
                if (i >= lines.Length) 
                    break;
                string[] values = lines[i].Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
                if (values.Length < 4) 
                    return Matrix4x4.identity;
                for (int j = 0; j < 4; j++)
                {
                    if (float.TryParse(values[j], System.Globalization.NumberStyles.Float, culture, out float val))
                        matrix[i, j] = val;
                    else
                        return Matrix4x4.identity;
                }
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"Error reading calibration file {filePath}: {ex.Message}.");
            return Matrix4x4.identity;
        }
        return matrix;
    }

    public static void SaveMatrixToFile(Matrix4x4 matrix, string fileName)
    {
        string directoryPath = Path.Combine(Application.dataPath, "CalibrationFiles");
        if (!Directory.Exists(directoryPath))
            Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        string content = "";
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < 4; i++)
        {
            content += $"{matrix[i, 0].ToString(culture)} {matrix[i, 1].ToString(culture)} {matrix[i, 2].ToString(culture)} {matrix[i, 3].ToString(culture)}";
            if (i < 3) 
                content += "\n";
        }
        File.WriteAllText(filePath, content);
        Debug.Log($"Calibration matrix saved to: {filePath}");
    }

    public static Matrix4x4 ComputeTransformationMatrix(List<Vector3> fromPoints, List<Vector3> toPoints)
    {
        if (fromPoints.Count != toPoints.Count || fromPoints.Count < 3)
        {
            Debug.LogError("Point counts must match and be >= 3 for calibration.");
            return Matrix4x4.identity;
        }
        int N = fromPoints.Count;
        
        // 1. Calculate Centroids
        Vector3 fromCentroid = Vector3.zero;
        Vector3 toCentroid = Vector3.zero;
        for (int i = 0; i < N; i++)
        {
            fromCentroid += fromPoints[i];
            toCentroid += toPoints[i];
        }
        fromCentroid /= N;
        toCentroid /= N;

        // 2. Demean data
        var fromMatrix = DenseMatrix.OfRowArrays(fromPoints.Select(p => new double[] { p.x, p.y, p.z }).ToArray());
        var toMatrix = DenseMatrix.OfRowArrays(toPoints.Select(p => new double[] { p.x, p.y, p.z }).ToArray());
        for (int i = 0; i < N; i++)
        {
            fromMatrix.SetRow(i, new double[] { fromPoints[i].x - fromCentroid.x, fromPoints[i].y - fromCentroid.y, fromPoints[i].z - fromCentroid.z });
            toMatrix.SetRow(i, new double[] { toPoints[i].x - toCentroid.x, toPoints[i].y - toCentroid.y, toPoints[i].z - toCentroid.z });
        }

        // 3. Compute Covariance Matrix H = fromMatrix.T * toMatrix
        var H = fromMatrix.Transpose() * toMatrix;

        // 4. Perform SVD on H
        var svd = H.Svd(true);
        var U = svd.U;
        var V = svd.VT.Transpose();

        // 5. Calculate Rotation Matrix R = V * U.T
        var R = V * svd.VT;

        // 6. Reflection correction
        // If det(R) is -1, it's an improper rotation (reflection). Fix by flipping the sign of the last column of V.
        if (R.Determinant() < 0)
        {
            V.SetColumn(2, V.Column(2).Multiply(-1));
            R = V * svd.VT;
        }

        // 7. Calculate Translation Vector t = toCentroid - R * fromCentroid
        var rFromCentroid = R * Vector<double>.Build.Dense(new double[] { fromCentroid.x, fromCentroid.y, fromCentroid.z });
        var t = new Vector3(
            toCentroid.x - (float)rFromCentroid[0],
            toCentroid.y - (float)rFromCentroid[1],
            toCentroid.z - (float)rFromCentroid[2]
        );

        // 8. Form 4x4 Homogeneous Transformation Matrix
        Matrix4x4 finalMatrix = Matrix4x4.identity;
        // Rotation part (3x3)
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                finalMatrix[i, j] = (float)R[i, j];
            }
        }
        // Translation part (4th column)
        finalMatrix[0, 3] = t.x;
        finalMatrix[1, 3] = t.y;
        finalMatrix[2, 3] = t.z;
        finalMatrix[3, 3] = 1f; // Homogeneous coordinate

        return finalMatrix;
    }
}