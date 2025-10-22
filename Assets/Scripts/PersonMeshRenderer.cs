using UnityEngine;
using System.Collections.Generic;

public class PersonMeshRenderer : MonoBehaviour
{
    [SerializeField] private PersonMeshGenerator meshGenerator;
    [SerializeField] private Material personMaterial;

    private readonly Dictionary<uint, PersonMeshObject> activeMeshes = new();

    public event System.Action OnMeshesUpdated;

    void Start()
    {
        if (meshGenerator != null)
            meshGenerator.OnPersonMeshesGenerated += RenderPersonMeshes;
    }

    void OnDestroy()
    {
        if (meshGenerator != null)
            meshGenerator.OnPersonMeshesGenerated -= RenderPersonMeshes;

        foreach (var kv in activeMeshes)
            if (kv.Value.GameObject != null)
                Destroy(kv.Value.GameObject);
        activeMeshes.Clear();
    }

    void RenderPersonMeshes(List<PersonMeshData> personMeshes)
    {
        var currentBodyIds = new HashSet<uint>();

        foreach (var personMesh in personMeshes)
        {
            currentBodyIds.Add(personMesh.BodyId);
            if (!activeMeshes.ContainsKey(personMesh.BodyId))
                CreatePersonMeshObject(personMesh.BodyId);
            UpdatePersonMesh(personMesh);
        }

        var toRemove = new List<uint>();
        foreach (var kvp in activeMeshes)
            if (!currentBodyIds.Contains(kvp.Key))
                toRemove.Add(kvp.Key);

        foreach (var bodyId in toRemove)
            RemovePersonMeshObject(bodyId);

        OnMeshesUpdated?.Invoke();
    }

    void CreatePersonMeshObject(uint bodyId)
    {
        GameObject meshObj = new GameObject($"Person_{bodyId}");

        meshObj.transform.SetParent(transform, false);

        // Ensure MeshFilter and MeshRenderer exist
        var meshFilter = meshObj.GetComponent<MeshFilter>();
        if (meshFilter == null)
            meshFilter = meshObj.AddComponent<MeshFilter>();

        var meshRenderer = meshObj.GetComponent<MeshRenderer>();
        if (meshRenderer == null)
            meshRenderer = meshObj.AddComponent<MeshRenderer>();

        // Double-check in case Unity delayed component creation (rare but possible in some prefabs)
        if (meshRenderer == null)
        {
            Debug.LogWarning($"[PersonMeshRenderer] Could not create MeshRenderer for {meshObj.name}. Retrying.");
            meshRenderer = meshObj.AddComponent<MeshRenderer>();
        }

        // Assign material safely
        if (meshRenderer != null)
        {
            meshRenderer.material = personMaterial != null ? personMaterial : new Material(Shader.Find("Standard"));
        }
        else
        {
            Debug.LogError($"[PersonMeshRenderer] MeshRenderer missing on {meshObj.name} after creation!");
        }

        // Prepare the mesh
        Mesh mesh = new Mesh
        {
            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
        };
        meshFilter.mesh = mesh;

        // Cache
        activeMeshes[bodyId] = new PersonMeshObject(meshObj, mesh, meshFilter, meshRenderer);
    }

    void UpdatePersonMesh(PersonMeshData personMesh)
    {
        if (!activeMeshes.ContainsKey(personMesh.BodyId))
            return;

        var meshObj = activeMeshes[personMesh.BodyId];
        var mesh = meshObj.Mesh;

        mesh.Clear();
        mesh.SetVertices(personMesh.Vertices);
        if (personMesh.Triangles?.Count > 0)
        {
            var tris = personMesh.Triangles.ToArray();
            for (int i = 0; i < tris.Length; i += 3)
                (tris[i], tris[i + 1]) = (tris[i + 1], tris[i]);
            mesh.SetTriangles(tris, 0);
        }
        mesh.SetUVs(0, personMesh.UVs);
        mesh.SetColors(personMesh.Colors);

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    void RemovePersonMeshObject(uint bodyId)
    {
        if (activeMeshes.TryGetValue(bodyId, out var meshObj))
        {
            Destroy(meshObj.GameObject);
            activeMeshes.Remove(bodyId);
        }
    }
}

public class PersonMeshObject
{
    public GameObject GameObject;
    public Mesh Mesh;
    public MeshFilter MeshFilter;
    public MeshRenderer MeshRenderer;

    public PersonMeshObject(GameObject go, Mesh mesh, MeshFilter mf, MeshRenderer mr)
    {
        GameObject = go;
        Mesh = mesh;
        MeshFilter = mf;
        MeshRenderer = mr;
    }
}
