using System;
using Microsoft.Azure.Kinect.Sensor;

public class KinectFrameData : IDisposable
{
    public Capture Capture;
    public byte[] DepthData;
    public byte[] ColorData;
    public int DepthWidth;
    public int DepthHeight;
    public int ColorWidth;
    public int ColorHeight;
    public int DepthStride;
    public int ColorStride;
    public DateTime Timestamp;

    private bool disposed = false;

    public void Dispose()
    {
        if (!disposed)
        {
            Capture?.Dispose();
            Capture = null;
            disposed = true;
        }
    }
}