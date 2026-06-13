import open3d as o3d
import numpy as np
import os

snap_dir = r"C:\Users\Human Mobility SP1\Documents\GitHub\AzureKinectUnity\Assets\Snapshots"
out_dir  = os.path.join(snap_dir, "icp_out")

def load_xyz(path):
    pcd = o3d.io.read_point_cloud(path)
    return pcd

def load_T(path):
    M = np.loadtxt(path)
    if M.shape == (4,4):
        return M
    raise ValueError(f"Bad matrix shape in {path}: {M.shape}")

target = load_xyz(os.path.join(snap_dir, "cam1.xyz"))

for cam in [2,3,4,5]:
    src = load_xyz(os.path.join(snap_dir, f"cam{cam}.xyz"))
    T = load_T(os.path.join(out_dir, f"cam{cam}_delta_to_cam1.txt"))

    src_t = src.transform(T.copy())

    print(f"Showing cam{cam} -> cam1")
    o3d.visualization.draw_geometries([target, src_t])
