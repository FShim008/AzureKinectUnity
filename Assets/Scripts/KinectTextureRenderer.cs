using UnityEngine;
using UnityEngine.UI;
using K4AdotNet.Sensor;
using System;

public class KinectTextureRenderer : MonoBehaviour
{
    [Tooltip("The KinectDevice component providing the capture stream.")]
    [SerializeField] private KinectDevice deviceComponent;

    [Tooltip("UI RawImage to display the Color Camera feed.")]
    public RawImage colorOutputUI;

    [Tooltip("UI RawImage to display the Depth feed.")]
    public RawImage depthOutputUI;

    [Header("Depth Visualization Settings")]
    [Tooltip("Depth value (in mm) used for the white clipping point (e.g., 5000mm = 5m).")]
    public ushort maxDepthVisualization = 5000;

    private Texture2D _colorTexture;
    private Texture2D _depthTexture;

    private byte[] _depthDisplayBuffer;
    private byte[] _colorDisplayBuffer;
    private short[] _depthRawBuffer;

    private void Start()
    {
        if (deviceComponent == null)
        {
            deviceComponent = GetComponent<KinectDevice>();
            if (deviceComponent == null)
            {
                Debug.LogError($"[{nameof(KinectTextureRenderer)}] Missing required {nameof(KinectDevice)} reference.");
                enabled = false;
                return;
            }
        }
        deviceComponent.OnCaptureReady += OnCaptureReady;
        StartCoroutine(InitializeTexturesAsync());
    }

    private System.Collections.IEnumerator InitializeTexturesAsync()
    {
        yield return new WaitUntil(() => deviceComponent.IsInitialized);

        var config = deviceComponent.Configuration;
        int colorWidth = ColorResolutions.WidthPixels(config.ColorResolution);
        int colorHeight = ColorResolutions.HeightPixels(config.ColorResolution);
        int depthWidth = DepthModes.WidthPixels(config.DepthMode);
        int depthHeight = DepthModes.HeightPixels(config.DepthMode);

        _colorTexture = new Texture2D(colorWidth, colorHeight, TextureFormat.BGRA32, false, true);
        if (colorOutputUI != null)
        {
            colorOutputUI.texture = _colorTexture;
            colorOutputUI.SetNativeSize();
            colorOutputUI.rectTransform.localScale = new Vector3(1f, -1f, 1f);
        }
        _colorDisplayBuffer = new byte[colorWidth * colorHeight * 4];

        _depthTexture = new Texture2D(depthWidth, depthHeight, TextureFormat.R8, false, true);
        if (depthOutputUI != null)
        {
            depthOutputUI.texture = _depthTexture;
            depthOutputUI.SetNativeSize();
            depthOutputUI.rectTransform.localScale = new Vector3(1f, -1f, 1f);
        }
        _depthDisplayBuffer = new byte[depthWidth * depthHeight];
        _depthRawBuffer = new short[_depthDisplayBuffer.Length];

        Debug.Log($"[{nameof(KinectTextureRenderer)}] Textures initialized. Color: {colorWidth}x{colorHeight}, Depth: {depthWidth}x{depthHeight}.");
    }

    private void OnCaptureReady(object sender, CaptureEventArgs e)
    {
        using (var capture = e.Capture.DuplicateReference())
        {
            RenderColorImage(capture.ColorImage);
            RenderDepthImage(capture.DepthImage);
        }
    }

    private void RenderColorImage(K4AdotNet.Sensor.Image colorImage)
    {
        if (_colorTexture == null || colorImage == null) 
            return;
        try
        {
            colorImage.CopyTo(_colorDisplayBuffer);
            _colorTexture.LoadRawTextureData(_colorDisplayBuffer);
            _colorTexture.Apply();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to render Color Image: {ex.Message}");
        }
    }

    private void RenderDepthImage(K4AdotNet.Sensor.Image depthImage)
    {
        if (_depthTexture == null || depthImage == null || _depthDisplayBuffer == null) 
            return;
        try
        {
            depthImage.CopyTo(_depthRawBuffer);
            for (int i = 0; i < _depthRawBuffer.Length; i++)
            {
                short depthValue = _depthRawBuffer[i];
                if (depthValue == 0)
                    _depthDisplayBuffer[i] = 0; // Black for invalid/unknown depth
                else
                {
                    float normalized = Mathf.Clamp01((float)depthValue / maxDepthVisualization);
                    _depthDisplayBuffer[i] = (byte)((1.0f - normalized) * 255);
                }
            }
            _depthTexture.LoadRawTextureData(_depthDisplayBuffer);
            _depthTexture.Apply();
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to render Depth Image: {ex.Message}");
        }
    }

    private void OnDestroy()
    {
        if (deviceComponent != null)
            deviceComponent.OnCaptureReady -= OnCaptureReady;
        if (_colorTexture != null)
            Destroy(_colorTexture);
        if (_depthTexture != null) 
            Destroy(_depthTexture);
    }
}