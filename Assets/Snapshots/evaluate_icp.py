import os, numpy as np
import open3d as o3d

ROOT = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(ROOT, "icp_out")
CAMS = ["cam1","cam2","cam3","cam4","cam5"]

PAIRS = [("cam1","cam2"),("cam2","cam3"),("cam3","cam4"),("cam4","cam5"),
         ("cam1","cam3"),("cam2","cam4"),("cam3","cam5")]

VOXEL = 0.02
MAX_CORR = 0.10

def load_xyz(cam):
    return o3d.io.read_point_cloud(os.path.join(ROOT, f"{cam}.xyz"), format="xyz")

def preprocess(pcd):
    p = pcd.voxel_down_sample(VOXEL)
    p, _ = p.remove_statistical_outlier(nb_neighbors=30, std_ratio=1.5)
    p.estimate_normals(o3d.geometry.KDTreeSearchParamHybrid(radius=VOXEL*3, max_nn=30))
    return p

def load_delta(cam):
    if cam == "cam1":
        return np.eye(4)
    path = os.path.join(OUT, f"{cam}_delta_to_cam1.txt")
    return np.loadtxt(path)

def icp_eval(src, tgt):
    r = o3d.pipelines.registration.registration_icp(
        src, tgt, MAX_CORR, np.eye(4),
        o3d.pipelines.registration.TransformationEstimationPointToPlane()
    )
    return r.fitness, r.inlier_rmse

clouds = {c: preprocess(load_xyz(c)) for c in CAMS}
deltas = {c: load_delta(c) for c in CAMS}

print("=== BEFORE vs AFTER (pairwise eval) ===")
improved = 0
total = 0

for a,b in PAIRS:
    A0 = clouds[a]
    B0 = clouds[b]

    # BEFORE (no deltas applied)
    f0, e0 = icp_eval(A0, B0)

    # AFTER (apply your pose-graph deltas to both clouds)
    A1 = A0.transform(deltas[a].copy()) if a != "cam1" else A0
    B1 = B0.transform(deltas[b].copy()) if b != "cam1" else B0

    f1, e1 = icp_eval(A1, B1)

    print(f"{a}->{b}  fitness {f0:.3f}->{f1:.3f}   rmse {e0:.4f}->{e1:.4f}")

    total += 1
    if (e1 < e0) and (f1 >= f0 - 1e-3):
        improved += 1

print(f"\nImproved pairs: {improved}/{total}")
