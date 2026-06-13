using UnityEngine;

/// <summary>
/// Centralized coordinate conversion between Unity (Y-up, left-handed)
/// and TR3D / SUNRGBD (Z-up, right-handed, DEPTH convention).
///
/// Unity:   X-right, Y-up,   Z-forward   (left-handed)
/// SUNRGBD: X-right, Y-forward, Z-up     (right-handed, DEPTH)
///
/// Mapping:
///   Unity X  →  TR3D X
///   Unity Y  →  TR3D Z      (Unity up  → TR3D up)
///   Unity Z  →  TR3D Y      (Unity fwd → TR3D fwd)
///   Negate Z when going Unity→TR3D to fix handedness.
/// </summary>
public static class CoordinateConvert
{
    /// <summary>
    /// Convert a point from Unity world space to TR3D/SUNRGBD DEPTH space.
    /// Used when SENDING points to the server.
    /// </summary>
    public static Vector3 UnityToTR3D(Vector3 p)
    {
        return new Vector3(p.x, p.z, p.y);
    }

    /// <summary>
    /// Convert a point from TR3D/SUNRGBD DEPTH space to Unity world space.
    /// Used when RECEIVING box centers from the server.
    /// </summary>
    public static Vector3 TR3DToUnity(Vector3 p)
    {
        return new Vector3(p.x, p.z, p.y);
    }

    /// <summary>
    /// Convert a TR3D bounding box (center + size) to Unity space.
    /// Size axes are swapped to match the center transform.
    /// Yaw remains around the up axis (TR3D Z-up → Unity Y-up).
    /// </summary>
    public static void TR3DBoxToUnity(
        float cx, float cy, float cz,
        float sx, float sy, float sz,
        float yaw,
        out Vector3 center, out Vector3 size, out float unityYaw)
    {
        center = TR3DToUnity(new Vector3(cx, cy, cz));
        // Size mapping: TR3D (sx along X, sy along Y-fwd, sz along Z-up)
        //             → Unity (sx along X, sz along Y-up, sy along Z-fwd)
        size = new Vector3(sx, sz, sy);
        // Yaw in TR3D is around Z-up. In Unity that's around Y-up.
        // Negate to account for handedness change.
        unityYaw = -yaw;
    }
}
