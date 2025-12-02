# Azure Kinect Unity Integration

A comprehensive Unity toolkit for Azure Kinect featuring real-time data capture, 3D reconstruction, skeleton tracking, avatar animation, and an automated multi-camera calibration system.

[![Unity Version](https://img.shields.io/badge/Unity-6.2%2B-blue)](https://unity.com/)
[![Azure Kinect](https://img.shields.io/badge/Azure%20Kinect-DK-green)](https://azure.microsoft.com/en-us/products/kinect-dk/)
[![License](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

---

## 📚 Table of Contents
- [Features](#-features)
- [Prerequisites](#-prerequisites)
- [Setup](#-setup)
- [Multi-Camera Calibration](#-multi-camera-calibration-setup)
- [Point Cloud - Entire Scene or Only Person](#-point-cloud)
- [Future Work](#-future-work)

---

## 🎯 Features

- **✨ K4AdotNet Integration**: Leverages the official, high-performance .NET Standard 2.1 wrapper for Azure Kinect.
- **🔗 Multi-Device Support**: Capable of initializing and running multiple Azure Kinect (or Orbbec Femto Bolt) devices simultaneously.
- **📐 Automated Multi-Camera Calibration**: Skeleton-based Procrustes analysis and transitive chaining to align all cameras to a single coordinate system (Camera 1).
- **🦴 Skeleton Tracking**: Multi-person body tracking with 32 joints per person.
- **🎨 Skeleton Visualization**: Skeleton visualization with confidence levels.
- **☁️ 3D Point Cloud (PC)**: Full scene reconstruction with color mapping.
- **👤 Person PC Segmentation**: Individual point clouds per tracked person.
- **📊 Performance Monitoring**: Built-in FPS and memory tracking

---

## 🔧 Prerequisites

### Hardware Requirements

- **Azure Kinect DK** device
- (Optional) **Orbbec Femto Bolt** device in lieu of Azure Kinect
- **USB 3.0** port (blue USB port)
- **Windows 10/11** (64-bit)
- **GPU** (recommended for body tracking, optional for CPU mode)
  - NVIDIA GPU with CUDA support (for GPU body tracking)
  - 8GB RAM minimum, 16GB recommended
  - Intel Core i5 or better

--- 

### Software Requirements

#### 1. Azure Kinect Sensor SDK

**Version:** 1.4.1

**Download:** [Azure Kinect Sensor SDK v1.4.1](https://www.microsoft.com/en-us/download/details.aspx?id=101454)

**Installation Steps:**
1. Download `Azure-Kinect-SDK-1.4.1.exe`
2. Run installer with default settings
3. Default path: `C:\Program Files\Azure Kinect SDK v1.4.1\`

**Verify Installation:**
```bash
# Check if Azure Kinect Viewer works
C:\Program Files\Azure Kinect SDK v1.4.1\tools\k4aviewer.exe
```

#### 2. Azure Kinect Body Tracking SDK

**Version:** 1.1.2

**Download:** [Azure Kinect Body Tracking SDK v1.1.2](https://www.microsoft.com/en-us/download/details.aspx?id=104221)

**Installation Steps:**
1. Download `Azure-Kinect-Body-Tracking-SDK-1.1.2.exe`
2. Run installer with default settings
3. Default path: `C:\Program Files\Azure Kinect Body Tracking SDK\`

**Verify Installation:**
```bash
# Check if Body Tracking Viewer works
C:\Program Files\Azure Kinect Body Tracking SDK\tools\k4abt_simple_3d_viewer.exe
```

#### 3. Unity

**Version:** 6.2 (6000.2.8f1) or later

**Download:** [Unity Hub](https://unity.com/download)

**Required Modules:**
- Windows Build Support (IL2CPP)
- Visual Studio 2026

**Project Settings Required:**
- **Api Compatibility Level:** .NET Standard 2.1 or .NET 4.x
- **Scripting Backend:** Mono (recommended) or IL2CPP
- **Architecture:** x86_64


#### 4.  Other Libraries

**K4AdotNet v1.4.17 or later:** [K4AdotNet NuGet]
(https://www.nuget.org/packages/K4AdotNet) - Download the zip and extract it in any folder.

**MathNet.Numerics v5.0.0 or later:** [MathNet.Numerics NuGet]
(https://www.nuget.org/packages/MathNet.Numerics) - Download the zip and extract it in any folder.


#### 5. Optional: Orbbec Femto Bolt K4A-Wrapper (in lieu of Azure Kinect)

**Version:** 2.0.11 or later

**Download:** [Orbbec SDK K4A Wrapper](https://github.com/orbbec/OrbbecSDK-K4A-Wrapper/releases/)

**Installation Steps:**
1. Download `OrbbecSDK_K4A_Wrapper_v2.0.11_windows_202510221441.zip` and extract it to some `wrapper-dir` folder 
2. Follow the Section-2 instructions here `https://www.orbbec.com/documentation/access-akdk-application-software-with-femto-bolt/` to copy files from the Wrapper to the default Azure Kinect SDK path: `C:\Program Files\Azure Kinect SDK v1.4.1\`
3. Header files - copy both the folders `k4a` and `k4arecord` from `wrapper-dir\include\` to `C:\Program Files\Azure Kinect SDK v1.4.1\sdk\include`
4. Library files - copy `k4a.lib` and `k4arecord.lib` from `wrapper-dir\lib\` to `C:\Program Files\Azure Kinect SDK v1.4.1\sdk\windows-desktop\amd64\release\lib\`
5. DLL files & Extensions folder - copy the files `k4a.dll`, `k4arecord.dll`, `depthengine_2_0.dll`, `OrbbecSDK.dll` and the `extensions\` folder from `wrapper-dir\bin\` to two locations - `C:\Program Files\Azure Kinect SDK v1.4.1\sdk\windows-desktop\amd64\release\bin\` & `C:\Program Files\Azure Kinect SDK v1.4.1\tools`


**Verify Installation:**
```bash
# Check if Body Tracking Viewer works
C:\Program Files\Azure Kinect Body Tracking SDK\tools\k4abt_simple_3d_viewer.exe
```

#### 6. Optional: CUDA Toolkit (for GPU Body Tracking)

**Version:** CUDA 11.0 or later

**Download:** [CUDA Toolkit](https://developer.nvidia.com/cuda-downloads)

**Note:** Only required if using `TrackerProcessingMode.Gpu` for body tracking. CPU mode works without CUDA.

---

## 📦 Setup

### Step 1: Clone Repository

```bash
git clone https://github.com/yourusername/azure-kinect-unity.git
cd azure-kinect-unity
```

### Step 2: Open in Unity

1. Open **Unity Hub**
2. Click **Add** → Select project folder
3. Open project with Unity 2021.3+ LTS

### Step 3: Copy Azure Kinect DLLs (or the overwritten Orbbec SDK Wrapper DLLs) + K4AdotNet + Math.Net.Numerics to Unity

#### Create Plugin Folders

```
Assets/
└── Plugins/
```

#### Copy DLLs to `Assets/Plugins/`

**From the extracted K4AdotNet:**
```K4AdotNet.dll```

**From the extracted MathNet.Numerics:**
```MathNet.Numerics.dll```

**From Sensor SDK:**
```
Source: C:\Program Files\Azure Kinect SDK v1.4.1\sdk\windows-desktop\amd64\release\bin\
Files:
  - k4a.dll
  - k4arecord.dll
  - depthengine_2_0.dll
  - (optional for using Orbbec Femto Bolt K4A-Wrapper) OrbbecSDK.dll
  - (optional for using Orbbec Femto Bolt K4A-Wrapper) (folder) extensions/
```

**From Body Tracking SDK:**
```
Source: C:\Program Files\Azure Kinect Body Tracking SDK\sdk\windows-desktop\amd64\release\bin\
Files:
  - k4abt.dll
  - onnxruntime.dll
  - dnn_model_2_0_op11.onnx
```

**Optional (for GPU tracking):**
```
Source: C:\Program Files\Azure Kinect Body Tracking SDK\tools\
Files:
  - cudart64_110.dll
  - cublas64_11.dll
  - cudnn64_8.dll
```

### Step 4: Configure DLL Import Settings in Unity

For each **DLL** in `Assets/Plugins/`:

1. Select the DLL in Unity Inspector
2. Verify the following in **Platform Settings:**
   - ☑ Windows
   - CPU: **x86_64**
   - ☑ Load on startup
   - ☑ Any Platform
   - ☑ Validate References
3. Click **Apply**

### Step 5: Configure Unity Project Settings

**Edit → Project Settings → Player:**

1. **Other Settings**
   - Api Compatibility Level: **.NET Standard 2.1**
   - Allow 'unsafe' Code: **☑ Checked**
   - Scripting Backend: **Mono** (recommended)

2. **Configuration**
   - Scripting Define Symbols: *(leave empty)*

**File → Build Settings:**

1. Architecture: **x86_64**
2. Target Platform: **Windows**

---

## 📐 Multi-Camera Calibration Setup
This toolkit features an automated workflow for calibrating multiple cameras using tracked skeletal joint data, ensuring all sensors share a common world coordinate system (Camera 1).
### Workflow:
1. Identify Base Camera: Camera 1 (Device Index 0) is designated as the world coordinate system origin, and its transformation matrix is always Matrix4x4.identity.
2. Setup Direct Pairs: In your scene, add a DualCameraCalibrator component for each direct calibration pair required to form a chain to Camera 1 (e.g., Camera 2 to Camera 1, Camera 3 to Camera 2, Camera 4 to Camera 3, etc.).
3. Static Registration: On Start(), every active DualCameraCalibrator registers its required pair (Source → Target) with the static CalibrationUtility.
4. Direct Calibration: For each active pair, the user presses the trigger key 'C' to collect skeleton data. The system performs Procrustes Analysis (SVD) to calculate the rigid transformation matrix T<sub>Source→Target</sub>.
5. Automated Transitive Sweep:
  - The CalibrationUtility saves the direct calibration file (e.g., calib-2-1.txt, calib-3-2.txt).
  - Crucially, it then checks its static registry: if all required direct calibrations are marked as complete, the system automatically triggers a comprehensive transitive sweep.
  - The sweep uses the chaining rule (e.g., T<sub>3→1</sub> = T<sub>2→1</sub> * T<sub>3→2</sub>) to compute the final transformation matrix for every camera (S > 1) relative to the base Camera 1.
6. Result: Final transformation files (e.g., calib-3-1.txt, calib-4-1.txt) are created and saved, ready to be loaded by the KinectDevice components on next scene launch. We can toggle between the loaded calibration files for all cameras or identity transformation by pressing the key 'L'.

---

## ☁️ Point Cloud - Entire Scene or Only Person
The `PointCloudGenerator.cs` component has been implemented with a toggle mechanism to switch between two viewing modes for the 3D point cloud: the entire scene or only the tracked person(s). This unified approach keeps the scene clean and resource usage efficient.
### How to Use the Toggle:
- **Key Binding:** Press the P key (by default) to switch between modes.
- **Property:** Control the behavior directly via the `FilterToHumanRegion` boolean property in the `PointCloudGenerator` component.
### Implementation Overview:
1. The `PointCloudGenerator` retrieves the *Body Index Map* (an 8-bit image aligning body segmentation data to the depth frame) from the `SkeletonTracker`.
2. The transformation from the depth camera's view (where the Body Index Map originates) to the color camera's view is handled simultaneously with the depth registration using the comprehensive K4AdotNet function:
```C#_transformation.DepthImageToColorCameraCustom(
    depthImage: capture.DepthImage,
    customImage: bodyIndexMap,
    transformedDepthImage: _registeredDepthImage,
    transformedCustomImage: _registeredBodyIndexMap,
    interpolation: TransformationInterpolation.NearestNeighbor,
    invalidCustomValue: 255
);```
3. When `FilterToHumanRegion` is active, the point generation loop checks the registered *Body Index Map*. Any point corresponding to a pixel with the value 255 (which is the universal background/untracked value) is discarded.


---

## 🌟 Future Work
- Add Avatar retargeting on the obtained skeleton (SMPL, RocketBox, Mixamo, etc.)

---

## 👤 Author
**Kevin Desai**  
Assistant Professor of Instruction, UTSA CS Department  
📧 [kevin.desai@utsa.edu](mailto:kevin.desai@utsa.edu)

---
