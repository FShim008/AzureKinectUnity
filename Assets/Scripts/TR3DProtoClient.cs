using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Streams point-cloud frames to the TR3D Python server using
/// length-delimited protobuf over TCP.  Replaces the old JSON-based
/// TR3DStreamer with faster binary serialization and latency tracking.
///
/// Protocol (matches tr3d_grpc_server.py):
///   REQUEST:  [4-byte LE msg_len] [PointCloudFrame protobuf bytes]
///   RESPONSE: [4-byte LE msg_len] [DetectionResult  protobuf bytes]
///
/// Because adding a full protobuf/gRPC NuGet into Unity is heavy,
/// we hand-serialize the two simple proto messages.  The wire format
/// is byte-compatible with the .proto definitions so the Python side
/// can use the generated pb2 module.
/// </summary>
public class TR3DProtoClient : MonoBehaviour, ITR3DBoxProvider
{
    // ── Inspector ───────────────────────────────────────────────────
    [Header("Network")]
    public string ServerAddress = "127.0.0.1";
    public int    ServerPort    = 50051;

    [Header("Source")]
    [Tooltip("Drag the PointCloudFusionSource here.")]
    public PointCloudFusionSource FusionSource;

    [Header("Rate Limiting")]
    [Range(0.1f, 30f)]
    public float MaxFPS = 5f;

    [Header("Score Filter")]
    [Range(0f, 1f)]
    [Tooltip("Discard boxes below this score on the Unity side.")]
    public float MinScore = 0.1f;

    // ── Public diagnostics ──────────────────────────────────────────
    /// <summary>True while TCP is connected.</summary>
    public bool  IsConnected        { get; private set; }
    /// <summary>Round-trip latency of last completed frame (ms).</summary>
    public float LastRoundTripMs    { get; private set; }
    /// <summary>Server-side inference time of last frame (ms).</summary>
    public float LastInferenceMs    { get; private set; }
    /// <summary>Number of frames actually sent to server.</summary>
    public int   FramesSent         { get; private set; }
    /// <summary>Number of frames dropped because network was busy.</summary>
    public int   FramesDropped      { get; private set; }

    // ── Events ──────────────────────────────────────────────────────
    public event Action<List<TR3DBoundingBox>> OnBBoxesReceived;

    // ── Internal ────────────────────────────────────────────────────
    private Thread _netThread;
    private volatile bool _running;
    private float _lastSendTime;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private ulong _nextFrameId = 1;

    private ConcurrentQueue<FramePayload>          _outQueue  = new ConcurrentQueue<FramePayload>();
    private ConcurrentQueue<List<TR3DBoundingBox>>  _inQueue   = new ConcurrentQueue<List<TR3DBoundingBox>>();

    // Pre-allocated scratch to avoid per-frame GC
    private byte[] _headerBuf = new byte[4];

    // ── Structs ─────────────────────────────────────────────────────
    private struct FramePayload
    {
        public ulong  FrameId;
        public byte[] ProtoBytes;
        public float  EnqueueTime;   // Time.realtimeSinceStartup at enqueue
    }

    // ────────────────────────────────────────────────────────────────
    // MonoBehaviour lifecycle
    // ────────────────────────────────────────────────────────────────
    private void OnEnable()
    {
        Debug.Log("[TR3DProtoClient] OnEnable called.");
        if (FusionSource == null)
        {
            Debug.LogError("[TR3DProtoClient] FusionSource is null!");
            return;
        }
        FusionSource.OnPointCloudGenerated += OnFusedCloud;

        _running = true;
        _netThread = new Thread(NetworkLoop)
        {
            IsBackground = true,
            Name = "TR3DProtoClient"
        };
        _netThread.Start();
        Debug.Log("[TR3DProtoClient] Network thread started.");
    }

    private void OnDisable()
    {
        Debug.Log("[TR3DProtoClient] OnDisable called.");
        if (FusionSource != null)
            FusionSource.OnPointCloudGenerated -= OnFusedCloud;

        _running = false;
        _netThread?.Join(1000);
    }

    private void Update()
    {
        while (_inQueue.TryDequeue(out var boxes))
        {
            OnBBoxesReceived?.Invoke(boxes);
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Point cloud callback (main thread)
    // ────────────────────────────────────────────────────────────────
    private void OnFusedCloud(PointCloudData data)
    {
        if (!_running || data.Count == 0) return;

        // Rate limit
        float now = Time.realtimeSinceStartup;
        if (now - _lastSendTime < 1f / MaxFPS) return;
        _lastSendTime = now;

        // Drop old queued frames (keep latest only)
        int dropped = 0;
        while (_outQueue.TryDequeue(out _)) dropped++;
        FramesDropped += dropped;

        // Clone + serialize
        int n = data.Count;
        ulong fid = _nextFrameId++;
        byte[] proto = SerializePointCloudFrame(fid, (uint)n, data.Points, data.Colors);

        _outQueue.Enqueue(new FramePayload
        {
            FrameId = fid,
            ProtoBytes = proto,
            EnqueueTime = now
        });
    }

    // ────────────────────────────────────────────────────────────────
    // Network thread
    // ────────────────────────────────────────────────────────────────
    private void NetworkLoop()
    {
        Debug.Log("[TR3DProtoClient] NetworkLoop entry.");
        int backoffMs = 2000;

        while (_running)
        {
            TcpClient tcp = null;
            NetworkStream ns = null;
            try
            {
                Debug.Log($"[TR3DProtoClient] Attempting connection to {ServerAddress}:{ServerPort}...");
                // Explicitly force IPv4 to match 127.0.0.1
                tcp = new TcpClient(AddressFamily.InterNetwork);
                var ar = tcp.BeginConnect(ServerAddress, ServerPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5))) // Increased to 5s
                {
                    Debug.LogError($"[TR3DProtoClient] Connect TIMEOUT to {ServerAddress}:{ServerPort}. Check SSH tunnel!");
                    tcp.Close();
                    Thread.Sleep(backoffMs);
                    backoffMs = Math.Min(backoffMs * 2, 30000);
                    continue;
                }
                tcp.EndConnect(ar);
                ns = tcp.GetStream();
                ns.ReadTimeout = 30000;
                IsConnected = true;
                backoffMs = 2000; 
                Debug.Log("[TR3DProtoClient] CONNECTED successfully!");

                // ── Send/receive loop (one frame at a time) ─────────
                while (_running && tcp.Connected)
                {
                    if (!_outQueue.TryDequeue(out var frame))
                    {
                        Thread.Sleep(5);
                        continue;
                    }

                    try
                    {
                        long sendTicks = _stopwatch.ElapsedMilliseconds;

                        // SEND: [4-byte len] [protobuf]
                        WriteUInt32LE(ns, (uint)frame.ProtoBytes.Length);
                        ns.Write(frame.ProtoBytes, 0, frame.ProtoBytes.Length);
                        ns.Flush();
                        FramesSent++;

                        // RECV: [4-byte len] [protobuf]
                        uint respLen = ReadUInt32LE(ns);
                        byte[] respBuf = ReadExact(ns, (int)respLen);

                        float rtt = _stopwatch.ElapsedMilliseconds - sendTicks;

                        // Parse response
                        var (boxes, inferMs) = ParseDetectionResult(respBuf, MinScore);
                        LastRoundTripMs = rtt;
                        LastInferenceMs = inferMs;

                        _inQueue.Enqueue(boxes);
                    }
                    catch (IOException ioEx)
                    {
                        // Network error — break out to reconnect
                        Debug.LogError($"[TR3DProtoClient] Network error: {ioEx.Message}");
                        break;
                    }
                    catch (Exception frameEx)
                    {
                        // Non-fatal per-frame error — log and continue
                        Debug.LogWarning($"[TR3DProtoClient] Frame error (continuing): {frameEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TR3DProtoClient] CONNECTION ERROR: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                IsConnected = false;
                try { ns?.Close(); } catch { }
                try { tcp?.Close(); } catch { }
            }

            if (_running)
            {
                Thread.Sleep(backoffMs);
                backoffMs = Math.Min(backoffMs * 2, 30000);
            }
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Manual protobuf serialization
    // (wire-compatible with tr3d_service.proto)
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Serialize PointCloudFrame:
    ///   field 1 (frame_id)   = varint, wire type 0, tag = 0x08
    ///   field 2 (num_points) = varint, wire type 0, tag = 0x10
    ///   field 3 (point_data) = bytes,  wire type 2, tag = 0x1A
    /// </summary>
    private byte[] SerializePointCloudFrame(ulong frameId, uint numPoints,
        List<Vector3> points, List<Color32> colors)
    {
        int n = (int)numPoints;
        int dataLen = n * 6 * 4;

        // Encode point data (XYZRGB, float32 LE)
        byte[] pointData = new byte[dataLen];
        int off = 0;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = CoordinateConvert.UnityToTR3D(points[i]);

            WriteF32(pointData, ref off, p.x);
            WriteF32(pointData, ref off, p.y);
            WriteF32(pointData, ref off, p.z);

            if (colors != null && colors.Count > i)
            {
                Color32 c = colors[i];
                WriteF32(pointData, ref off, c.r / 255f);
                WriteF32(pointData, ref off, c.g / 255f);
                WriteF32(pointData, ref off, c.b / 255f);
            }
            else
            {
                WriteF32(pointData, ref off, 1f);
                WriteF32(pointData, ref off, 1f);
                WriteF32(pointData, ref off, 1f);
            }
        }

        // Build protobuf bytes
        using (var ms = new MemoryStream())
        {
            // field 1: frame_id (varint)
            ms.WriteByte(0x08);
            WriteVarint(ms, frameId);

            // field 2: num_points (varint)
            ms.WriteByte(0x10);
            WriteVarint(ms, numPoints);

            // field 3: point_data (length-delimited bytes)
            ms.WriteByte(0x1A);
            WriteVarint(ms, (ulong)dataLen);
            ms.Write(pointData, 0, dataLen);

            return ms.ToArray();
        }
    }

    /// <summary>
    /// Parse DetectionResult:
    ///   field 1 (frame_id)          = varint, tag = 0x08
    ///   field 2 (boxes)             = repeated sub-message, tag = 0x12
    ///   field 3 (inference_time_ms) = fixed32 (float), tag = 0x1D
    /// </summary>
    private (List<TR3DBoundingBox>, float) ParseDetectionResult(byte[] buf, float minScore)
    {
        var boxes = new List<TR3DBoundingBox>();
        float inferMs = 0f;
        int pos = 0;

        while (pos < buf.Length)
        {
            uint tag = ReadVarintU32(buf, ref pos);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            switch (fieldNum)
            {
                case 1: // frame_id (varint)
                    ReadVarintU64(buf, ref pos);
                    break;
                case 2: // boxes (length-delimited sub-message)
                    int subLen = (int)ReadVarintU32(buf, ref pos);
                    var box = ParseBoundingBox3D(buf, pos, pos + subLen);
                    pos += subLen;
                    if (box.score >= minScore)
                        boxes.Add(box);
                    break;
                case 3: // inference_time_ms (wire type 5 = fixed32)
                    inferMs = BitConverter.ToSingle(buf, pos);
                    pos += 4;
                    break;
                default:
                    SkipField(buf, ref pos, wireType);
                    break;
            }
        }

        return (boxes, inferMs);
    }

    private TR3DBoundingBox ParseBoundingBox3D(byte[] buf, int start, int end)
    {
        var box = new TR3DBoundingBox();
        int pos = start;
        // Raw TR3D-space values
        float rawCx = 0, rawCy = 0, rawCz = 0;
        float rawSx = 0, rawSy = 0, rawSz = 0;
        float rawYaw = 0;

        while (pos < end)
        {
            uint tag = ReadVarintU32(buf, ref pos);
            int fieldNum = (int)(tag >> 3);
            int wireType = (int)(tag & 0x7);

            switch (fieldNum)
            {
                case 1: box.label = (int)ReadVarintU32(buf, ref pos); break;
                case 2: // class_name (string, length-delimited)
                    int slen = (int)ReadVarintU32(buf, ref pos);
                    box.className = System.Text.Encoding.UTF8.GetString(buf, pos, slen);
                    pos += slen;
                    break;
                case 3:  box.score = BitConverter.ToSingle(buf, pos); pos += 4; break;
                case 4:  rawCx = BitConverter.ToSingle(buf, pos); pos += 4; break;
                case 5:  rawCy = BitConverter.ToSingle(buf, pos); pos += 4; break;
                case 6:  rawCz = BitConverter.ToSingle(buf, pos); pos += 4; break;
                case 7:  rawSx = BitConverter.ToSingle(buf, pos); pos += 4; break;
                case 8:  rawSy = BitConverter.ToSingle(buf, pos); pos += 4; break;
                case 9:  rawSz = BitConverter.ToSingle(buf, pos); pos += 4; break;
                case 10: rawYaw = BitConverter.ToSingle(buf, pos); pos += 4; break;
                default: SkipField(buf, ref pos, wireType); break;
            }
        }

        // Convert from TR3D space to Unity space
        CoordinateConvert.TR3DBoxToUnity(
            rawCx, rawCy, rawCz,
            rawSx, rawSy, rawSz,
            rawYaw,
            out Vector3 center, out Vector3 size, out float unityYaw);

        box.cx = center.x; box.cy = center.y; box.cz = center.z;
        box.sx = size.x;   box.sy = size.y;   box.sz = size.z;
        box.yaw = unityYaw;

        return box;
    }

    // ────────────────────────────────────────────────────────────────
    // Protobuf encoding helpers
    // ────────────────────────────────────────────────────────────────
    private static void WriteVarint(MemoryStream ms, ulong value)
    {
        while (value > 0x7F)
        {
            ms.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        ms.WriteByte((byte)value);
    }

    private static uint ReadVarintU32(byte[] buf, ref int pos)
    {
        uint result = 0;
        int shift = 0;
        while (pos < buf.Length)
        {
            byte b = buf[pos++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    private static ulong ReadVarintU64(byte[] buf, ref int pos)
    {
        ulong result = 0;
        int shift = 0;
        while (pos < buf.Length)
        {
            byte b = buf[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    private static void SkipField(byte[] buf, ref int pos, int wireType)
    {
        switch (wireType)
        {
            case 0: ReadVarintU64(buf, ref pos); break;          // varint
            case 1: pos += 8; break;                             // 64-bit
            case 2: int len = (int)ReadVarintU32(buf, ref pos); pos += len; break; // length-delimited
            case 5: pos += 4; break;                             // 32-bit
        }
    }

    // ────────────────────────────────────────────────────────────────
    // TCP / byte helpers
    // ────────────────────────────────────────────────────────────────
    private static void WriteF32(byte[] buf, ref int off, float v)
    {
        byte[] b = BitConverter.GetBytes(v);
        if (!BitConverter.IsLittleEndian) Array.Reverse(b);
        Buffer.BlockCopy(b, 0, buf, off, 4);
        off += 4;
    }

    private void WriteUInt32LE(NetworkStream ns, uint value)
    {
        _headerBuf[0] = (byte)(value);
        _headerBuf[1] = (byte)(value >> 8);
        _headerBuf[2] = (byte)(value >> 16);
        _headerBuf[3] = (byte)(value >> 24);
        ns.Write(_headerBuf, 0, 4);
    }

    private uint ReadUInt32LE(NetworkStream ns)
    {
        byte[] buf = ReadExact(ns, 4);
        return (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));
    }

    private byte[] ReadExact(NetworkStream ns, int count)
    {
        byte[] buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int r = ns.Read(buf, read, count - read);
            if (r == 0) throw new IOException("Connection closed reading response");
            read += r;
        }
        return buf;
    }
}
