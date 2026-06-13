import os
import re
import numpy as np
import open3d as o3d

# -------------------- Paths --------------------
# Folder where Unity saves snapshots: cam1.xyz ... cam5.xyz
SNAP_DIR = r"C:\Users\Human Mobility SP1\Documents\GitHub\AzureKinectUnity\Assets\Snapshots"

# Where to write ICP outputs.
# Option A (recommended): write directly to Assets/CalibrationFiles for Unity to read immediately.
OUTPUT_DIR = r"C:\Users\Human Mobility SP1\Documents\GitHub\AzureKinectUnity\Assets\CalibrationFiles"

# Option B: keep old behavior (uncomment) to write into Snapshots/icp_out
# OUTPUT_DIR = os.path.join(SNAP_DIR, "icp_out")

# -------------------- Camera naming --------------------
cams = ["cam1", "cam2", "cam3", "cam4", "cam5"]
ref = "cam1"  # base camera snapshot name (Unity base camera is usually 1)

# Unity expects icp-{cam}-{base}.txt
BASE_CAM_NUM = 1

# ---------- ROI crop (tune these!) ----------
# This is in the SAME coordinate frame as your snapshots (WORLD after calibration toggle).
# If ROI is too tight or wrong, ICP fitness will be low.
CROP_MIN = np.array([-4.0, -0.5,  0.0])
CROP_MAX = np.array([ 4.0,  2.5,  9.0])

# ---------- ICP params ----------
# Multi-stage ICP settings
STAGES = [
    (0.050, 0.125),  # (voxel, max_corr)
    (0.020, 0.050),
    (0.010, 0.025),
]

# -------------------- Helpers --------------------
def cam_name_to_num(cam_name: str) -> int:
    """'cam4' -> 4"""
    m = re.search(r"(\d+)$", cam_name)
    if not m:
        raise ValueError(f"Cannot parse camera number from '{cam_name}'")
    return int(m.group(1))

def load_xyz(path: str) -> o3d.geometry.PointCloud:
    pts = np.loadtxt(path, dtype=np.float32)
    if pts.ndim == 1:
        pts = pts.reshape(1, 3)
    pcd = o3d.geometry.PointCloud()
    pcd.points = o3d.utility.Vector3dVector(pts[:, :3])
    return pcd

def crop_and_downsample(pcd: o3d.geometry.PointCloud, voxel: float) -> o3d.geometry.PointCloud:
    aabb = o3d.geometry.AxisAlignedBoundingBox(CROP_MIN, CROP_MAX)
    p = pcd.crop(aabb)
    if voxel > 0:
        p = p.voxel_down_sample(voxel)
    p.estimate_normals(search_param=o3d.geometry.KDTreeSearchParamHybrid(radius=voxel * 3.0, max_nn=30))
    return p

def save_matrix_txt(path: str, T: np.ndarray):
    """Save 4x4 matrix in a simple whitespace format (Unity CalibrationUtility-friendly)."""
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        for r in range(4):
            f.write(" ".join(f"{T[r, c]:.9f}" for c in range(4)) + "\n")

def run_icp(source_pcd: o3d.geometry.PointCloud, target_pcd: o3d.geometry.PointCloud) -> np.ndarray:
    """
    Returns T that maps source -> target.
    Multi-stage point-to-plane ICP.
    """
    T = np.eye(4, dtype=np.float64)

    for voxel, max_corr in STAGES:
        src = crop_and_downsample(source_pcd, voxel)
        tgt = crop_and_downsample(target_pcd, voxel)

        if len(src.points) < 50 or len(tgt.points) < 50:
            print(f"  [WARN] Too few points after crop/downsample: src={len(src.points)} tgt={len(tgt.points)}")
            break

        reg = o3d.pipelines.registration.registration_icp(
            src, tgt,
            max_corr,
            T,
            o3d.pipelines.registration.TransformationEstimationPointToPlane(),
            o3d.pipelines.registration.ICPConvergenceCriteria(max_iteration=60)
        )

        T = reg.transformation
        print(f"  voxel={voxel:.3f} maxCorr={max_corr:.3f} fitness={reg.fitness:.4f} rmse={reg.inlier_rmse:.4f}")

    return T

# -------------------- Main --------------------
def main():
    ref_path = os.path.join(SNAP_DIR, f"{ref}.xyz")
    if not os.path.exists(ref_path):
        raise FileNotFoundError(f"Missing reference snapshot: {ref_path}")

    target = load_xyz(ref_path)

    out_dir = OUTPUT_DIR
    os.makedirs(out_dir, exist_ok=True)

    base_num = BASE_CAM_NUM

    for cam in cams:
        if cam == ref:
            continue

        cam_num = cam_name_to_num(cam)

        src_path = os.path.join(SNAP_DIR, f"{cam}.xyz")
        if not os.path.exists(src_path):
            print(f"[SKIP] Missing snapshot: {src_path}")
            continue

        print(f"=== ICP: cam{cam_num} -> cam{base_num} ===")
        source = load_xyz(src_path)

        # ICP gives correction that maps the (already roughly aligned) source -> target
        T_delta = run_icp(source, target)

        # Save with Unity's expected naming
        out_name = f"icp-{cam_num}-{base_num}.txt"
        out_path = os.path.join(out_dir, out_name)
        save_matrix_txt(out_path, T_delta)

        print(f"Saved delta: {out_path}")

    print(f"Done. Deltas are in: {out_dir}")

if __name__ == "__main__":
    main()
