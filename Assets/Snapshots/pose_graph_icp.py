import os
import numpy as np
import open3d as o3d

ROOT = os.path.dirname(os.path.abspath(__file__))  # Assets/Snapshots
IN_DIR = ROOT
OUT_DIR = os.path.join(ROOT, "icp_out")

CAMS = ["cam1", "cam2", "cam3", "cam4", "cam5"]

# ---- Tunables (good indoor defaults) ----
VOXEL = 0.02                 # 2 cm
MAX_CORR = 0.10              # 10 cm
EDGE_FITNESS_MIN = 0.25      # skip weak constraints
# ----------------------------------------

def load_xyz(path):
    pcd = o3d.io.read_point_cloud(path, format="xyz")
    if pcd.is_empty():
        raise RuntimeError(f"Empty point cloud: {path}")
    return pcd

def preprocess(pcd: o3d.geometry.PointCloud):
    p = pcd.voxel_down_sample(VOXEL)
    p, _ = p.remove_statistical_outlier(nb_neighbors=30, std_ratio=1.5)
    p.estimate_normals(
        search_param=o3d.geometry.KDTreeSearchParamHybrid(radius=VOXEL * 3.0, max_nn=30)
    )
    return p

def icp_point_to_plane(src, tgt):
    result = o3d.pipelines.registration.registration_icp(
        src, tgt,
        MAX_CORR,
        np.eye(4),
        o3d.pipelines.registration.TransformationEstimationPointToPlane()
    )
    return result.transformation, result.fitness, result.inlier_rmse

def ensure_out_dir():
    os.makedirs(OUT_DIR, exist_ok=True)

def save_matrix_txt(path, T):
    # ROW-major 4 lines, 4 cols (Unity loader expects this)
    with open(path, "w") as f:
        for r in range(4):
            f.write(f"{T[r,0]:.9f} {T[r,1]:.9f} {T[r,2]:.9f} {T[r,3]:.9f}\n")

def main():
    ensure_out_dir()

    # Load + preprocess
    clouds = {}
    for cam in CAMS:
        xyz_path = os.path.join(IN_DIR, f"{cam}.xyz")
        if not os.path.exists(xyz_path):
            raise FileNotFoundError(f"Missing snapshot: {xyz_path}")
        raw = load_xyz(xyz_path)
        clouds[cam] = preprocess(raw)
        print(f"[LOAD] {cam}: {len(clouds[cam].points)} pts (downsampled)")

    # Build pose graph: nodes = correction poses (ΔT_cam)
    # We anchor cam1 at identity.
    pg = o3d.pipelines.registration.PoseGraph()
    pg.nodes.append(o3d.pipelines.registration.PoseGraphNode(np.eye(4)))

    # initialize other nodes as identity too (since already in world via calibration)
    for _ in CAMS[1:]:
        pg.nodes.append(o3d.pipelines.registration.PoseGraphNode(np.eye(4)))

    # Candidate edges: neighbors + a few extra loops (helps stability if overlap exists)
    pairs = [
        ("cam1","cam2"),
        ("cam2","cam3"),
        ("cam3","cam4"),
        ("cam4","cam5"),
    ]

    cam_to_idx = {c:i for i,c in enumerate(CAMS)}

    good_edges = 0
    for a, b in pairs:
        ia = cam_to_idx[a]
        ib = cam_to_idx[b]
        Ta_b, fit, rmse = icp_point_to_plane(clouds[a], clouds[b])
        print(f"[ICP] {a}->{b} fit={fit:.3f} rmse={rmse:.4f}")

        if fit < EDGE_FITNESS_MIN:
            print(f"  [SKIP] weak overlap ({fit:.3f} < {EDGE_FITNESS_MIN})")
            continue

        # PoseGraphEdge expects transformation from source to target
        # uncertain=False for odometry-like edges, uncertain=True for loop closures.
        uncertain = not (abs(ia - ib) == 1)  # neighbors are "odometry", extras are "loop closures"
        pg.edges.append(
            o3d.pipelines.registration.PoseGraphEdge(
                ia, ib, Ta_b, np.identity(6), uncertain
            )
        )
        good_edges += 1

    if good_edges == 0:
        raise RuntimeError("No usable ICP edges (all fitness too low). Try increasing overlap / MAX_CORR / lowering EDGE_FITNESS_MIN.")

    # Global optimize (fix cam1)
    option = o3d.pipelines.registration.GlobalOptimizationOption(
        max_correspondence_distance=MAX_CORR,
        edge_prune_threshold=0.05,
        reference_node=0
    )
    o3d.pipelines.registration.global_optimization(
        pg,
        o3d.pipelines.registration.GlobalOptimizationLevenbergMarquardt(),
        o3d.pipelines.registration.GlobalOptimizationConvergenceCriteria(),
        option
    )

    # Node pose = optimized correction transform for that cam (ΔT_cam)
    # Unity wants camX_delta_to_cam1.txt containing ΔT_cam (left-multiply)
    for cam in CAMS:
        idx = cam_to_idx[cam]
        T = np.linalg.inv(pg.nodes[idx].pose)

        if cam == "cam1":
            continue

        out_path = os.path.join(OUT_DIR, f"{cam}_delta_to_cam1.txt")
        save_matrix_txt(out_path, T)
        print(f"[WRITE] {out_path}")

    print("DONE. Copy is not needed; Unity will read directly from Assets/Snapshots/icp_out.")

if __name__ == "__main__":
    main()
