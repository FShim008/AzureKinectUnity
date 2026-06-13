using UnityEngine;
using UnityEngine.InputSystem;
using System.IO;
using System.Text;

[RequireComponent(typeof(PointCloudGenerator))]
public class PointCloudSnapshot : MonoBehaviour
{
    public Key SnapshotKey = Key.S;
    public string CameraName = "camX";

    private PointCloudGenerator generator;
    private bool captureNext = false;

    private void Awake()
    {
        generator = GetComponent<PointCloudGenerator>();
        generator.OnPointCloudGenerated += OnCloud;
    }

    // NEW: allow an external manager to request a snapshot
    public void RequestSnapshot()
    {
        captureNext = true;
        Debug.Log($"[{CameraName}] Snapshot requested (external).");
    }

    private void Update()
    {
        // Keep old behavior too (optional)
        if (Keyboard.current != null && Keyboard.current[SnapshotKey].wasPressedThisFrame)
        {
            Debug.Log($"[{CameraName}] Snapshot requested (local key).");
            captureNext = true;
        }
    }

    private void OnCloud(PointCloudData pc)
    {
        if (!captureNext || pc.Count == 0) return;
        captureNext = false;

        // Saves WORLD points (because PointCloudGenerator already ApplyDeviceTransform() before invoking event)
        string dir = Path.Combine(Application.dataPath, "Snapshots");
        Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, $"{CameraName}.xyz");
        SaveXYZ(path, pc);

        Debug.Log($"[{CameraName}] Snapshot saved: {path}");
    }

    private void SaveXYZ(string path, PointCloudData pc)
    {
        var sb = new StringBuilder(pc.Count * 32);
        for (int i = 0; i < pc.Count; i++)
        {
            Vector3 p = pc.Points[i];
            sb.AppendLine($"{p.x} {p.y} {p.z}");
        }
        File.WriteAllText(path, sb.ToString());
    }

    private void OnDestroy()
    {
        if (generator != null)
            generator.OnPointCloudGenerated -= OnCloud;
    }
}
