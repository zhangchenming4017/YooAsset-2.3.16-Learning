#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using YooAsset;
using YooAsset.Editor;

public static class PrintEditorSimulatePackageRoot
{
    [MenuItem("YooAsset/Debug/Print EditorSimulate packageRoot")]
    public static void Print()
    {
        string packageName = "DefaultPackage";
        var param = new PackageInvokeBuildParam(packageName)
        {
            BuildPipelineName = "EditorSimulateBuildPipeline"
        };
        var result = AssetBundleSimulateBuilder.SimulateBuild(param);
        string packageRoot = result.PackageRootDirectory;

        Debug.Log($"packageRoot = {packageRoot}");

        string version = Path.Combine(packageRoot, YooAssetSettingsData.GetPackageVersionFileName(packageName));
        string hash = Path.Combine(packageRoot, YooAssetSettingsData.GetPackageHashFileName(packageName, "Simulate"));
        string manifest = Path.Combine(packageRoot, YooAssetSettingsData.GetManifestBinaryFileName(packageName, "Simulate"));

        Debug.Log($"version exists = {File.Exists(version)} : {version}");
        Debug.Log($"hash    exists = {File.Exists(hash)} : {hash}");
        Debug.Log($"manifest exists = {File.Exists(manifest)} : {manifest}");

        // 新增：检查依赖缓存文件（YooAsset 自己的 DB，不是 Unity 的）
        string dbPath = "Library/AssetDependencyDB";
        bool dbExists = File.Exists(dbPath);
        Debug.Log($"AssetDependencyDB exists = {dbExists} : {Path.GetFullPath(dbPath)}");
        if (dbExists)
        {
            long size = new FileInfo(dbPath).Length;
            Debug.Log($"AssetDependencyDB size = {size} bytes");

            // 读取头部信息：版本 + 条目数，验证内容是否有效
            try
            {
                using (var fs = File.OpenRead(dbPath))
                using (var br = new BinaryReader(fs))
                {
                    string fileVersion = br.ReadString();
                    int count = br.ReadInt32();
                    Debug.Log($"AssetDependencyDB header -> version: {fileVersion}, entries: {count}");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Read AssetDependencyDB failed: {e.Message}");
            }
        }
        else
        {
            Debug.LogWarning("AssetDependencyDB not found. 若未看到你在 AssetDependencyCache 中的日志，说明未以 true 构造 AssetDependencyCache 或流程未走到保存。");
        }
    }
}
#endif