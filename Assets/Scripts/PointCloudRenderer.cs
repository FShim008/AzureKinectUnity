using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PointCloudRenderer : MonoBehaviour
{
    [SerializeField] private MonoBehaviour pointCloudSourceBehaviour; // must implement IPointCloudSource
    private IPointCloudSource _source;

    [Header("Rendering")]
    public Shader pointCloudShader;
    public bool TreatInputAsWorldSpace = true;

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

    private void OnEnable() => BindSource();
    private void OnDisable() => UnbindSource();

    private void BindSource()
    {
        if (pointCloudSourceBehaviour == null)
        {
            Debug.LogError($"[{gameObject.name}] pointCloudSourceBehaviour is NULL. Drag PointCloudFusionSource (or generator) here.");
            enabled = false;
            return;
        }

        _source = pointCloudSourceBehaviour as IPointCloudSource;
        if (_source == null)
        {
            Debug.LogError($"[{gameObject.name}] Assigned source '{pointCloudSourceBehaviour.name}' does NOT implement IPointCloudSource.");
            enabled = false;
            return;
        }

        _source.OnPointCloudGenerated -= RenderPointCloud;
        _source.OnPointCloudGenerated += RenderPointCloud;

        Debug.Log($"[PCR] Bound renderer '{name}' to source '{pointCloudSourceBehaviour.name}' (TreatInputAsWorldSpace={TreatInputAsWorldSpace})");
    }

    private void UnbindSource()
    {
        if (_source != null)
        {
            _source.OnPointCloudGenerated -= RenderPointCloud;
            _source = null;
        }
    }

    private void SetupMeshAndMaterial()
    {
        if (pointCloudShader == null)
            pointCloudShader = Shader.Find("Custom/PointCloudPointsOnly");

        if (pointCloudShader == null)
        {
            Debug.LogError($"[{gameObject.name}] Shader not found. Assign pointCloudShader in Inspector.");
            enabled = false;
            return;
        }

        var mat = new Material(pointCloudShader);
        mat.enableInstancing = true;
        meshRenderer.sharedMaterial = mat;

        pointMesh = new Mesh { name = $"{gameObject.name}_PointMesh" };
        pointMesh.indexFormat = IndexFormat.UInt32;
        pointMesh.MarkDynamic();
        meshFilter.sharedMesh = pointMesh;
    }

    private void RenderPointCloud(PointCloudData pc)
    {
        // ✅ FIX: PointCloudData is likely a struct, so pc==null is invalid
        if (pc.Points == null || pc.Colors == null) return;
        if (pc.Count <= 0) return;

        int count = Mathf.Min(pc.Count, Mathf.Min(pc.Points.Count, pc.Colors.Count));
        if (count <= 0) return;

        if (vertexArray == null || vertexArray.Length < count)
        {
            int newSize = Mathf.NextPowerOfTwo(count);
            vertexArray = new Vector3[newSize];
            colorArray = new Color32[newSize];
            indexArray = new int[newSize];
            Debug.Log($"[{gameObject.name}] Resized render arrays capacity to {newSize}.");
        }

        for (int i = 0; i < count; i++)
        {
            vertexArray[i] = pc.Points[i];
            colorArray[i] = pc.Colors[i];
        }

        if (TreatInputAsWorldSpace)
        {
            for (int i = 0; i < count; i++)
                vertexArray[i] = transform.InverseTransformPoint(vertexArray[i]);
        }

        for (int i = 0; i < count; i++)
            indexArray[i] = i;

        pointMesh.Clear(false);
        pointMesh.subMeshCount = 1;
        pointMesh.SetVertices(vertexArray, 0, count);
        pointMesh.SetColors(colorArray, 0, count);
        pointMesh.SetIndices(indexArray, 0, count, MeshTopology.Points, 0, false);
        pointMesh.RecalculateBounds();
    }

    private void OnDestroy()
    {
        UnbindSource();
        if (pointMesh != null)
        {
            Destroy(pointMesh);
            pointMesh = null;
        }
    }
}
