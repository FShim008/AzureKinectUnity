using UnityEngine;
using Microsoft.Azure.Kinect.Sensor;
using System;
using System.Threading.Tasks;

public class KinectDataCapture : MonoBehaviour
{
    [Header("Device Settings")]
    [SerializeField] private int deviceIndex = 0;
    [SerializeField] private DepthMode depthMode = DepthMode.NFOV_Unbinned;
    [SerializeField] private ColorResolution colorResolution = ColorResolution.R720p; // CHANGED: Lower resolution
    [SerializeField] private FPS frameRate = FPS.FPS30;

    public event Action<KinectFrameData> OnFrameReceived;

    private Device kinect;
    private Calibration calibration;
    private Transformation transformation;
    private bool isCapturing = false;

    private KinectFrameData latestFrame = null;
    private readonly object frameLock = new object();

    // Reusable buffers to reduce allocations
    private byte[] depthBuffer = null;
    private byte[] colorBuffer = null;

    public Calibration GetCalibration() => calibration;
    public Transformation GetTransformation() => transformation;
    public Device GetDevice() => kinect;

    void Awake()
    {
        InitializeKinect();
    }

    void InitializeKinect()
    {
        try
        {
            kinect = Device.Open(deviceIndex);

            var config = new DeviceConfiguration
            {
                ColorFormat = ImageFormat.ColorBGRA32,
                ColorResolution = colorResolution,
                DepthMode = depthMode,
                SynchronizedImagesOnly = true,
                CameraFPS = frameRate
            };

            kinect.StartCameras(config);
            calibration = kinect.GetCalibration(depthMode, colorResolution);
            transformation = calibration.CreateTransformation();

            // Pre-allocate buffers based on resolution
            int depthSize = calibration.DepthCameraCalibration.ResolutionWidth *
                           calibration.DepthCameraCalibration.ResolutionHeight * 2;
            int colorSize = calibration.ColorCameraCalibration.ResolutionWidth *
                           calibration.ColorCameraCalibration.ResolutionHeight * 4;

            depthBuffer = new byte[depthSize];
            colorBuffer = new byte[colorSize];

            Debug.Log($"Kinect initialized: Depth {calibration.DepthCameraCalibration.ResolutionWidth}x{calibration.DepthCameraCalibration.ResolutionHeight}");
            Debug.Log($"Pre-allocated buffers: Depth={depthSize / 1024}KB, Color={colorSize / 1024}KB");

            StartCapture();
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to initialize Kinect: {e.Message}");
        }
    }

    async void StartCapture()
    {
        isCapturing = true;
        await Task.Run(() => CaptureLoop());
    }

    async Task CaptureLoop()
    {
        while (isCapturing)
        {
            try
            {
                using (Capture capture = kinect.GetCapture(TimeSpan.FromMilliseconds(1000)))
                {
                    if (capture.Depth != null && capture.Color != null)
                    {
                        // Reuse buffers instead of allocating new ones
                        int depthBytes = capture.Depth.Memory.Length;
                        int colorBytes = capture.Color.Memory.Length;

                        if (depthBytes <= depthBuffer.Length && colorBytes <= colorBuffer.Length)
                        {
                            capture.Depth.Memory.Span.CopyTo(depthBuffer);
                            capture.Color.Memory.Span.CopyTo(colorBuffer);

                            var frameData = new KinectFrameData
                            {
                                Capture = capture.Reference(),
                                DepthData = depthBuffer,
                                ColorData = colorBuffer,
                                DepthWidth = capture.Depth.WidthPixels,
                                DepthHeight = capture.Depth.HeightPixels,
                                ColorWidth = capture.Color.WidthPixels,
                                ColorHeight = capture.Color.HeightPixels,
                                DepthStride = capture.Depth.StrideBytes,
                                ColorStride = capture.Color.StrideBytes,
                                Timestamp = DateTime.Now
                            };

                            // Only keep the latest frame - drop old one
                            lock (frameLock)
                            {
                                latestFrame?.Dispose();
                                latestFrame = frameData;
                            }
                        }
                    }
                }

                // Small delay to prevent CPU spinning
                await Task.Delay(1);
            }
            catch (Exception e)
            {
                Debug.LogError($"Capture error: {e.Message}");
                await Task.Delay(100); // Longer delay on error
            }
        }
    }

    void Update()
    {
        // Process only the latest frame on the main thread
        KinectFrameData frameToProcess = null;

        lock (frameLock)
        {
            if (latestFrame != null)
            {
                frameToProcess = latestFrame;
                latestFrame = null;
            }
        }

        if (frameToProcess != null)
        {
            OnFrameReceived?.Invoke(frameToProcess);
            // Don't dispose here - let the frame live until next update
        }
    }

    void OnDestroy()
    {
        isCapturing = false;

        if (kinect != null)
        {
            kinect.StopCameras();
            kinect.Dispose();
        }

        transformation?.Dispose();

        lock (frameLock)
        {
            latestFrame?.Dispose();
            latestFrame = null;
        }

        depthBuffer = null;
        colorBuffer = null;
    }
}