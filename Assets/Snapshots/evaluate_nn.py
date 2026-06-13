import os, numpy as np
import open3d as o3d

ROOT = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(ROOT, "icp_out")
CAMS = ["cam1","cam2","cam3","cam4","cam5"]

# Evaluate these pairs (neighbors are the most meaningful)
PAIRS = [("cam1","cam2"),("cam2","cam3"),("cam3","cam4"),("cam4","cam5")]

VOXEL = 0.02

def load_xyz(cam):
    return o3d.io.read_point_cloud(os.path.join(ROOT, f"{cam}.xyz"), format="xyz")

def preprocess(pcd):
    p = pcd.voxel_down_sample(VOXEL)
    p, _ = p.remove_statistical_outlier(nb_neighbors=30, std_ratio=1.5)
    return p

def load_delta(cam):
    if cam == "cam1":
        return np.eye(4)
    return np.loadtxt(os.path.join(OUT, f"{cam}_delta_to_cam1.txt"))

def nn_stats(A: o3d.geometry.PointCloud, B: o3d.geometry.PointCloud):
    # distance from A -> nearest neighbor in B
    d = np.asarray(A.compute_point_cloud_distance(B))
    return {
        "median": float(np.median(d)),
        "p90": float(np.quantile(d, 0.90)),
        "mean": float(np.mean(d)),
        "n": int(len(d))
    }

clouds0 = {c: preprocess(load_xyz(c)) for c in CAMS}
deltas = {c: load_delta(c) for c in CAMS}

print("=== NN distance eval (meters): BEFORE vs AFTER ===")
for a,b in PAIRS:
    A0 = clouds0[a]
    B0 = clouds0[b]

    s0 = nn_stats(A0, B0)

    A1 = o3d.geometry.PointCloud(A0)
    B1 = o3d.geometry.PointCloud(B0)
    A1.transform(deltas[a])
    B1.transform(deltas[b])

    s1 = nn_stats(A1, B1)

    print(f"{a}->{b}  median {s0['median']:.4f}->{s1['median']:.4f} "
          f" p90 {s0['p90']:.4f}->{s1['p90']:.4f}  mean {s0['mean']:.4f}->{s1['mean']:.4f}")
