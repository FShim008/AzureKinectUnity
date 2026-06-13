using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class TR3DStreamer : MonoBehaviour, ITR3DBoxProvider
{
    [Header("Network")]
    public string ServerAddress = "127.0.0.1";
    public int ServerPort = 9999;
    
    [Header("Sources")]
    [Tooltip("The source of the fused point cloud.")]
    public PointCloudFusionSource FusionSource;

    [Header("Coordinate System")]
    [Tooltip("True = Convert Unity Y-up to Z-up for TR3D streaming. False = Leave as Y-up.")]
    public bool SwapYZForStreaming = true;

    [Header("Rate Limiting")]
    [Tooltip("Target streaming rate in frames per second.")]
    [Range(0.1f, 30f)]
    public float MaxFPS = 5f;
    private float _lastSendTime = 0f;

    [Tooltip("Only send every N-th point. 1 = all points, 5 = 20% of points. Boosts performance significantly.")]
    [Range(1, 20)]
    public int StreamDownsampleStride = 5;

    // Threading
    private Thread _networkThread;
    private bool _isRunning = false;
    
    // Concurrent queues
    private ConcurrentQueue<PointCloudData> _pendingClouds = new ConcurrentQueue<PointCloudData>();
    private ConcurrentQueue<List<TR3DBoundingBox>> _receivedBBoxes = new ConcurrentQueue<List<TR3DBoundingBox>>();

    // Output Event for Visualizers
    public event Action<List<TR3DBoundingBox>> OnBBoxesReceived;

    private void OnEnable()
    {
        Debug.Log("[TR3DStreamer] OnEnable called. Starting network thread...");

        if (FusionSource != null)
        {
            FusionSource.OnPointCloudGenerated += OnFusedCloud;
            Debug.Log("[TR3DStreamer] Subscribed to FusionSource.");
        }
        else
        {
            Debug.LogError("[TR3DStreamer] FusionSource is null! Drag the FusionPointCloud object into the Inspector.");
        }

        _isRunning = true;
        _networkThread = new Thread(NetworkThreadLoop)
        {
            IsBackground = true,
            Name = "TR3D Network Thread"
        };
        _networkThread.Start();
    }

    private void OnDisable()
    {
        Debug.Log("[TR3DStreamer] OnDisable called. Stopping network thread...");

        if (FusionSource != null)
        {
            FusionSource.OnPointCloudGenerated -= OnFusedCloud;
        }

        _isRunning = false;
        if (_networkThread != null && _networkThread.IsAlive)
        {
            _networkThread.Join(500);
        }
    }

    private void Update()
    {
        // Drain the BBox queue on the main thread and fire events
        while (_receivedBBoxes.TryDequeue(out var bboxes))
        {
            Debug.Log($"[TR3DStreamer] Received {bboxes.Count} bounding boxes from server.");
            OnBBoxesReceived?.Invoke(bboxes);
        }
    }

    private void OnFusedCloud(PointCloudData data)
    {
        if (!_isRunning) return;

        // Rate limit
        if (Time.time - _lastSendTime < 1f / MaxFPS)
        {
            return;
        }

        // We only keep the latest frame. If network is slow, drop old frames.
        while (_pendingClouds.TryDequeue(out _)) { }

        // Clone the arrays because the network thread will read them while Unity might overwrite them
        var clone = new PointCloudData
        {
            Count = data.Count,
            Points = new List<Vector3>(data.Points),
            Colors = new List<Color32>(data.Colors)
        };
        
        _pendingClouds.Enqueue(clone);
        _lastSendTime = Time.time;
    }

    private void NetworkThreadLoop()
    {
        TcpClient client = null;
        NetworkStream stream = null;

        try
        {
            while (_isRunning)
            {
                // Try to connect if not connected
                if (client == null || !client.Connected)
                {
                    try
                    {
                        client = new TcpClient();
                        var result = client.BeginConnect(ServerAddress, ServerPort, null, null);
                        if (!result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
                        {
                            Debug.LogWarning($"[TR3DStreamer] Connection timed out to {ServerAddress}:{ServerPort}. Is the Python server running?");
                            client.Close();
                            client = null;
                        }
                        else
                        {
                            client.EndConnect(result);
                            stream = client.GetStream();
                            stream.ReadTimeout = 10000;
                            Debug.Log("[TR3DStreamer] Connected to Python server!");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[TR3DStreamer] Failed to connect: {ex.Message}");
                        if (client != null) { client.Close(); client = null; }
                    }

                    if (client == null || !client.Connected)
                    {
                        Thread.Sleep(2000); // retry every 2 seconds
                        continue;
                    }
                }

                // Connected. Wait for a point cloud.
                if (_pendingClouds.TryDequeue(out var cloud))
                {
                    int n = cloud.Count;
                    if (n == 0) continue;

                    try
                    {
                        // Header (4 bytes): Payload size in bytes
                        int stride = Mathf.Max(1, StreamDownsampleStride);
                        int pointsToSend = n / stride;
                        int actualPayloadSize = pointsToSend * 6 * 4;

                        byte[] payload = new byte[4 + actualPayloadSize];
                        byte[] szBytes = BitConverter.GetBytes(actualPayloadSize);
                        if (!BitConverter.IsLittleEndian) Array.Reverse(szBytes);
                        Buffer.BlockCopy(szBytes, 0, payload, 0, 4);

                        // Body
                        int offset = 4;
                        for (int i = 0; i < n; i += stride)
                        {
                            if (offset + 24 > payload.Length) break;

                            Vector3 p = cloud.Points[i];
                            Color32 c = cloud.Colors[i];

                            if (SwapYZForStreaming)
                            {
                                float tempY = p.y;
                                p.y = p.z;
                                p.z = -tempY;
                            }

                            WriteFloatLE(p.x, payload, ref offset);
                            WriteFloatLE(p.y, payload, ref offset);
                            WriteFloatLE(p.z, payload, ref offset);
                            
                            WriteFloatLE(c.r / 255f, payload, ref offset);
                            WriteFloatLE(c.g / 255f, payload, ref offset);
                            WriteFloatLE(c.b / 255f, payload, ref offset);
                        }

                        // Send
                        Debug.Log($"[TR3DStreamer] Sending {pointsToSend} points ({actualPayloadSize} bytes)...");
                        stream.Write(payload, 0, payload.Length);

                        // 2. Wait for JSON response (newline terminated)
                        using (var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true))
                        {
                            string response = reader.ReadLine();
                            if (string.IsNullOrEmpty(response))
                            {
                                throw new EndOfStreamException("Server closed connection or sent empty line.");
                            }

                            // 3. Parse JSON
                            var inferenceResult = JsonUtility.FromJson<TR3DInferenceResult>(response);
                            if (inferenceResult != null && inferenceResult.boxes != null)
                            {
                                _receivedBBoxes.Enqueue(inferenceResult.boxes);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[TR3DStreamer] Connection dropped: {ex.Message}");
                        client.Close();
                        client = null;
                        stream = null;
                    }
                }
                else
                {
                    Thread.Sleep(5); // Nothing to do, chill.
                }
            }
        }
        finally
        {
            if (stream != null) stream.Close();
            if (client != null) client.Close();
        }
    }

    private void WriteFloatLE(float value, byte[] buffer, ref int offset)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
        Buffer.BlockCopy(bytes, 0, buffer, offset, 4);
        offset += 4;
    }
}

// Data structures for JSON parsing
[Serializable]
public class TR3DInferenceResult
{
    public List<TR3DBoundingBox> boxes;
}

[Serializable]
public class TR3DBoundingBox
{
    public int label;
    public string className;
    public float score;
    public float cx, cy, cz;
    public float sx, sy, sz;
    public float yaw;
}
