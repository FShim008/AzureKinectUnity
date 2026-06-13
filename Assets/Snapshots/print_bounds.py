import os
import open3d as o3d

SNAP_DIR = r"C:\Users\Human Mobility SP1\Documents\GitHub\AzureKinectUnity\Assets\Snapshots"
cams = ["cam1","cam2","cam3","cam4","cam5"]

for cam in cams:
    p = o3d.io.read_point_cloud(os.path.join(SNAP_DIR, f"{cam}.xyz"))
    bbox = p.get_axis_aligned_bounding_box()
    mn = bbox.get_min_bound()
    mx = bbox.get_max_bound()
    print(cam, "points=", len(p.points))
    print("  min:", mn)
    print("  max:", mx)
