using UnityEngine;
using UnityEngine.InputSystem;

public class MultiCamSnapshotManager : MonoBehaviour
{
    public Key snapshotAllKey = Key.S;

    [Tooltip("If true, will print how many snapshot components were found.")]
    public bool verbose = true;

    private PointCloudSnapshot[] _snapshots;

    private void Start()
    {
        _snapshots = FindObjectsByType<PointCloudSnapshot>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (verbose)
            Debug.Log($"[MultiCamSnapshotManager] Found {_snapshots.Length} PointCloudSnapshot components.");
    }

    private void Update()
    {
        if (Keyboard.current == null) return;

        if (Keyboard.current[snapshotAllKey].wasPressedThisFrame)
        {
            if (_snapshots == null || _snapshots.Length == 0)
                _snapshots = FindObjectsByType<PointCloudSnapshot>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var s in _snapshots)
                s.RequestSnapshot();

            Debug.Log($"[MultiCamSnapshotManager] Snapshot triggered for {_snapshots.Length} cameras.");
        }
    }
}
