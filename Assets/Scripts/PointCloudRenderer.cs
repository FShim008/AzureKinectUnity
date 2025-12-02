using UnityEngine;
using System.Collections.Generic;
using System;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PointCloudRenderer : MonoBehaviour
{
    [SerializeField] private PointCloudGenerator pointCloudSource;

    [Header("Rendering")]
    [Tooltip("Custom shader required for rendering points.")]
    public Shader pointCloudShader;

    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;
    private Mesh pointMesh;

    private Vector3[] vertexArray;
    private Color32[] colorArray;
    private int[] indexArray;

    private void Awake()
    {
        meshFilter = GetComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        SetupMeshAndMaterial();
    }

    private void Start()
    {
        if (pointCloudSource == null)
        {
            pointCloudSource = GetComponent<PointCloudGenerator>();
            if (pointCloudSource == null)
            {
                Debug.LogError($"[{gameObject.name}] Missing required {nameof(PointCloudGenerator)} component. Cannot render.");
                enabled = false;
                return;
            }
        }
        pointCloudSource.OnPointCloudGenerated += RenderPointCloud;
    }

    private void SetupMeshAndMaterial()
    {
        if (pointCloudShader == null)
        {
            pointCloudShader = Shader.Find("Custom/PointCloudShader");
            if (pointCloudShader == null)
            {
                Debug.LogError($"[{gameObject.name}] Point Cloud Shader 'Custom/PointCloudShader' not found.");
                enabled = false;
                return;
            }
        }
        Material mat = new Material(pointCloudShader);
        mat.SetInt("_ColorMode", 1);
        mat.enableInstancing = true;
        meshRenderer.material = mat;

        pointMesh = new Mesh();
        pointMesh.indexFormat = IndexFormat.UInt32;
        pointMesh.MarkDynamic();
        meshFilter.mesh = pointMesh;
    }

    private void RenderPointCloud(PointCloudData pointCloud)
    {
        if (pointCloud.Count == 0)
            return;
        int count = pointCloud.Count;
        if (vertexArray == null || vertexArray.Length < count)
        {
            int newSize = Mathf.NextPowerOfTwo(count);
            vertexArray = new Vector3[newSize];
            colorArray = new Color32[newSize];
            indexArray = new int[newSize];
            Debug.Log($"[{gameObject.name}] Resized render arrays capacity to {newSize}.");
        }
        pointCloud.Points.CopyTo(vertexArray, 0);
        pointCloud.Colors.CopyTo(colorArray, 0);
        for (int i = 0; i < count; i++)
            indexArray[i] = i;
        pointMesh.Clear();
        pointMesh.SetVertices(vertexArray, 0, count);
        pointMesh.SetColors(colorArray, 0, count);
        pointMesh.SetIndices(indexArray, 0, count, MeshTopology.Points, 0, false);
        pointMesh.RecalculateBounds();
    }

    private void OnDestroy()
    {
        if (pointCloudSource != null)
            pointCloudSource.OnPointCloudGenerated -= RenderPointCloud;
    }
}