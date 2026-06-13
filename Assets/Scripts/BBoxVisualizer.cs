using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Renders 3D oriented bounding boxes from any ITR3DBoxProvider source
/// (TR3DStreamer legacy or TR3DProtoClient new pipeline).
///
/// Uses CoordinateConvert for consistent box placement.
/// Boxes arrive PRE-CONVERTED to Unity space by the provider,
/// so no additional Y/Z swapping is needed here.
/// </summary>
public class BBoxVisualizer : MonoBehaviour
{
    [Header("Box Source")]
    [Tooltip("Drag any GameObject that has TR3DStreamer or TR3DProtoClient.")]
    public MonoBehaviour BoxSourceComponent;

    [Header("Rendering")]
    public Material BoxMaterial;
    public float LineWidth = 0.02f;

    [Tooltip("Show class labels above each box.")]
    public bool ShowLabels = true;

    [Tooltip("If the source is old TR3DStreamer (which does NOT pre-convert coords), enable this.")]
    public bool LegacyUnswapYZ = false;

    private ITR3DBoxProvider _boxProvider;
    private List<TR3DBoundingBox> _latestBoxes = new List<TR3DBoundingBox>();
    private readonly object _lock = new object();
    private Camera _mainCam;
    private GUIStyle _labelStyle;
    private GUIStyle _shadowStyle;

    private void OnEnable()
    {
        _mainCam = Camera.main;

        if (BoxSourceComponent == null)
        {
            Debug.LogError("[BBoxVisualizer] BoxSourceComponent is null!");
            return;
        }

        _boxProvider = BoxSourceComponent as ITR3DBoxProvider;
        if (_boxProvider == null)
        {
            Debug.LogError($"[BBoxVisualizer] {BoxSourceComponent.name} does not implement ITR3DBoxProvider!");
            return;
        }

        _boxProvider.OnBBoxesReceived += HandleNewBoxes;
    }

    private void OnDisable()
    {
        if (_boxProvider != null)
            _boxProvider.OnBBoxesReceived -= HandleNewBoxes;
    }

    private void HandleNewBoxes(List<TR3DBoundingBox> boxes)
    {
        lock (_lock)
        {
            _latestBoxes = boxes;
        }
    }

    private void OnRenderObject()
    {
        if (BoxMaterial == null) return;

        List<TR3DBoundingBox> boxesToDraw;
        lock (_lock)
        {
            if (_latestBoxes == null || _latestBoxes.Count == 0) return;
            boxesToDraw = new List<TR3DBoundingBox>(_latestBoxes);
        }

        try
        {
            BoxMaterial.SetPass(0);
            GL.PushMatrix();
            // Force rendering in absolute world space (points from AI are already absolute)
            GL.MultMatrix(Matrix4x4.identity);
            GL.Begin(GL.LINES);

            foreach (var box in boxesToDraw)
            {
                GL.Color(GetColorForLabel(box.label));
                DrawBox(box);
            }

            GL.End();
            GL.PopMatrix();
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[BBoxVisualizer] OnRenderObject error (safe to ignore during load): {ex.Message}");
        }
    }

    private void DrawBox(TR3DBoundingBox box)
    {
        Vector3 center = new Vector3(box.cx, box.cy, box.cz);
        Vector3 size   = new Vector3(box.sx, box.sy, box.sz);

        // Legacy path: old TR3DStreamer sends raw TR3D coords, needs un-swap
        if (LegacyUnswapYZ)
        {
            float tempY = center.y;
            center.y = -center.z;
            center.z = tempY;

            float tempSizeY = size.y;
            size.y = size.z;
            size.z = tempSizeY;
        }

        Quaternion rotation = Quaternion.Euler(0, box.yaw * Mathf.Rad2Deg, 0);
        Vector3 extents = size * 0.5f;

        Vector3[] corners = new Vector3[8];
        corners[0] = center + rotation * new Vector3( extents.x,  extents.y,  extents.z);
        corners[1] = center + rotation * new Vector3(-extents.x,  extents.y,  extents.z);
        corners[2] = center + rotation * new Vector3(-extents.x,  extents.y, -extents.z);
        corners[3] = center + rotation * new Vector3( extents.x,  extents.y, -extents.z);
        corners[4] = center + rotation * new Vector3( extents.x, -extents.y,  extents.z);
        corners[5] = center + rotation * new Vector3(-extents.x, -extents.y,  extents.z);
        corners[6] = center + rotation * new Vector3(-extents.x, -extents.y, -extents.z);
        corners[7] = center + rotation * new Vector3( extents.x, -extents.y, -extents.z);

        // Top face
        GLLine(corners[0], corners[1]); GLLine(corners[1], corners[2]);
        GLLine(corners[2], corners[3]); GLLine(corners[3], corners[0]);
        // Bottom face
        GLLine(corners[4], corners[5]); GLLine(corners[5], corners[6]);
        GLLine(corners[6], corners[7]); GLLine(corners[7], corners[4]);
        // Verticals
        GLLine(corners[0], corners[4]); GLLine(corners[1], corners[5]);
        GLLine(corners[2], corners[6]); GLLine(corners[3], corners[7]);
    }

    /// <summary>Draw class labels in screen space above each box.</summary>
    private void OnGUI()
    {
        if (!ShowLabels || _mainCam == null) return;

        List<TR3DBoundingBox> boxesToDraw;
        lock (_lock)
        {
            if (_latestBoxes == null || _latestBoxes.Count == 0) return;
            boxesToDraw = new List<TR3DBoundingBox>(_latestBoxes);
        }

        try
        {
            // Cache the GUIStyle to avoid recreating every frame
            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 14,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter
                };
                _shadowStyle = new GUIStyle(_labelStyle);
            }

            foreach (var box in boxesToDraw)
            {
                Vector3 worldPos = new Vector3(box.cx, box.cy + box.sy * 0.5f + 0.1f, box.cz);
                if (LegacyUnswapYZ)
                {
                    float ty = worldPos.y;
                    worldPos.y = -worldPos.z;
                    worldPos.z = ty;
                }

                Vector3 screenPos = _mainCam.WorldToScreenPoint(worldPos);
                if (screenPos.z < 0) continue; // behind camera

                float guiY = Screen.height - screenPos.y;
                string label = string.IsNullOrEmpty(box.className)
                    ? $"Class {box.label}"
                    : $"{box.className}";
                string text = $"{label} ({box.score:F2})";

                // Drop shadow
                _shadowStyle.normal.textColor = Color.black;
                GUI.Label(new Rect(screenPos.x - 49, guiY - 9, 100, 20), text, _shadowStyle);

                // Foreground
                _labelStyle.normal.textColor = GetColorForLabel(box.label);
                GUI.Label(new Rect(screenPos.x - 50, guiY - 10, 100, 20), text, _labelStyle);
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[BBoxVisualizer] OnGUI error (safe to ignore during load): {ex.Message}");
        }
    }

    private void GLLine(Vector3 a, Vector3 b)
    {
        GL.Vertex(a);
        GL.Vertex(b);
    }

    private Color GetColorForLabel(int label)
    {
        float h = (label * 0.618033988749895f) % 1.0f;
        return Color.HSVToRGB(h, 0.8f, 0.9f);
    }
}
