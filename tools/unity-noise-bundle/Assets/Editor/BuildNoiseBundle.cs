using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

public static class BuildNoiseBundle
{
    public static void Build()
    {
        string output = Environment.GetEnvironmentVariable("PEV_NOISE_BUNDLE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) throw new InvalidOperationException("PEV_NOISE_BUNDLE_OUTPUT is required");
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var shaderPaths = Directory.GetFiles("Assets/Generated", "*.shader").Select(p => p.Replace('\\', '/')).OrderBy(p => p).ToArray();
        if (shaderPaths.Length != 10) throw new InvalidOperationException("Expected ten generated noise shaders");
        const string computePath = "Assets/NoiseFieldCoverage.compute";
        var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(computePath);
        if (compute == null) throw new InvalidOperationException("Compute shader import failed");
        var assets = shaderPaths.Concat(new[] { computePath }).ToArray();
        foreach (var path in shaderPaths)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null) throw new InvalidOperationException("Shader import failed: " + path);
            foreach (var message in ShaderUtil.GetShaderMessages(shader))
                if (message.severity == ShaderCompilerMessageSeverity.Error)
                    throw new InvalidOperationException(path + ": " + message.message);
        }
        foreach (var message in ShaderUtil.GetComputeShaderMessages(compute))
            if (message.severity == ShaderCompilerMessageSeverity.Error)
                throw new InvalidOperationException(computePath + ": " + message.message);
        var build = new AssetBundleBuild { assetBundleName = "noise-field.bundle", assetNames = assets };
        var manifest = BuildPipeline.BuildAssetBundles(output, new[] { build },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode,
            BuildTarget.StandaloneWindows64);
        if (manifest == null || !File.Exists(Path.Combine(output, build.assetBundleName)))
            throw new InvalidOperationException("Noise bundle build failed");
        // Compilation can occur during bundle creation; inspect diagnostics again.
        foreach (var path in shaderPaths)
            foreach (var message in ShaderUtil.GetShaderMessages(AssetDatabase.LoadAssetAtPath<Shader>(path)))
                if (message.severity == ShaderCompilerMessageSeverity.Error)
                    throw new InvalidOperationException(path + ": " + message.message);
        foreach (var message in ShaderUtil.GetComputeShaderMessages(compute))
            if (message.severity == ShaderCompilerMessageSeverity.Error)
                throw new InvalidOperationException(computePath + ": " + message.message);
        Debug.Log("PEV noise draft bundle: " + Path.Combine(output, build.assetBundleName));
    }
}
