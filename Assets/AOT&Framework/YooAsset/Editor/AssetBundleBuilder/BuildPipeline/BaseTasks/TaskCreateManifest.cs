using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;

namespace YooAsset.Editor
{
    public class ManifestContext : IContextObject
    {
        internal PackageManifest Manifest;
    }

    public abstract class TaskCreateManifest
    {
        // 资源包索引缓存，键：资源包名称，值：PackageManifest.BuildleList资源包索引ID
        private readonly Dictionary<string, int> _cachedBundleIndexIDs = new Dictionary<string, int>(10000);
        // 资源包标签缓存，键：资源包索引ID，值：标签集合
        private readonly Dictionary<int, HashSet<string>> _cacheBundleTags = new Dictionary<int, HashSet<string>>(10000);

        /// <summary>
        /// 创建补丁清单文件到输出目录
        /// </summary>
        protected void CreateManifestFile(bool processBundleDepends, bool processBundleTags, BuildContext context)
        {
            var buildMapContext = context.GetContextObject<BuildMapContext>();
            var buildParametersContext = context.GetContextObject<BuildParametersContext>();
            var buildParameters = buildParametersContext.Parameters;
            string packageOutputDirectory = buildParametersContext.GetPackageOutputDirectory();

            // 检测资源包哈希冲突
            CheckBundleHashConflict(buildMapContext);

            // 创建新补丁清单
            PackageManifest manifest = new PackageManifest();
            manifest.FileVersion = ManifestDefine.FileVersion;
            manifest.EnableAddressable = buildMapContext.Command.EnableAddressable;
            manifest.SupportExtensionless = buildMapContext.Command.SupportExtensionless;
            manifest.LocationToLower = buildMapContext.Command.LocationToLower;
            manifest.IncludeAssetGUID = buildMapContext.Command.IncludeAssetGUID;
            manifest.OutputNameStyle = (int)buildParameters.FileNameStyle;
            manifest.BuildBundleType = buildParameters.BuildBundleType;
            manifest.BuildPipeline = buildParameters.BuildPipeline;
            manifest.PackageName = buildParameters.PackageName;
            manifest.PackageVersion = buildParameters.PackageVersion;
            manifest.PackageNote = buildParameters.PackageNote;
            manifest.AssetList = CreatePackageAssetList(buildMapContext);       //! 创建资源对象PackageAsset列表，获取资源包里的主资源对象
            manifest.BundleList = CreatePackageBundleList(buildMapContext);     //! 创建资源包列表PackageBundle列表

            // 1. 处理资源清单的资源对象：根据[资源包名]缓存[资源包]索引、记录[资源对象]所属的[资源包]索引PackageAsset.BundleID、记录[资源对象]依赖的[资源包]ID索引集合PackageAsset.DependBundleIDs
            ProcessPacakgeAsset(manifest);

            // 2. 处理资源包的依赖列表：填充[资源包]的依赖集合PackageBundle.DependBundleIDs
            if (processBundleDepends)
                ProcessBundleDepends(context, manifest);

            // 3. 处理资源包的标签集合：缓存[资源包]标签、记录[资源包]标签集合
            if (processBundleTags)
                ProcessBundleTags(manifest);

            // 4. 处理内置资源包：如果是可编程构建管线，需要补充内置资源包
            if (processBundleDepends)
                ProcessBuiltinBundleDependency(context, manifest);

            // 创建资源清单文本文件.json
            {
                string fileName = YooAssetSettingsData.GetManifestJsonFileName(buildParameters.PackageName, buildParameters.PackageVersion);
                string filePath = $"{packageOutputDirectory}/{fileName}";
                ManifestTools.SerializeToJson(filePath, manifest);
                BuildLogger.Log($"Create package manifest file: {filePath}");
            }

            // 创建资源清单二进制文件.bytes
            string packageHash;
            string packagePath;
            {
                string fileName = YooAssetSettingsData.GetManifestBinaryFileName(buildParameters.PackageName, buildParameters.PackageVersion);
                packagePath = $"{packageOutputDirectory}/{fileName}";
                ManifestTools.SerializeToBinary(packagePath, manifest, buildParameters.ManifestProcessServices);
                packageHash = HashUtility.FileCRC32(packagePath);
                BuildLogger.Log($"Create package manifest file: {packagePath}");
            }

            // 创建资源清单哈希文件.hash
            {
                string fileName = YooAssetSettingsData.GetPackageHashFileName(buildParameters.PackageName, buildParameters.PackageVersion);
                string filePath = $"{packageOutputDirectory}/{fileName}";
                FileUtility.WriteAllText(filePath, packageHash);
                BuildLogger.Log($"Create package manifest hash file: {filePath}");
            }

            // 创建资源清单版本文件.version
            {
                string fileName = YooAssetSettingsData.GetPackageVersionFileName(buildParameters.PackageName);
                string filePath = $"{packageOutputDirectory}/{fileName}";
                FileUtility.WriteAllText(filePath, buildParameters.PackageVersion);
                BuildLogger.Log($"Create package manifest version file: {filePath}");
            }

            //? 不同格式的清单文件有什么不同？
            //!? .byte（二进制清单）
            //!? 内容：PackageManifest 完整数据（二进制）。
            //!? 写入时可经过 IManifestProcessServices 处理（压缩/加密）。
            //!? 运行时真正加载的是它（DeserializeFromBinary(byte[])）。
            //!? .json（可读清单）
            //!? 内容与 .bytes 一致，但不做处理（明文 JSON）。
            //!? 只用于调试、分析，不是运行时的主数据源。
            //!? .hash
            //!? 内容：对 .bytes 做 CRC32 的字符串。
            //!? 用途：记录当前使用的版本号，配合首包 / 远程版本查询流程。
            //!? .version
            //!? 内容：当前构建的 PackageVersion。
            //!? 用途：记录当前使用的版本号，配合首包 / 远程版本查询流程。
            //!? .report（在 TaskCreateReport_SBP 生成）
            //!? 内容：构建统计/明细，主要给人看（编辑器分析）。
            //!? 

            // 填充上下文
            {
                ManifestContext manifestContext = new ManifestContext();
                byte[] bytesData = FileUtility.ReadAllBytes(packagePath);
                manifestContext.Manifest = ManifestTools.DeserializeFromBinary(bytesData, buildParameters.ManifestRestoreServices);
                context.SetContextObject(manifestContext);
            }
        }

        /// <summary>
        /// 检测资源包哈希冲突，是否存在相同PackageFileHash的BuildBundleInfo
        /// </summary>
        private void CheckBundleHashConflict(BuildMapContext buildMapContext)
        {
            // 说明：在特殊情况下，例如某些文件加密算法会导致加密后的文件哈希值冲突！
            // 说明：二进制完全相同的原生文件也会冲突！
            HashSet<string> guids = new HashSet<string>();
            foreach (var bundleInfo in buildMapContext.Collection)
            {
                if (guids.Contains(bundleInfo.PackageFileHash))
                {
                    string message = BuildLogger.GetErrorMessage(ErrorCode.BundleHashConflict, $"Bundle hash conflict : {bundleInfo.BundleName}");
                    throw new Exception(message);
                }
                else
                {
                    guids.Add(bundleInfo.PackageFileHash);
                }
            }
        }

        /// <summary>
        /// 获取资源包的依赖集合
        /// </summary>
        protected abstract string[] GetBundleDepends(BuildContext context, string bundleName);

        /// <summary>
        /// 创建资源对象PackageAsset列表，获取资源包里的主资源对象（包含Address、AssetPath、AssetGUID、AssetTags、BuildAssetInfo）
        /// </summary>
        private List<PackageAsset> CreatePackageAssetList(BuildMapContext buildMapContext)
        {
            List<PackageAsset> result = new List<PackageAsset>(1000);
            foreach (var bundleInfo in buildMapContext.Collection)
            {
                //! 获取资源包里的主资源对象
                var assetInfos = bundleInfo.GetAllManifestAssetInfos();
                foreach (var assetInfo in assetInfos)
                {
                    PackageAsset packageAsset = new PackageAsset();
                    packageAsset.Address = buildMapContext.Command.EnableAddressable ? assetInfo.Address : string.Empty;
                    packageAsset.AssetPath = assetInfo.AssetInfo.AssetPath;
                    packageAsset.AssetGUID = buildMapContext.Command.IncludeAssetGUID ? assetInfo.AssetInfo.AssetGUID : string.Empty;
                    packageAsset.AssetTags = assetInfo.AssetTags.ToArray();
                    packageAsset.TempDataInEditor = assetInfo;
                    result.Add(packageAsset);
                }
            }

            // 按照AssetPath排序
            result.Sort((a, b) => a.AssetPath.CompareTo(b.AssetPath));
            return result;
        }

        /// <summary>
        /// 创建资源包列表
        /// </summary>
        private List<PackageBundle> CreatePackageBundleList(BuildMapContext buildMapContext)
        {
            List<PackageBundle> result = new List<PackageBundle>(1000);
            foreach (var bundleInfo in buildMapContext.Collection)
            {
                var packageBundle = bundleInfo.CreatePackageBundle();
                result.Add(packageBundle);
            }

            // 按照BundleName排序
            result.Sort((a, b) => a.BundleName.CompareTo(b.BundleName));
            return result;
        }

        /// <summary>
        /// 处理资源清单的[资源对象]列表，根据[资源包名]缓存[资源包]索引、记录[资源对象]所属的[资源包]索引、记录[资源对象]依赖的[资源包]索引集合
        /// </summary>
        private void ProcessPacakgeAsset(PackageManifest manifest)
        {
            // 注意：优先缓存资源包索引
            for (int index = 0; index < manifest.BundleList.Count; index++)
            {
                string bundleName = manifest.BundleList[index].BundleName;
                _cachedBundleIndexIDs.Add(bundleName, index);
            }

            // 记录资源对象所属的资源包ID
            foreach (var packageAsset in manifest.AssetList)
            {
                var assetInfo = packageAsset.TempDataInEditor as BuildAssetInfo;
                packageAsset.BundleID = GetCachedBundleIndexID(assetInfo.BundleName);
            }

            // 通过BuildAssetInfo.AllDependAssetInfos记录[资源对象]依赖的[资源包]ID集合
            // 注意：依赖关系非引擎构建结果里查询！
            foreach (var packageAsset in manifest.AssetList)
            {
                var mainAssetInfo = packageAsset.TempDataInEditor as BuildAssetInfo;
                packageAsset.DependBundleIDs = GetAssetDependBundleIDs(mainAssetInfo);
            }
        }

        /// <summary>
        /// 处理资源包的依赖集合
        /// </summary>
        private void ProcessBundleDepends(BuildContext context, PackageManifest manifest)
        {
            // 查询引擎生成的资源包依赖关系，然后记录到清单
            foreach (var packageBundle in manifest.BundleList)
            {
                // 获取当前[资源包]在缓存中的ID索引
                int mainBundleID = GetCachedBundleIndexID(packageBundle.BundleName);
                // 获取[资源包]的[依赖资源包]名称集合，SBP通过TaskBuildin_SBP的构建结果IBundleBuildResults的BundleInfos来获取
                string[] dependNames = GetBundleDepends(context, packageBundle.BundleName);
                List<int> dependIDs = new List<int>(dependNames.Length);
                // 通过[资源包名]和缓存查询依赖[资源包]的索引ID
                foreach (var dependName in dependNames)
                {
                    int dependBundleID = GetCachedBundleIndexID(dependName);
                    if (dependBundleID != mainBundleID)
                        dependIDs.Add(dependBundleID);
                }

                // 排序并填充数据
                dependIDs.Sort();
                packageBundle.DependBundleIDs = dependIDs.ToArray();
            }
        }

        /// <summary>
        /// 处理资源包的标签集合
        /// </summary>
        private void ProcessBundleTags(PackageManifest manifest)
        {
            // 清空所有[资源包]的标签信息
            foreach (var packageBundle in manifest.BundleList)
            {
                packageBundle.Tags = Array.Empty<string>();
            }

            // 缓存[资源包]的标签信息
            foreach (var packageAsset in manifest.AssetList)
            {
                var assetTags = packageAsset.AssetTags;
                int bundleID = packageAsset.BundleID;
                // 缓存主资源包的标签
                CacheBundleTags(bundleID, assetTags);
                if (packageAsset.DependBundleIDs != null)
                {
                    // 缓存依赖资源包的标签
                    foreach (var dependBundleID in packageAsset.DependBundleIDs)
                    {
                        CacheBundleTags(dependBundleID, assetTags);
                    }
                }
            }

            // 将缓存的资源标签赋值给[资源包]
            for (int index = 0; index < manifest.BundleList.Count; index++)
            {
                var packageBundle = manifest.BundleList[index];
                if (_cacheBundleTags.TryGetValue(index, out var value))
                {
                    packageBundle.Tags = value.ToArray();
                }
                else
                {
                    // 注意：SBP构建管线会自动剔除一些冗余资源的引用关系，导致游离资源包没有被任何主资源包引用。
                    string warning = BuildLogger.GetErrorMessage(ErrorCode.FoundStrayBundle, $"Found stray bundle ! Bundle ID : {index} Bundle name : {packageBundle.BundleName}");
                    BuildLogger.Warning(warning);
                }
            }
        }

        /// <summary>
        /// 缓存资源包的标签集合
        /// </summary>
        /// <param name="bundleID"></param>
        /// <param name="assetTags"></param>
        private void CacheBundleTags(int bundleID, string[] assetTags)
        {
            if (_cacheBundleTags.ContainsKey(bundleID) == false)
                _cacheBundleTags.Add(bundleID, new HashSet<string>());

            foreach (var assetTag in assetTags)
            {
                if (_cacheBundleTags[bundleID].Contains(assetTag) == false)
                    _cacheBundleTags[bundleID].Add(assetTag);
            }
        }

        /// <summary>
        /// 获取缓存的资源包的索引ID
        /// </summary>
        private int GetCachedBundleIndexID(string bundleName)
        {
            if (_cachedBundleIndexIDs.TryGetValue(bundleName, out int value) == false)
            {
                throw new Exception($"Should never get here ! Not found bundle index ID : {bundleName}");
            }
            return value;
        }

        /// <summary>
        /// 是否包含该资源包的索引ID
        /// </summary>
        private bool ContainsCachedBundleIndexID(string bundleName)
        {
            return _cachedBundleIndexIDs.ContainsKey(bundleName);
        }

        #region YOOASSET_LEGACY_DEPENDENCY
        private void ProcessBuiltinBundleDependency(BuildContext context, PackageManifest manifest)
        {
            // 注意：初始化资源清单建立引用关系
            ManifestTools.InitManifest(manifest);

            // 注意：如果是可编程构建管线，需要补充内置资源包
            // 注意：该步骤依赖前面的操作！
            var buildResultContext = context.TryGetContextObject<TaskBuilding_SBP.BuildResultContext>();

            if (buildResultContext != null)
            {
                ProcessBuiltinBundleReference(manifest, buildResultContext.BuiltinShadersBundleName);
                ProcessBuiltinBundleReference(manifest, buildResultContext.MonoScriptsBundleName);

                var buildParametersContext = context.TryGetContextObject<BuildParametersContext>();
                var buildParameters = buildParametersContext.Parameters;
                if (buildParameters is ScriptableBuildParameters scriptableBuildParameters)
                {
                    if (scriptableBuildParameters.TrackSpriteAtlasDependencies)
                    {
                        // 注意：检测是否开启图集模式
                        // 说明：需要记录主资源对象对图集的依赖关系！
                        if (EditorSettings.spritePackerMode != SpritePackerMode.Disabled)
                        {
                            var buildMapContext = context.GetContextObject<BuildMapContext>();
                            foreach (var spriteAtlasAsset in buildMapContext.SpriteAtlasAssetList)
                            {
                                string spriteAtlasBundleName = spriteAtlasAsset.BundleName;
                                ProcessBuiltinBundleReference(manifest, spriteAtlasBundleName);
                            }
                        }
                    }
                }
            }
        }
        private void ProcessBuiltinBundleReference(PackageManifest manifest, string builtinBundleName)
        {
            if (string.IsNullOrEmpty(builtinBundleName))
                return;

            // 查询内置资源包是否存在
            if (ContainsCachedBundleIndexID(builtinBundleName) == false)
                return;

            // 获取内置资源包
            int builtinBundleID = GetCachedBundleIndexID(builtinBundleName);
            var builtinPackageBundle = manifest.BundleList[builtinBundleID];

            // 更新依赖资源包ID集合
            HashSet<int> cacheBundleIDs = new HashSet<int>(builtinPackageBundle.ReferenceBundleIDs);
            HashSet<string> tempTags = new HashSet<string>();
            foreach (var packageAsset in manifest.AssetList)
            {
                if (cacheBundleIDs.Contains(packageAsset.BundleID))
                {
                    if (packageAsset.DependBundleIDs.Contains(builtinBundleID) == false)
                    {
                        var tempBundleIDs = new List<int>(packageAsset.DependBundleIDs);
                        tempBundleIDs.Add(builtinBundleID);
                        packageAsset.DependBundleIDs = tempBundleIDs.ToArray();
                    }

                    foreach (var tag in packageAsset.AssetTags)
                    {
                        if (tempTags.Contains(tag) == false)
                            tempTags.Add(tag);
                    }
                }
            }

            // 更新内置资源包的标签集合
            foreach (var tag in builtinPackageBundle.Tags)
            {
                if (tempTags.Contains(tag) == false)
                    tempTags.Add(tag);
            }
            builtinPackageBundle.Tags = tempTags.ToArray();
        }

        /// <summary>
        /// 通过BuildAssetInfo的AllDependAssetInfos获取[资源对象]依赖的[资源包]ID数组集合
        /// </summary>
        /// <param name="mainAssetInfo"></param>
        /// <returns></returns>
        private int[] GetAssetDependBundleIDs(BuildAssetInfo mainAssetInfo)
        {
            HashSet<int> result = new HashSet<int>();
            // 获取当前资源所在资源包的ID
            int mainBundleID = GetCachedBundleIndexID(mainAssetInfo.BundleName);
            foreach (var dependAssetInfo in mainAssetInfo.AllDependAssetInfos)
            {
                if (dependAssetInfo.HasBundleName())
                {
                    // 获取依赖资源的所在资源包的ID索引
                    int bundleID = GetCachedBundleIndexID(dependAssetInfo.BundleName);
                    if (mainBundleID != bundleID)
                    {
                        if (result.Contains(bundleID) == false)
                            result.Add(bundleID);
                    }
                }
            }

            // 排序并返回数据
            List<int> listResult = new List<int>(result);
            listResult.Sort();
            return listResult.ToArray();
        }
        #endregion
    }
}
