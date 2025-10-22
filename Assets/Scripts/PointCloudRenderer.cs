using UnityEngine;

public class PointCloudRenderer : MonoBehaviour
{
    [SerializeField] private MonoBehaviour generatorSource;
    private IPointCloudSource pointCloudSource;

    private Mesh pointMesh;
    private MeshFilter meshFilter;
    private MeshRenderer meshRenderer;

    // Reusable arrays to avoid allocations
    private Vector3[] vertexArray;
    private Color32[] colorArray;
    private int[] indexArray;
    private int lastCapacity = 0;

    void Start()
    {
        pointCloudSource = generatorSource as IPointCloudSource;
        if (pointCloudSource != null)
            pointCloudSource.OnPointCloudGenerated += RenderPointCloud;
        SetupRenderer();
    }

    void SetupRenderer()
    {
        meshFilter = gameObject.AddComponent<MeshFilter>();
        meshRenderer = gameObject.AddComponent<MeshRenderer>();

        Shader shader = Shader.Find("Particles/Standard Unlit");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");

        Material mat = new Material(shader);
        mat.SetInt("_ColorMode", 1);
        mat.enableInstancing = true;
        meshRenderer.material = mat;

        pointMesh = new Mesh();
        pointMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        pointMesh.MarkDynamic(); // Important for frequently updated meshes
        meshFilter.mesh = pointMesh;
    }

    void RenderPointCloud(PointCloudData pointCloud)
    {
        if (pointCloud.Points.Count == 0)
            return;

        int count = pointCloud.Points.Count;

        // Resize arrays only if needed
        if (vertexArray == null || vertexArray.Length < count)
        {
            int newSize = Mathf.NextPowerOfTwo(count);
            vertexArray = new Vector3[newSize];
            colorArray = new Color32[newSize];
            indexArray = new int[newSize];

            Debug.Log($"Resized render arrays to {newSize}");
        }

        // Copy to arrays (faster than SetVertices with List)
        pointCloud.Points.CopyTo(vertexArray, 0);
        pointCloud.Colors.CopyTo(colorArray, 0);

        for (int i = 0; i < count; i++)
            indexArray[i] = i;

        pointMesh.Clear(false); // Don't reset bounds/arrays
        pointMesh.vertices = vertexArray;
        pointMesh.colors32 = colorArray;
        pointMesh.SetIndices(indexArray, 0, count, MeshTopology.Points, 0, false);
        pointMesh.RecalculateBounds();
    }

    void OnDestroy()
    {
        if (pointCloudSource != null)
            pointCloudSource.OnPointCloudGenerated -= RenderPointCloud;
    }
}