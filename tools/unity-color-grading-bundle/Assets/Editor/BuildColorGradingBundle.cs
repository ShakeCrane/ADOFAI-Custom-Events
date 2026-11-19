using System;
using System.IO;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

/// <summary>
/// Builds the colour grading shader bundle in an isolated project.
///
/// The bundle is the only shipped artefact the game cannot produce itself: Unity compiles
/// ShaderLab, and the game has no editor. Keeping this project free of package dependencies
/// means the build works offline, on the same editor version the game runs.
/// </summary>
public static class BuildColorGradingBundle
{
    public static void Build()
    {
        string output = Environment.GetEnvironmentVariable("PEV_GRADING_BUNDLE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("PEV_GRADING_BUNDLE_OUTPUT is required");
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);

        const string shaderPath = "Assets/Generated/ColorGrading.shader";
        var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
        if (shader == null) throw new InvalidOperationException("Shader import failed: " + shaderPath);
        foreach (var message in ShaderUtil.GetShaderMessages(shader))
            if (message.severity == ShaderCompilerMessageSeverity.Error)
                throw new InvalidOperationException(shaderPath + ": " + message.message);

        var build = new AssetBundleBuild
        {
            assetBundleName = "color-grading.bundle",
            assetNames = new[] { shaderPath }
        };
        var manifest = BuildPipeline.BuildAssetBundles(output, new[] { build },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode,
            BuildTarget.StandaloneWindows64);
        if (manifest == null || !File.Exists(Path.Combine(output, build.assetBundleName)))
            throw new InvalidOperationException("Colour grading bundle build failed");

        // Compilation can be deferred to bundle creation; inspect again afterwards so a
        // shader error cannot be reported as a successful build.
        foreach (var message in ShaderUtil.GetShaderMessages(AssetDatabase.LoadAssetAtPath<Shader>(shaderPath)))
            if (message.severity == ShaderCompilerMessageSeverity.Error)
                throw new InvalidOperationException(shaderPath + ": " + message.message);

        Debug.Log("PEV colour grading bundle: " + Path.Combine(output, build.assetBundleName));
    }
}
