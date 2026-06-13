using System;
using System.Collections.Generic;

/// <summary>
/// Common interface for any component that produces TR3D bounding boxes,
/// allowing BBoxVisualizer (and other consumers) to work with either
/// TR3DStreamer (legacy JSON/TCP) or TR3DProtoClient (new protobuf/TCP).
/// </summary>
public interface ITR3DBoxProvider
{
    event Action<List<TR3DBoundingBox>> OnBBoxesReceived;
}
