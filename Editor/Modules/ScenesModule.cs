using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.ProjectAuditor.Editor.Utils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

#if ADDRESSABLES_ANALYZER_SUPPORT
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
#endif

namespace Unity.ProjectAuditor.Editor.Auditors
{
    public enum ModelProperty
    {
        IndexFormat = 0,
        NumIndices,
        NumVertices,
        NumSubMeshes,
        Readable,
        Num
    }

    public enum SceneProperty
    {
        NumObjects = 0,
        NumPrefabs,
        NumMaterials,
        NumShaders,
        NumTextures,
        Num
    }

    struct AssetUsageStats
    {
        public int objects;
        public int prefabs;
        public int materials;
        public int models;
        public int shaders;
        public int textures;
    }

    class SceneStatsCollector
    {
        static readonly ProblemDescriptor k_Descriptor = new ProblemDescriptor
            (
            700003,
            "Mesh Stats"
            );

        int m_NumObjects;
        Dictionary<string, int> m_Materials = new Dictionary<string, int>();
        Dictionary<string, int> m_Meshes = new Dictionary<string, int>();
        Dictionary<string, int> m_Models = new Dictionary<string, int>();
        Dictionary<string, int> m_Prefabs = new Dictionary<string, int>();
        Dictionary<string, int> m_Shaders = new Dictionary<string, int>();
        Dictionary<int, int> m_Textures = new Dictionary<int, int>();

        public Action<ProjectIssue> onIssueFound;

        public void Collect(Scene scene)
        {
            foreach (var go in scene.GetRootGameObjects())
            {
                Collect(go);
            }
        }

        public void Collect(GameObject go)
        {
            m_NumObjects++;

            CollectGameObjectMaterials(go);
            CollectModels(go);

            if (PrefabUtility.GetPrefabInstanceStatus(go) != PrefabInstanceStatus.NotAPrefab)
            {
                if (PrefabUtility.GetNearestPrefabInstanceRoot(go) == go)
                {
                    var prefabAssetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                    if (!m_Prefabs.ContainsKey(prefabAssetPath))
                    {
                        m_Prefabs.Add(prefabAssetPath, 0);
                    }

                    m_Prefabs[prefabAssetPath]++;
                }
            }

            foreach (Transform childTransform in go.transform)
            {
                Collect(childTransform.gameObject);
            }
        }

        void CollectGameObjectMaterials(GameObject go)
        {
            var renderers = go.GetComponents<Renderer>();
            foreach (var material in renderers.SelectMany(r => r.sharedMaterials))
            {
                if (material == null)
                    continue;

                CollectMaterial(material);
            }
        }

        public void CollectMaterial(Material material)
        {
            var assetPath = AssetDatabase.GetAssetPath(material);
            if (string.IsNullOrEmpty(assetPath))
                return;
            if (!m_Materials.ContainsKey(assetPath))
            {
                var shader = material.shader;
                if (shader == null)
                    return;

                CollectShader(shader);

#if UNITY_2019_3_OR_NEWER
                for (int i = 0; i < shader.GetPropertyCount(); i++)
                {
                    if (shader.GetPropertyType(i) == ShaderPropertyType.Texture)
                    {
                        var texture = material.GetTexture(shader.GetPropertyName(i));
                        if (texture == null)
                            continue;

                        CollectTexture(texture);
                    }
                }
#endif

                m_Materials.Add(assetPath, 0);
            }

            m_Materials[assetPath]++;
        }

        void CollectModels(GameObject go)
        {
            var meshFilters = go.GetComponents<MeshFilter>();
            foreach (var mesh in meshFilters)
            {
                if (mesh == null)
                    continue;

                var assetPath = AssetDatabase.GetAssetPath(mesh);
                if (string.IsNullOrEmpty(assetPath))
                    continue;
                if (!m_Models.ContainsKey(assetPath))
                {
                    m_Models.Add(assetPath, 0);
                }
                m_Models[assetPath]++;
            }
        }

        public void CollectAllModels()
        {
            var meshAssetPaths = AssetDatabase.FindAssets("t:model").Select(AssetDatabase.GUIDToAssetPath).ToArray();
            foreach (var assetPath in meshAssetPaths)
            {
                var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
                if (mesh == null)
                    continue; // skip animation-only fbx
                onIssueFound(new ProjectIssue(k_Descriptor, Path.GetFileNameWithoutExtension(assetPath), ScenesModule.k_MeshLayout.category, new Location(assetPath),
                    new object[(int)ModelProperty.Num]
                    {
                        mesh.indexFormat,
                        CalcTotalIndices(mesh),
                        mesh.vertexCount,
                        mesh.subMeshCount,
                        mesh.isReadable
                    }));
            }
        }

        public void CollectShader(Shader shader)
        {
            var shaderName = shader.name;
            if (!m_Shaders.ContainsKey(shaderName))
                m_Shaders.Add(shaderName, 0);

            m_Shaders[shaderName]++;
        }

        public void CollectTexture(Texture texture)
        {
            var id = texture.GetInstanceID();
            if (!m_Textures.ContainsKey(id))
                m_Textures.Add(id, 0);

            m_Textures[id]++;
        }

        static int CalcTotalIndices(Mesh mesh)
        {
            var totalCount = 0;
            for (var i = 0; i < mesh.subMeshCount; i++)
                totalCount += (int)mesh.GetIndexCount(i);
            return totalCount;
        }

        public void Merge(SceneStatsCollector other)
        {
            m_NumObjects += other.m_NumObjects;
            m_Materials = other.m_Materials.Concat(m_Materials).GroupBy(d => d.Key).ToDictionary(d => d.Key, d => d.First().Value);
            m_Prefabs = other.m_Prefabs.Concat(m_Prefabs).GroupBy(d => d.Key).ToDictionary(d => d.Key, d => d.First().Value);
            m_Shaders = other.m_Shaders.Concat(m_Shaders).GroupBy(d => d.Key).ToDictionary(d => d.Key, d => d.First().Value);
            m_Textures = other.m_Textures.Concat(m_Textures).GroupBy(d => d.Key).ToDictionary(d => d.Key, d => d.First().Value);
        }

        public AssetUsageStats GetStats()
        {
            var stats = new AssetUsageStats
            {
                objects = m_NumObjects,
                materials = m_Materials.Count,
                models = m_Models.Count,
                shaders = m_Shaders.Count,
                textures = m_Textures.Count,
                prefabs = m_Prefabs.Count
            };

            return stats;
        }
    }

    class ScenesModule : IProjectAuditorModule
    {
        static readonly ProblemDescriptor k_Descriptor = new ProblemDescriptor
            (
            700002,
            "Scene Stats"
            );

        internal static readonly IssueLayout k_MeshLayout = new IssueLayout
        {
            category = ProjectAuditor.GetOrRegisterCategory("Models"),
            properties = new[]
            {
                new PropertyDefinition { type = PropertyType.Description, name = "Model Name"},
                new PropertyDefinition { type = PropertyTypeUtil.FromCustom(ModelProperty.IndexFormat), format = PropertyFormat.String, name = "Index Format"},
                new PropertyDefinition { type = PropertyTypeUtil.FromCustom(ModelProperty.NumIndices), format = PropertyFormat.Integer, name = "Num Indices"},
                new PropertyDefinition { type = PropertyTypeUtil.FromCustom(ModelProperty.NumVertices), format = PropertyFormat.Integer, name = "Num Vertices"},
                new PropertyDefinition { type = PropertyTypeUtil.FromCustom(ModelProperty.NumSubMeshes), format = PropertyFormat.Integer, name = "Num Sub-Meshes"},
                new PropertyDefinition { type = PropertyTypeUtil.FromCustom(ModelProperty.Readable), format = PropertyFormat.Bool, name = "Readable"},
                new PropertyDefinition { type = PropertyType.Path, name = "Path"},
            }
        };

        static readonly IssueLayout k_SceneLayout = new IssueLayout
        {
            category = ProjectAuditor.GetOrRegisterCategory("Scenes"),
            properties = new[]
            {
                new PropertyDefinition { type = PropertyType.Description, name = "Scene Name"},
                new PropertyDefinition { type = PropertyTypeUtil.FromCustom(SceneProperty.NumObjects), format = PropertyFormat.Integer, name = "Num Objects", longName = "Num Objects"},
                new PropertyDefinition { type = PropertyTypeUtil.FromCustom(SceneProperty.NumPrefabs), format = PropertyFormat.Integer, name = "Num Prefabs", longName = "Num Unique Prefabs"},
                new PropertyDefinition { type = PropertyTypeUtil.FromCustom(SceneProperty.NumMaterials), format = PropertyFormat.Integer, name = "Num Materials", longName = "Num Unique Materials"},
                new PropertyDefinition { type = PropertyTypeUtil.FromCustom(SceneProperty.NumShaders), format = PropertyFormat.Integer, name = "Num Shaders", longName = "Num Unique Shaders"},
                new PropertyDefinition { type = PropertyTypeUtil.FromCustom(SceneProperty.NumTextures), format = PropertyFormat.Integer, name = "Num Textures", longName = "Num Unique Textures"},
            }
        };

        public IEnumerable<ProblemDescriptor> GetDescriptors()
        {
            throw new NotImplementedException();
        }

        public IEnumerable<IssueLayout> GetLayouts()
        {
            yield return k_MeshLayout;
            yield return k_SceneLayout;
        }

        public void Initialize(ProjectAuditorConfig config)
        {
        }

        public bool IsSupported()
        {
            return true;
        }

        public void RegisterDescriptor(ProblemDescriptor descriptor)
        {
            throw new NotImplementedException();
        }

        public void Audit(Action<ProjectIssue> onIssueFound, Action onComplete = null, IProgress progress = null)
        {
            if (progress != null)
                progress.Start("Analyzing Scenes in Build Settings", "Collecting statistics",
                    EditorBuildSettings.scenes.Length);
            var prevSceneSetups =  EditorSceneManager.GetSceneManagerSetup();

            var globalCollector = new SceneStatsCollector();
            globalCollector.onIssueFound = onIssueFound;

            globalCollector.CollectAllModels();

            foreach (var editorBuildSettingsScene in EditorBuildSettings.scenes)
            {
                var path = editorBuildSettingsScene.path;
                if (progress != null)
                    progress.Advance(path);

                // skip scene if it does not contribute to the build
                if (!editorBuildSettingsScene.enabled)
                    continue;

                AnalyzeScene(onIssueFound, path, globalCollector);
            }

            // restore previously-loaded scenes
            if (prevSceneSetups.Length > 0)
                EditorSceneManager.RestoreSceneManagerSetup(prevSceneSetups);

            var globalStats = globalCollector.GetStats();

            Debug.Log("Total GameObjects: " + globalStats.objects);
            Debug.Log("Unique Prefabs: "  + globalStats.prefabs);
            Debug.Log("Unique Materials: "  + globalStats.materials);
            Debug.Log("Unique Shaders: "  + globalStats.shaders);
            Debug.Log("Unique Textures: "  + globalStats.textures);

            if (progress != null)
                progress.Clear();

#if ADDRESSABLES_ANALYZER_SUPPORT
            AuditAddressables(onIssueFound, globalCollector);
#endif
            if (onComplete != null)
                onComplete();
        }

        static void AnalyzeScene(Action<ProjectIssue> onIssueFound, string path, SceneStatsCollector globalCollector)
        {
            if (!File.Exists(path))
                return;

            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var collector = new SceneStatsCollector();

            collector.Collect(scene);

            globalCollector.Merge(collector);

            var stats = collector.GetStats();
            onIssueFound(new ProjectIssue(
                k_Descriptor,
                path,
                k_SceneLayout.category,
                path,
                new object[(int)SceneProperty.Num]
                {
                    stats.objects,
                    stats.prefabs,
                    stats.materials,
                    stats.shaders,
                    stats.textures,
                }));
        }

#if ADDRESSABLES_ANALYZER_SUPPORT
        void AuditAddressables(Action<ProjectIssue> onIssueFound, SceneStatsCollector globalCollector)
        {
            var assets = new List<AddressableAssetEntry>();
            foreach (var group in AddressableAssetSettingsDefaultObject.Settings.groups)
            {
                foreach (var entry in group.entries)
                    entry.GatherAllAssets(assets, true, true, true);
            }
            foreach (var entry in assets)
            {
                if (entry.IsScene)
                    AnalyzeScene(onIssueFound, entry.AssetPath, globalCollector);
                else
                {
                    var type = AssetDatabase.GetMainAssetTypeAtPath(entry.AssetPath);
                    if (typeof(UnityEngine.GameObject).IsAssignableFrom(type))
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(entry.AssetPath);
                        globalCollector.Collect(obj);
                    }
                    else if (typeof(UnityEngine.Material).IsAssignableFrom(type))
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(entry.AssetPath);
                        globalCollector.CollectMaterial(obj);
                    }
                    else if (typeof(UnityEngine.Shader).IsAssignableFrom(type))
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Shader>(entry.AssetPath);
                        globalCollector.CollectShader(obj);
                    }
                    else if (typeof(UnityEngine.Texture).IsAssignableFrom(type))
                    {
                        var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Texture>(entry.AssetPath);
                        globalCollector.CollectTexture(obj);
                    }
                }
            }
        }

#endif
    }
}
