# Azure Kinect Unity Integration

A comprehensive Unity toolkit for Azure Kinect featuring real-time data capture, 3D reconstruction, skeleton tracking, and avatar animation.

[![Unity Version](https://img.shields.io/badge/Unity-6.2%2B-blue)](https://unity.com/)
[![Azure Kinect](https://img.shields.io/badge/Azure%20Kinect-DK-green)](https://azure.microsoft.com/en-us/products/kinect-dk/)
[![License](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

## 🎯 Features

- **✨ Real-time Data Capture**: Direct Azure Kinect integration with Unity
- **☁️ 3D Point Cloud**: Full scene reconstruction with color mapping
- **🦴 Skeleton Tracking**: Multi-person body tracking with 32 joints per person
- **🎨 Skeleton Visualization**: Skeleton visualization with confidence levels
- **👤 Person Segmentation**: Individual point clouds and meshes per tracked person
- **📊 Performance Monitoring**: Built-in FPS and memory tracking

---

## 🔧 Prerequisites

### Hardware Requirements

- **Azure Kinect DK** device
- **USB 3.0** port (blue USB port)
- **Windows 10/11** (64-bit)
- **GPU** (recommended for body tracking, optional for CPU mode)
  - NVIDIA GPU with CUDA support (for GPU body tracking)
  - 8GB RAM minimum, 16GB recommended
  - Intel Core i5 or better

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
- Visual Studio 2022

**Project Settings Required:**
- **Api Compatibility Level:** .NET Standard 2.1 or .NET 4.x
- **Scripting Backend:** Mono (recommended) or IL2CPP
- **Architecture:** x86_64

#### 4. Optional: CUDA Toolkit (for GPU Body Tracking)

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

### Step 3: Copy Azure Kinect DLLs to Unity

#### Create Plugin Folders

```
Assets/
└── Plugins/
    ├── x86_64/          # Native DLLs
    └── (root)           # Managed DLLs
```

#### Copy Managed DLLs to `Assets/Plugins/`

**From Sensor SDK:**
```
Source: C:\Program Files\Azure Kinect SDK v1.4.1\sdk\netstandard2.0\release\
Files:
  - Microsoft.Azure.Kinect.Sensor.dll
```

**From Body Tracking SDK:**
```
Source: C:\Program Files\Azure Kinect Body Tracking SDK\sdk\netstandard2.0\release\
Files:
  - Microsoft.Azure.Kinect.BodyTracking.dll
```

#### Copy Native DLLs to `Assets/Plugins/x86_64/`

**From Sensor SDK:**
```
Source: C:\Program Files\Azure Kinect SDK v1.4.1\sdk\windows-desktop\amd64\release\bin\
Files:
  - k4a.dll
  - k4arecord.dll
  - depthengine_2_0.dll
```

**From Body Tracking SDK:**
```
Source: C:\Program Files\Azure Kinect Body Tracking SDK\sdk\windows-desktop\amd64\release\bin\
Files:
  - k4abt.dll
  - onnxruntime.dll
  - dnn_model_2_0_op11.onnx  (AI model - 159MB)
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

For each **Native DLL** in `Assets/Plugins/x86_64/`:

1. Select the DLL in Unity Inspector
2. Set **Platform Settings:**
   - ☑ Windows
   - CPU: **x86_64**
   - ☑ Load on startup
3. Click **Apply**

For each **Managed DLL** in `Assets/Plugins/`:

1. Select the DLL in Unity Inspector
2. Set **Platform Settings:**
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

### Step 6: Verify Installation

1. Connect Azure Kinect via USB 3.0
2. Open the main scene: `Assets/Scenes/MainScene.unity`
3. Press ▶️ **Play** — Unity will start capturing Kinect data and visualize:
- ✅ Live 3D Point cloud rendering of the entire scene
- ✅ Real-time Skeleton (joints and bones) and Per-person Mesh (if person detected)

---

## 🌟 Future Work
- Add SMPL and RocketBox avatar retargeting
- Integrate Multiple Azure Kinects
- Perform Skeleton-based Calibration of multiple Kinects

---
