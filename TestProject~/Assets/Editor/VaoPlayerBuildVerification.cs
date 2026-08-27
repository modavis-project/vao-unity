using System;
using System.IO;
using Modavis.Vao;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

internal static class VaoPlayerBuildVerification
{
    public static void BuildMacIl2Cpp()
    {
        const string scenePath = "Assets/__VaoPlayerBuildVerification.unity";
        var output = Environment.GetEnvironmentVariable("VAO_IL2CPP_BUILD_PATH");
        if (string.IsNullOrWhiteSpace(output)) output = Path.Combine(Path.GetTempPath(), "vao-unity-il2cpp", "VAOVerification.app");
        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? Path.GetTempPath());

        var previousBackend = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone);
        try
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("VAO IL2CPP verification");
            root.AddComponent<AudioListener>();
            root.AddComponent<VaoRuntimeObject>();
            root.AddComponent<VaoSamplePlayer>();
            root.AddComponent<VaoMidiRouter>();
            root.AddComponent<VaoLinkedAnimationPlayer>();
            root.AddComponent<VaoMediaPlayer>();
            root.AddComponent<VaoTrackedPlacement>();
            root.AddComponent<VaoConvolutionRenderer>();
            root.AddComponent<VaoAcousticEnvironment>();
            root.AddComponent<VaoSpatialAnchor>();
            EditorSceneManager.SaveScene(scene, scenePath);

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = output,
                target = BuildTarget.StandaloneOSX,
                options = BuildOptions.CleanBuildCache
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new BuildFailedException($"VAO IL2CPP verification failed: {report.summary.result} ({report.summary.totalErrors} errors).");
            Debug.Log($"VAO_IL2CPP_BUILD_SUCCEEDED path={output} bytes={report.summary.totalSize}");
        }
        finally
        {
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, previousBackend);
            AssetDatabase.DeleteAsset(scenePath);
            AssetDatabase.SaveAssets();
        }
    }
}
