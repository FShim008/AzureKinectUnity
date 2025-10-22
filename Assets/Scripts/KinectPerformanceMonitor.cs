using UnityEngine;

public class KinectPerformanceMonitor : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private SkeletonTracker skeletonTracker;
    [SerializeField] private PersonMeshRenderer personMeshRenderer;

    [Header("Display")]
    [SerializeField] private bool showOnScreen = true;
    [SerializeField] private int fontSize = 16;

    private int skeletonFrames = 0;
    private int meshFrames = 0;

    private float updateInterval = 1.0f;
    private float timer = 0f;

    private float unityFPS = 0f;
    private float skeletonFPS = 0f;
    private float meshFPS = 0f;

    private long lastMemory = 0;
    private float memoryMB = 0f;

    void Start()
    {
        if (skeletonTracker != null)
            skeletonTracker.OnSkeletonsUpdated += _ => skeletonFrames++;

        if (personMeshRenderer != null)
            personMeshRenderer.OnMeshesUpdated += () => meshFrames++;
    }

    void Update()
    {
        timer += Time.deltaTime;

        if (timer >= updateInterval)
        {
            unityFPS = 1.0f / Time.smoothDeltaTime;
            skeletonFPS = skeletonFrames / timer;
            meshFPS = meshFrames / timer;

            long currentMemory = System.GC.GetTotalMemory(false);
            memoryMB = currentMemory / (1024f * 1024f);

            skeletonFrames = 0;
            meshFrames = 0;
            timer = 0;
            lastMemory = currentMemory;
        }
    }

    void OnGUI()
    {
        if (!showOnScreen) return;

        GUI.skin.label.fontSize = fontSize;

        GUILayout.BeginArea(new Rect(10, 10, 320, 180));
        GUILayout.Box("KINECT PERFORMANCE", GUILayout.Width(310));

        GUILayout.Label($"Unity FPS: {unityFPS:F1}");
        GUILayout.Label($"Skeleton FPS: {skeletonFPS:F1}");
        GUILayout.Label($"Mesh FPS: {meshFPS:F1}");
        GUILayout.Space(8);
        GUILayout.Label($"Frame Time: {(Time.deltaTime * 1000):F1} ms");
        GUILayout.Label($"Memory: {memoryMB:F1} MB");

        if (unityFPS < 15)
            GUILayout.Label("<color=red>⚠ Low Unity FPS!</color>");
        if (memoryMB > 500)
            GUILayout.Label("<color=yellow>⚠ High Memory Usage</color>");

        GUILayout.EndArea();
    }
}
