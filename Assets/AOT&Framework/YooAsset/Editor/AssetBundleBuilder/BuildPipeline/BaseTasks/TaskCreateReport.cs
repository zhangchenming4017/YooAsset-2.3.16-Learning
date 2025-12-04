using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace YooAsset.Editor
{
    /// <summary>
    /// 用途：
    ///<para>构建审计与对账：核对本次打包的输入配置与输出统计，验证加密、压缩、命名策略是否生效。</para>
    ///<para>依赖分析与优化：查看资源级与包级依赖，识别共享过度、循环/冗余依赖、游离包等问题，指导分包与收集策略优化。</para>
    ///<para>问题排查：定位某个资源属于哪个包、包内具体包含哪些资源、为何体积增大、哪些包被大量引用等。</para>
    ///<para>历史对比与发布记录：与上次报告比对（包数/大小/哈希变化），作为发布留档。</para>
    ///<para>自动化检查：可被工具读取做门禁（如加密覆盖率、包大小阈值、依赖数量上限等）。</para>
    /// </summary>
    public class TaskCreateReport
    {
        protected void CreateReportFile(BuildParametersContext buildParametersContext, BuildMapContext buildMapContext, ManifestContext manifestContext)
        {
            var buildParameters = buildParametersContext.Parameters;

            string packageOutputDirectory = buildParametersContext.GetPackageOutputDirectory();
            PackageManifest manifest = manifestContext.Manifest;
            BuildReport buildReport = new BuildReport();

            // 概述信息
            {
                // 版本与环境：YooAsset版本、Unity版本、构建日期、耗时、目标平台、构建管线、打包类型、包名/版本/备注
                buildReport.Summary.YooVersion = EditorTools.GetPackageManagerYooVersion();
                buildReport.Summary.UnityVersion = UnityEngine.Application.unityVersion;
                buildReport.Summary.BuildDate = DateTime.Now.ToString();
                buildReport.Summary.BuildSeconds = BuildRunner.TotalSeconds;
                buildReport.Summary.BuildTarget = buildParameters.BuildTarget;
                buildReport.Summary.BuildPipeline = buildParameters.BuildPipeline;
                buildReport.Summary.BuildBundleType = buildParameters.BuildBundleType;
                buildReport.Summary.BuildPackageName = buildParameters.PackageName;
                buildReport.Summary.BuildPackageVersion = buildParameters.PackageVersion;
                buildReport.Summary.BuildPackageNote = buildParameters.PackageNote;

                // 收集器配置：是否唯一包名、是否 Addressable、是否扩展名可省略、位置转小写、是否包含GUID、自动收集着色器、忽略规则类型
                buildReport.Summary.UniqueBundleName = buildMapContext.Command.UniqueBundleName;
                buildReport.Summary.EnableAddressable = buildMapContext.Command.EnableAddressable;
                buildReport.Summary.SupportExtensionless = buildMapContext.Command.SupportExtensionless;
                buildReport.Summary.LocationToLower = buildMapContext.Command.LocationToLower;
                buildReport.Summary.IncludeAssetGUID = buildMapContext.Command.IncludeAssetGUID;
                buildReport.Summary.AutoCollectShaders = buildMapContext.Command.AutoCollectShaders;
                buildReport.Summary.IgnoreRuleName = buildMapContext.Command.IgnoreRule.GetType().FullName;

                // 构建参数：是否清理缓存、是否使用依赖数据库、是否启用共享分包、单引用资源是否独立、文件名风格、加密/清单处理/还原服务类型
                buildReport.Summary.ClearBuildCacheFiles = buildParameters.ClearBuildCacheFiles;
                buildReport.Summary.UseAssetDependencyDB = buildParameters.UseAssetDependencyDB;
                buildReport.Summary.EnableSharePackRule = buildParameters.EnableSharePackRule;
                buildReport.Summary.SingleReferencedPackAlone = buildParameters.SingleReferencedPackAlone;
                buildReport.Summary.FileNameStyle = buildParameters.FileNameStyle;
                buildReport.Summary.EncryptionServicesClassName = buildParameters.EncryptionServices == null ? "null" : buildParameters.EncryptionServices.GetType().FullName;
                buildReport.Summary.ManifestProcessServicesClassName = buildParameters.ManifestProcessServices == null ? "null" : buildParameters.ManifestProcessServices.GetType().FullName;
                buildReport.Summary.ManifestRestoreServicesClassName = buildParameters.ManifestRestoreServices == null ? "null" : buildParameters.ManifestRestoreServices.GetType().FullName;

                // SBP或Builtin特定参数：压缩选项、TypeTree相关开关、是否写入 link.xml、CacheServer配置、内置着色器包名、MonoScripts包名
                if (buildParameters is BuiltinBuildParameters)
                {
                    var builtinBuildParameters = buildParameters as BuiltinBuildParameters;
                    buildReport.Summary.CompressOption = builtinBuildParameters.CompressOption;
                    buildReport.Summary.DisableWriteTypeTree = builtinBuildParameters.DisableWriteTypeTree;
                    buildReport.Summary.IgnoreTypeTreeChanges = builtinBuildParameters.IgnoreTypeTreeChanges;
                }
                else if (buildParameters is ScriptableBuildParameters)
                {
                    var scriptableBuildParameters = buildParameters as ScriptableBuildParameters;
                    buildReport.Summary.CompressOption = scriptableBuildParameters.CompressOption;
                    buildReport.Summary.DisableWriteTypeTree = scriptableBuildParameters.DisableWriteTypeTree;
                    buildReport.Summary.IgnoreTypeTreeChanges = scriptableBuildParameters.IgnoreTypeTreeChanges;
                    buildReport.Summary.WriteLinkXML = scriptableBuildParameters.WriteLinkXML;
                    buildReport.Summary.CacheServerHost = scriptableBuildParameters.CacheServerHost;
                    buildReport.Summary.CacheServerPort = scriptableBuildParameters.CacheServerPort;
                    buildReport.Summary.BuiltinShadersBundleName = scriptableBuildParameters.BuiltinShadersBundleName;
                    buildReport.Summary.MonoScriptsBundleName = scriptableBuildParameters.MonoScriptsBundleName;
                }

                // 构建结果统计：资产文件总数、主资源总数、Bundle总数与总大小、加密Bundle数量与总大小
                buildReport.Summary.AssetFileTotalCount = buildMapContext.AssetFileCount;
                buildReport.Summary.MainAssetTotalCount = GetMainAssetCount(manifest);
                buildReport.Summary.AllBundleTotalCount = GetAllBundleCount(manifest);
                buildReport.Summary.AllBundleTotalSize = GetAllBundleSize(manifest);
                buildReport.Summary.EncryptedBundleTotalCount = GetEncryptedBundleCount(manifest);
                buildReport.Summary.EncryptedBundleTotalSize = GetEncryptedBundleSize(manifest);
            }

            // 资源对象列表：
            // 每个资源的 Address、AssetPath、AssetTags、GUID
            // 所属主包名与主包大小
            // 资源级依赖：依赖的其它资源列表、依赖的 Bundle 名列表
            buildReport.AssetInfos = new List<ReportAssetInfo>(manifest.AssetList.Count);
            foreach (var packageAsset in manifest.AssetList)
            {
                var mainBundle = manifest.BundleList[packageAsset.BundleID];
                ReportAssetInfo reportAssetInfo = new ReportAssetInfo();
                reportAssetInfo.Address = packageAsset.Address;
                reportAssetInfo.AssetPath = packageAsset.AssetPath;
                reportAssetInfo.AssetTags = packageAsset.AssetTags;
                reportAssetInfo.AssetGUID = AssetDatabase.AssetPathToGUID(packageAsset.AssetPath);
                reportAssetInfo.MainBundleName = mainBundle.BundleName;
                reportAssetInfo.MainBundleSize = mainBundle.FileSize;
                reportAssetInfo.DependAssets = GetAssetDependAssets(buildMapContext, mainBundle.BundleName, packageAsset.AssetPath);
                reportAssetInfo.DependBundles = GetAssetDependBundles(manifest, packageAsset);
                buildReport.AssetInfos.Add(reportAssetInfo);
            }

            // 资源包列表
            // 每个包的 BundleName、最终发布 FileName、FileHash、FileCRC、FileSize、是否加密、Tags
            // 包级依赖与反向引用包集合（DependBundles、ReferenceBundles）
            // 包内资源内容明细（BundleContents）
            buildReport.BundleInfos = new List<ReportBundleInfo>(manifest.BundleList.Count);
            foreach (var packageBundle in manifest.BundleList)
            {
                ReportBundleInfo reportBundleInfo = new ReportBundleInfo();
                reportBundleInfo.BundleName = packageBundle.BundleName;
                reportBundleInfo.FileName = packageBundle.FileName;
                reportBundleInfo.FileHash = packageBundle.FileHash;
                reportBundleInfo.FileCRC = packageBundle.FileCRC;
                reportBundleInfo.FileSize = packageBundle.FileSize;
                reportBundleInfo.Encrypted = packageBundle.Encrypted;
                reportBundleInfo.Tags = packageBundle.Tags;
                reportBundleInfo.DependBundles = GetBundleDependBundles(manifest, packageBundle);
                reportBundleInfo.ReferenceBundles = GetBundleReferenceBundles(manifest, packageBundle);
                reportBundleInfo.BundleContents = GetBundleContents(buildMapContext, packageBundle.BundleName);
                buildReport.BundleInfos.Add(reportBundleInfo);
            }

            // 其它资源列表：构建图中标记为独立的资源集合（供分析）
            buildReport.IndependAssets = new List<ReportIndependAsset>(buildMapContext.IndependAssets);

            // 序列化文件
            string fileName = YooAssetSettingsData.GetBuildReportFileName(buildParameters.PackageName, buildParameters.PackageVersion);
            string filePath = $"{packageOutputDirectory}/{fileName}";
            BuildReport.Serialize(filePath, buildReport);
            BuildLogger.Log($"Create build report file: {filePath}");
        }

        /// <summary>
        /// 获取资源对象依赖的其它所有资源
        /// </summary>
        private List<AssetInfo> GetAssetDependAssets(BuildMapContext buildMapContext, string bundleName, string assetPath)
        {
            List<AssetInfo> result = new List<AssetInfo>();
            var bundleInfo = buildMapContext.GetBundleInfo(bundleName);
            var assetInfo = bundleInfo.GetPackAssetInfo(assetPath);
            foreach (var dependAssetInfo in assetInfo.AllDependAssetInfos)
            {
                result.Add(dependAssetInfo.AssetInfo);
            }
            result.Sort();
            return result;
        }

        /// <summary>
        /// 获取资源对象依赖的资源包集合
        /// </summary>
        private List<string> GetAssetDependBundles(PackageManifest manifest, PackageAsset packageAsset)
        {
            List<string> dependBundles = new List<string>(packageAsset.DependBundleIDs.Length);
            foreach (int index in packageAsset.DependBundleIDs)
            {
                string dependBundleName = manifest.BundleList[index].BundleName;
                dependBundles.Add(dependBundleName);
            }
            dependBundles.Sort();
            return dependBundles;
        }

        /// <summary>
        /// 获取资源包依赖的资源包集合
        /// </summary>
        private List<string> GetBundleDependBundles(PackageManifest manifest, PackageBundle packageBundle)
        {
            List<string> dependBundles = new List<string>(packageBundle.DependBundleIDs.Length);
            foreach (int index in packageBundle.DependBundleIDs)
            {
                string dependBundleName = manifest.BundleList[index].BundleName;
                dependBundles.Add(dependBundleName);
            }
            dependBundles.Sort();
            return dependBundles;
        }

        /// <summary>
        /// 获取引用该资源包的资源包集合
        /// </summary>
        private List<string> GetBundleReferenceBundles(PackageManifest manifest, PackageBundle packageBundle)
        {
            List<string> referenceBundles = new List<string>(packageBundle.ReferenceBundleIDs.Count);
            foreach (int index in packageBundle.ReferenceBundleIDs)
            {
                string dependBundleName = manifest.BundleList[index].BundleName;
                referenceBundles.Add(dependBundleName);
            }
            referenceBundles.Sort();
            return referenceBundles;
        }

        /// <summary>
        /// 获取资源包内部所有资产
        /// </summary>
        private List<AssetInfo> GetBundleContents(BuildMapContext buildMapContext, string bundleName)
        {
            var bundleInfo = buildMapContext.GetBundleInfo(bundleName);
            List<AssetInfo> result = bundleInfo.GetBundleContents();
            result.Sort();
            return result;
        }

        private int GetMainAssetCount(PackageManifest manifest)
        {
            return manifest.AssetList.Count;
        }
        private int GetAllBundleCount(PackageManifest manifest)
        {
            return manifest.BundleList.Count;
        }
        private long GetAllBundleSize(PackageManifest manifest)
        {
            long fileBytes = 0;
            foreach (var packageBundle in manifest.BundleList)
            {
                fileBytes += packageBundle.FileSize;
            }
            return fileBytes;
        }
        private int GetEncryptedBundleCount(PackageManifest manifest)
        {
            int fileCount = 0;
            foreach (var packageBundle in manifest.BundleList)
            {
                if (packageBundle.Encrypted)
                    fileCount++;
            }
            return fileCount;
        }
        private long GetEncryptedBundleSize(PackageManifest manifest)
        {
            long fileBytes = 0;
            foreach (var packageBundle in manifest.BundleList)
            {
                if (packageBundle.Encrypted)
                    fileBytes += packageBundle.FileSize;
            }
            return fileBytes;
        }
    }
}