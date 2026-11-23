using System;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Runtime;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Stages the AR scene for a job imported by <see cref="ArSplatInboxWatcher"/>, the same way the
/// ground station stages its own (TriffidSceneStager in UnityGaussianSplattingAutoWatcher):
/// the tracked template <c>Assets/Scenes/TRIFFID.unity</c> is copied to the git-ignored
/// <c>Assets/Staged/TRIFFID_staged.unity</c>, every scene component in the copy is switched to the
/// job's paths in project_paths.local.json, and the same actions as the Inspector buttons run, so
/// the copy shows the job in Edit Mode and reloads it on Play. The template never holds job data.
///
/// The staged scene belongs to the pipeline: it is saved after every job, whatever its modified
/// flag says (scene components mark it modified just by loading), so changes made by hand to it are
/// overwritten by the next job. Lasting changes belong in the template.
/// </summary>
internal static class ArSceneStager
{
    private const string TemplateScenePath = "Assets/Scenes/TRIFFID.unity";
    private const string StagedFolder = "Assets/Staged";
    internal const string ScenePath = StagedFolder + "/TRIFFID_staged.unity";
    private const string LogPrefix = "[ArSceneStager]";
    private static readonly HashSet<string> StartupSceneObjects = new(StringComparer.Ordinal)
    {
        "Main Camera",
        "Directional Light",
        "Global Volume",
    };

    // Scene-side paths for one job, in the form they were written to project_paths.local.json.
    // Every input is required; staging fails instead of keeping an earlier job's file.
    internal sealed class SceneInputs
    {
        public JObject Job;
        public string GaussianAssetPath;
        public string MeshPath;
        public string TransformPath;
    }

    internal enum StageResult
    {
        Saved,
        Failed,
    }

    public static StageResult Stage(SceneInputs inputs, out string problem)
    {
        try
        {
            return StageScene(inputs, out problem);
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            Debug.LogError($"{LogPrefix} Staging '{ScenePath}' failed: {ex}");
            return StageResult.Failed;
        }
    }

    private static StageResult StageScene(SceneInputs inputs, out string problem)
    {
        problem = null;
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(TemplateScenePath) == null)
        {
            problem = $"Scene template '{TemplateScenePath}' does not exist.";
            Debug.LogError($"{LogPrefix} {problem}");
            return StageResult.Failed;
        }

        // An open staged scene is staged in place, so whoever is looking at it sees the new job.
        // Otherwise it is refreshed from the template, so nothing of an earlier job carries over.
        Scene scene = SceneManager.GetSceneByPath(ScenePath);
        bool closeWhenDone = false;
        if (!scene.isLoaded)
        {
            if (!AssetDatabase.IsValidFolder(StagedFolder))
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(StagedFolder));
            if (!AssetDatabase.CopyAsset(TemplateScenePath, ScenePath))
            {
                problem = $"Could not copy '{TemplateScenePath}' to '{ScenePath}'.";
                Debug.LogError($"{LogPrefix} {problem}");
                return StageResult.Failed;
            }

            if (CanShowScene())
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }
            else
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
                closeWhenDone = true;
            }
        }

        try
        {
            // Order matters: the aligner is pointed at the new geolocal JSON and the mesh is in place
            // before the rotation is applied and the collider is synced onto the mesh.
            StageGaussianRenderers(scene, inputs);
            StageRotationAligners(scene, inputs);
            StageJsonSpawners(scene, inputs);
            StageMeshLoaders(scene, inputs);
            ApplyAlignment(scene);
            StageDetections(scene, inputs);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new IOException($"Could not save '{ScenePath}'.");
        }
        catch
        {
            DiscardPartialStaging(scene, closeWhenDone);
            throw;
        }

        if (closeWhenDone)
        {
            EditorSceneManager.CloseScene(scene, removeScene: true);
            Debug.Log($"{LogPrefix} Another scene is open, so '{ScenePath}' was staged in the background. Open it to see the job.");
        }

        Debug.Log($"{LogPrefix} Staged '{ScenePath}' for job '{inputs.Job?["job_id"]}'.");
        return StageResult.Saved;
    }

    // The staged scene replaces what the Editor shows only when nothing would be lost: a single
    // open scene that is either the empty startup scene or the template without unsaved changes.
    // A scene someone is working in is never replaced; the job is then staged in the background.
    private static bool CanShowScene()
    {
        if (SceneManager.sceneCount != 1)
            return false;

        Scene active = SceneManager.GetActiveScene();
        if (active.path == TemplateScenePath)
            return !active.isDirty;
        if (!string.IsNullOrEmpty(active.path))
            return false;

        // The startup scene is recognised by its contents: its dirty flag can be set without
        // anyone having edited it.
        foreach (var root in active.GetRootGameObjects())
        {
            if (!StartupSceneObjects.Contains(root.name))
                return false;
        }

        return true;
    }

    // Leaves no half-applied job behind: the staged scene is reloaded from its last save (the
    // previous job), discarding the partial one.
    private static void DiscardPartialStaging(Scene scene, bool closeWhenDone)
    {
        if (closeWhenDone)
            EditorSceneManager.CloseScene(scene, removeScene: true);
        else if (SceneManager.sceneCount == 1)
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        else
            Debug.LogError($"{LogPrefix} '{ScenePath}' still holds a partly staged job because other scenes are open with it; reopen it to discard the partial job.");
    }

    private static void StageGaussianRenderers(Scene scene, SceneInputs inputs)
    {
        if (inputs.GaussianAssetPath == null)
            throw new InvalidOperationException("The job has no Gaussian asset; the scene would keep an earlier job's splat.");

        var renderers = FindInScene<GaussianSplatRenderer>(scene);
        if (renderers.Count == 0)
            throw new InvalidOperationException($"'{TemplateScenePath}' has no GaussianSplatRenderer to stage the splat into.");

        foreach (var renderer in renderers)
        {
            var so = new SerializedObject(renderer);
            SetBool(so, "m_LoadAssetFromPath", true);
            SetString(so, "m_AssetPath", inputs.GaussianAssetPath);
            SetEnum(so, "m_InputPathRoot", (int)ProjectPathResolver.PathRoot.ProjectRoot);
            SetBool(so, "m_UseSharedAssetPathJson", true);
            SetString(so, "m_SharedAssetPathsJsonFile", ArSplatInboxWatcher.ProjectPathsJsonFileName);
            SetString(so, "m_AssetPathJsonKey", ArSplatInboxWatcher.GaussianAssetPathJsonKey);
            so.ApplyModifiedProperties();

            Undo.RecordObject(renderer, "Stage Gaussian asset");
            renderer.LoadAssetFromConfiguredPath();
        }
    }

    private static void StageRotationAligners(Scene scene, SceneInputs inputs)
    {
        if (inputs.TransformPath == null)
            throw new InvalidOperationException("The job has no geolocal transform JSON; the scene would keep an earlier job's alignment.");

        foreach (var aligner in FindInScene<ColmapGaussianRotationAligner>(scene))
        {
            var so = new SerializedObject(aligner);
            SetString(so, "transformJsonPath", inputs.TransformPath);
            SetEnum(so, "inputPathRoot", (int)ProjectPathResolver.PathRoot.ProjectRoot);
            SetBool(so, "useSharedTransformPathJson", true);
            SetString(so, "sharedTransformPathsJsonFile", ArSplatInboxWatcher.ProjectPathsJsonFileName);
            SetString(so, "transformPathJsonKey", ArSplatInboxWatcher.ColmapTransformPathJsonKey);
            so.ApplyModifiedProperties();
        }
    }

    // The JSON spawner converts GeoJSON annotations to scene positions with the same geolocal
    // JSON, so it reads the aligner's key. It only spawns on Play; here the path is validated.
    private static void StageJsonSpawners(Scene scene, SceneInputs inputs)
    {
        foreach (var spawner in FindInScene<JsonSpawner>(scene))
        {
            var so = new SerializedObject(spawner);
            SetString(so, "transformJsonPath", inputs.TransformPath);
            SetBool(so, "useSharedTransformPathJson", true);
            SetString(so, "sharedTransformPathsJsonFile", ArSplatInboxWatcher.ProjectPathsJsonFileName);
            SetString(so, "transformPathJsonKey", ArSplatInboxWatcher.ColmapTransformPathJsonKey);
            so.ApplyModifiedProperties();

            spawner.ValidateSharedTransformPath();
        }
    }

    private static void StageMeshLoaders(Scene scene, SceneInputs inputs)
    {
        if (inputs.MeshPath == null)
            throw new InvalidOperationException("The job has no mesh OBJ; the scene would keep an earlier job's mesh.");

        foreach (var loader in FindInScene<MeshPathJsonLoader>(scene))
        {
            var so = new SerializedObject(loader);
            SetString(so, "meshPath", inputs.MeshPath);
            SetEnum(so, "inputPathRoot", (int)ProjectPathResolver.PathRoot.ProjectRoot);
            SetBool(so, "useSharedMeshPathJson", true);
            SetString(so, "sharedMeshPathsJsonFile", ArSplatInboxWatcher.ProjectPathsJsonFileName);
            SetString(so, "meshPathJsonKey", ArSplatInboxWatcher.MeshPathJsonKey);
            so.ApplyModifiedProperties();

            GameObject target = so.FindProperty("targetObject")?.objectReferenceValue as GameObject ?? loader.gameObject;
            Undo.RegisterFullObjectHierarchyUndo(target, "Stage mesh");
            loader.LoadAndApplyMesh();

            // An OBJ parsed from disk is a scene-local Mesh; keep it out of the saved scene so the
            // file does not grow by the whole mesh. The loader reloads it on Play.
            var filter = target.GetComponent<MeshFilter>();
            Mesh mesh = filter != null ? filter.sharedMesh : target.GetComponent<MeshCollider>()?.sharedMesh;
            if (mesh != null && !EditorUtility.IsPersistent(mesh))
                mesh.hideFlags |= HideFlags.DontSaveInEditor;

            if (!so.FindProperty("loadOnStart").boolValue && !so.FindProperty("loadOnEnable").boolValue)
                Debug.LogWarning($"{LogPrefix} '{loader.name}' has neither Load On Start nor Load On Enable set; the mesh will not load in Play Mode.", loader);
        }
    }

    // Runs after the mesh is staged so the collider lands on the new mesh.
    private static void ApplyAlignment(Scene scene)
    {
        foreach (var aligner in FindInScene<ColmapGaussianRotationAligner>(scene))
        {
            var moved = new List<UnityEngine.Object>();
            if (aligner.gaussianRenderer != null)
                moved.Add(aligner.gaussianRenderer);
            if (aligner.mapColliderSync != null && aligner.mapColliderSync.MeshColliderObject != null)
                moved.Add(aligner.mapColliderSync.MeshColliderObject);
            if (moved.Count > 0)
                Undo.RecordObjects(moved.ToArray(), "Stage geolocal alignment");

            aligner.ApplyJsonRotationAndSyncCollider();
        }
    }

    // Detections (3D detections from the ground station) are not part of the AR job yet. This is
    // where they get wired once it carries them.
    private static void StageDetections(Scene scene, SceneInputs inputs)
    {
    }

    private static List<T> FindInScene<T>(Scene scene) where T : Component
    {
        var found = new List<T>();
        foreach (var root in scene.GetRootGameObjects())
            found.AddRange(root.GetComponentsInChildren<T>(includeInactive: true));
        return found;
    }

    private static SerializedProperty FindProperty(SerializedObject so, string name)
    {
        var property = so.FindProperty(name);
        if (property == null)
            throw new InvalidOperationException($"'{so.targetObject.GetType().Name}' has no serialized field '{name}', so the job could not be staged into it.");
        return property;
    }

    private static void SetString(SerializedObject so, string name, string value)
    {
        FindProperty(so, name).stringValue = value;
    }

    private static void SetBool(SerializedObject so, string name, bool value)
    {
        FindProperty(so, name).boolValue = value;
    }

    private static void SetEnum(SerializedObject so, string name, int value)
    {
        FindProperty(so, name).enumValueIndex = value;
    }
}
