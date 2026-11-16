using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildProbeBundle
{
    public static void Build()
    {
        string output = Environment.GetEnvironmentVariable("PEV_PROBE_BUNDLE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException("Set PEV_PROBE_BUNDLE_OUTPUT to an output directory.");
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var build = new AssetBundleBuild
        {
            assetBundleName = "pev-depth-probe",
            assetNames = new[] { "Assets/DepthProbeGrab.shader", "Assets/DepthProbeNoGrab.shader" }
        };
        var manifest = BuildPipeline.BuildAssetBundles(output, new[] { build },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode,
            BuildTarget.StandaloneWindows64);
        if (manifest == null || !File.Exists(Path.Combine(output, build.assetBundleName)))
            throw new InvalidOperationException("Depth probe bundle build failed.");
        Debug.Log("PEV depth probe bundle: " + Path.Combine(output, build.assetBundleName));
    }
}
