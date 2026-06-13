using UnityEngine;

/// <summary>
/// Debug overlay displaying TR3DProtoClient streaming diagnostics.
/// Attach to any GameObject in the scene.
/// </summary>
public class TR3DDiagnosticsUI : MonoBehaviour
{
    [Tooltip("The TR3DProtoClient to monitor.")]
    public TR3DProtoClient Client;

    [Tooltip("Show the overlay (toggle with F3).")]
    public bool ShowOverlay = true;

    private GUIStyle _bgStyle;
    private GUIStyle _textStyle;
    private GUIStyle _headerStyle;

    private void Update()
    {
#if ENABLE_INPUT_SYSTEM
        if (UnityEngine.InputSystem.Keyboard.current != null &&
            UnityEngine.InputSystem.Keyboard.current.f3Key.wasPressedThisFrame)
            ShowOverlay = !ShowOverlay;
#endif
    }

    private void OnGUI()
    {
        if (!ShowOverlay || Client == null) return;

        // Lazy init styles
        if (_bgStyle == null)
        {
            _bgStyle = new GUIStyle(GUI.skin.box);
            var bg = new Texture2D(1, 1);
            bg.SetPixel(0, 0, new Color(0, 0, 0, 0.7f));
            bg.Apply();
            _bgStyle.normal.background = bg;

            _textStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                richText = true
            };
            _textStyle.normal.textColor = Color.white;

            _headerStyle = new GUIStyle(_textStyle)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold
            };
        }

        float w = 280, h = 160;
        Rect area = new Rect(10, 10, w, h);

        GUI.Box(area, GUIContent.none, _bgStyle);
        GUILayout.BeginArea(new Rect(area.x + 8, area.y + 6, area.width - 16, area.height - 12));

        GUILayout.Label("TR3D Diagnostics", _headerStyle);

        string connColor = Client.IsConnected ? "#00FF88" : "#FF4444";
        string connText  = Client.IsConnected ? "CONNECTED" : "DISCONNECTED";
        GUILayout.Label($"<color={connColor}>● {connText}</color>", _textStyle);

        GUILayout.Label($"Round-trip:  <b>{Client.LastRoundTripMs:F1} ms</b>", _textStyle);
        GUILayout.Label($"Inference:   <b>{Client.LastInferenceMs:F1} ms</b>", _textStyle);
        GUILayout.Label($"Frames sent: <b>{Client.FramesSent}</b>  |  Dropped: <b>{Client.FramesDropped}</b>", _textStyle);
        GUILayout.Label($"Server:      <b>{Client.ServerAddress}:{Client.ServerPort}</b>", _textStyle);

        GUILayout.EndArea();
    }
}
