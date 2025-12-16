using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;

namespace YooAsset.Editor
{
    /// <summary>
    /// 真正的“待打包单元”
    /// <para>在 TaskGetBuildMap 步骤 10 由 BuildMapContext.PackAsset 构建并维护：一个 BundleName → N 个 BuildAssetInfo。</para>
    /// <para>额外能力：</para>
    /// <para>1. CreatePipelineBuild() 生成 Unity 的 AssetBundleBuild（assetNames = 本包显式打入的资产路径）。</para>
    /// <para>2. 记录打包后尺寸、哈希、CRC、加密结果等构建产物信息。</para>
    /// <para>作用：把“资产节点”按最终 BundleName 聚拢，作为 BuildPipeline/SBP 的输入，以及供 TaskCreateManifest/TaskCreateReport 使用。</para>
    /// </summary>
    public class BuildBundleInfo
    {
        #region 补丁文件的关键信息
        /// <summary>
        /// Unity引擎生成的哈希值（构建内容的哈希值）
        /// <para>来源：Unity 构建产物的内容哈希：</para>
        /// <para>• BuiltinBuildPipeline：TaskUpdateBundleInfo_BBP.GetUnityHash 通过 UnityManifest.GetAssetBundleHash(...)</para>
        /// <para>• ScriptableBuildPipeline：TaskUpdateBundleInfo_SBP.GetUnityHash 通过 buildResult.Results.BundleInfos[bundleName].Hash</para>
        /// <para>含义：Unity 对 bundle 内容（含依赖集变化）计算出的 Content Hash；依赖改变也会变。</para>
        /// <para>用途：仅记录在构建阶段便于追踪和对齐 Unity 构建结果。</para>
        /// <para>是否入清单：否（CreatePackageBundle 未写入）。</para>
        /// </summary>
        public string PackageUnityHash { set; get; }

        /// <summary>
        /// Unity引擎生成的CRC
        /// <para>来源：Unity 对构建出的 AssetBundle 计算的 CRC：</para>
        /// <para>• BuiltinBuildPipeline：BuildPipeline.GetCRCForAssetBundle(filePath, out crc)</para>
        /// <para>• SBP：buildResult.Results.BundleInfos[bundleName].Crc</para>
        /// <para>含义：Unity 官方校验码，用于 AssetBundle 加载校验。</para>
        /// <para>用途（运行时）：写入清单为 PackageBundle.UnityCRC，被文件系统或解密加载使用，例如 DefaultCacheFileSystem.LoadEncryptedAssetBundle* 传入 FileLoadCRC = bundle.UnityCRC。</para>
        /// <para>是否入清单：是（CreatePackageBundle 赋给 UnityCRC）。</para>
        /// </summary>
        public uint PackageUnityCRC { set; get; }

        /// <summary>
        /// 文件哈希值
        /// <para>来源：对“最终用于发布或复制的文件”（可能已加密）的字节做 MD5：</para>
        /// <para>• 计算对象：PackageSourceFilePath（若加密则为加密后文件）。</para>
        /// <para>• 代码：HashUtility.FileMD5(filePath)。</para>
        /// <para>含义：补丁侧的“最终文件内容哈希”，与 Unity 内容哈希不同，且受加密影响。</para>
        /// <para>用途：</para>
        /// <para>• 生成发布文件名：TaskUpdateBundleInfo 第 4 步，ManifestTools.GetRemoteBundleFileName(..., fileHash) → PackageDestFilePath。</para>
        /// <para>• 检测哈希冲突：TaskCreateManifest.CheckBundleHashConflict（加密或原生文件二进制相同会冲突）。</para>
        /// <para>• 作为清单字段：PackageBundle.FileHash（常用于跨版本是否复用同一物理文件的判断和对比）。</para>
        /// <para>是否入清单：是（CreatePackageBundle 赋给 FileHash）。</para>
        /// </summary>
        public string PackageFileHash { set; get; }

        /// <summary>
        /// 文件CRC
        /// <para>来源：对“最终用于发布或复制的文件”（可能已加密）的字节做 CRC32：</para>
        /// <para>• 代码：HashUtility.FileCRC32Value(filePath)。</para>
        /// <para>含义：补丁文件完整性校验码，和 PackageFileHash 一致基于最终字节。</para>
        /// <para>用途：下载和缓存校验（运行时 Downloader 或 FileSystem 校验完整性），便于快速校验与差错定位。</para>
        /// <para>是否入清单：是（CreatePackageBundle 赋给 FileCRC）。</para>
        /// </summary>
        public uint PackageFileCRC { set; get; }

        /// <summary>
        /// 文件大小
        /// <para>来源：最终文件大小（字节）。</para>
        /// <para>• 代码：FileUtility.GetFileSize(filePath)。</para>
        /// <para>含义：用于进度统计、配额估算、快速校验。</para>
        /// <para>用途：下载进度和校验（与 CRC 搭配），同时写入清单。</para>
        /// <para>是否入清单：是（CreatePackageBundle 赋给 FileSize）。</para>
        /// </summary>
        public long PackageFileSize { set; get; }

        /// <summary>
        /// 构建输出的文件路径
        /// <para>含义：构建管线输出目录下，Unity 打出的原始 AssetBundle 文件路径。</para>
        /// <para>填充：TaskUpdateBundleInfo 第 2 步设为 pipelineOutputDirectory/bundleName。</para>
        /// <para>用途：用于计算 PackageUnityCRC（Builtin 流程）。</para>
        /// </summary>
        public string BuildOutputFilePath { set; get; }

        /// <summary>
        /// 补丁包的源文件路径
        /// <para>含义：“用于生成清单和拷贝到补丁目录的源文件”。</para>
        /// <para>规则：若启用加密则取 EncryptedFilePath，否则取 BuildOutputFilePath。</para>
        /// <para>用途：作为 PackageFileHash、PackageFileCRC 和 PackageFileSize 的计算对象，以及 TaskCreatePackage_BBP 的拷贝来源。</para>
        /// </summary>
        public string PackageSourceFilePath { set; get; }

        /// <summary>
        /// 补丁包的目标文件路径
        /// <para>含义：最终发布目录下的补丁文件路径。</para>
        /// <para>生成：TaskUpdateBundleInfo 第 4 步，使用 FileNameStyle + PackageFileHash + 扩展名决定。</para>
        /// <para>用途：TaskCreatePackage_BBP 将 PackageSourceFilePath 拷贝到该处；后续清单和首包拷贝均以清单里的 FileName 为准（与此一致）。</para>
        /// </summary>
        public string PackageDestFilePath { set; get; }

        /// <summary>
        /// 加密生成文件的路径
        /// <para>注意：如果未加密该路径为空。</para>
        /// <para>含义：若启用加密，生成的“加密后文件”的路径；未加密则为空。</para>
        /// <para>影响：若 Encrypted 为 true，则 PackageSourceFilePath = EncryptedFilePath。</para>
        /// </summary>
        public string EncryptedFilePath { set; get; }
        #endregion

        /// <summary>
        /// 资源节点集合
        /// <para>键：AssetPath，值：资源节点 BuildAssetInfo。</para>
        /// <para>作用：用于去重与快速查找（O(1)），保证同一包内不会重复加入同一资源，仅在内部使用（private）。</para>
        /// </summary>
        private readonly Dictionary<string, BuildAssetInfo> _packAssetDic = new Dictionary<string, BuildAssetInfo>(100);

        /// <summary>
        /// 参与构建的资源列表
        /// <para>注意：不包含零依赖资源和冗余资源。</para>
        /// <para>作用：用于顺序遍历和稳定输出，保留插入顺序，便于生成可预期和稳定的构建输入与清单。</para>
        /// </summary>
        public readonly List<BuildAssetInfo> AllPackAssets = new List<BuildAssetInfo>(100);

        /// <summary>
        /// 资源包名称
        /// <para>含义：当前 BuildBundleInfo 对应的逻辑 Bundle 名称，用于构建和清单标识。</para>
        /// </summary>
        public string BundleName { private set; get; }

        /// <summary>
        /// 加密文件标记
        /// <para>含义：标记该 bundle 是否被加密。</para>
        /// <para>用途：决定 PackageSourceFilePath 取值，并写入清单 PackageBundle.Encrypted；运行时文件系统据此选择解密加载流程（例如 DefaultCacheFileSystem.LoadEncryptedAssetBundle*）。</para>
        /// </summary>
        public bool Encrypted { set; get; }


        public BuildBundleInfo(string bundleName)
        {
            BundleName = bundleName;
        }

        /// <summary>
        /// 添加一个打包资源
        /// <para>功能：将一个 BuildAssetInfo 加入当前 bundle 的打包列表，并在内部字典中建立索引。</para>
        /// <para>约束：如果同一资源路径已存在于当前 bundle，会抛出异常以防止重复打包。</para>
        /// </summary>
        public void PackAsset(BuildAssetInfo buildAsset)
        {
            string assetPath = buildAsset.AssetInfo.AssetPath;
            if (_packAssetDic.ContainsKey(assetPath))
                throw new System.Exception($"Should never get here ! Asset is existed : {assetPath}");

            _packAssetDic.Add(assetPath, buildAsset);
            AllPackAssets.Add(buildAsset);
        }

        /// <summary>
        /// 是否包含指定资源
        /// <para>功能：根据资源路径判断该资源是否已经被加入到当前 bundle 的打包集合中。</para>
        /// <para>返回：true 表示已包含，false 表示未包含。</para>
        /// </summary>
        public bool IsContainsPackAsset(string assetPath)
        {
            return _packAssetDic.ContainsKey(assetPath);
        }

        /// <summary>
        /// 获取构建的资源路径列表
        /// <para>功能：返回当前 bundle 中所有参与打包资源的 AssetPath 集合（仅主资源，不含冗余和零依赖资源）。</para>
        /// </summary>
        public string[] GetAllPackAssetPaths()
        {
            return AllPackAssets.Select(t => t.AssetInfo.AssetPath).ToArray();
        }

        /// <summary>
        /// 获取构建的主资源信息
        /// <para>功能：根据资源路径从当前 bundle 中查找对应的 BuildAssetInfo。</para>
        /// <para>异常：如果未找到对应资源，将抛出异常提示当前 bundle 中不存在该资源。</para>
        /// </summary>
        public BuildAssetInfo GetPackAssetInfo(string assetPath)
        {
            if (_packAssetDic.TryGetValue(assetPath, out BuildAssetInfo value))
            {
                return value;
            }
            else
            {
                throw new Exception($"Can not found pack asset info {assetPath} in bundle : {BundleName}");
            }
        }

        /// <summary>
        /// 获取资源包内部所有资产
        /// <para>功能：返回当前 bundle 实际包含的所有 AssetInfo 列表。</para>
        /// <para>组成：包括所有打包主资源以及其依赖中“未被单独打包”的零依赖和冗余资源（HasBundleName == false）。</para>
        /// <para>用途：用于分析 bundle 内容、制作报告或调试依赖关系。</para>
        /// </summary>
        public List<AssetInfo> GetBundleContents()
        {
            Dictionary<string, AssetInfo> result = new Dictionary<string, AssetInfo>(AllPackAssets.Count);
            foreach (var packAsset in AllPackAssets)
            {
                result.Add(packAsset.AssetInfo.AssetPath, packAsset.AssetInfo);
                if (packAsset.AllDependAssetInfos != null)
                {
                    foreach (var dependAssetInfo in packAsset.AllDependAssetInfos)
                    {
                        // 注意：依赖资源里只添加零依赖资源和冗余资源
                        if (dependAssetInfo.HasBundleName() == false)
                        {
                            string dependAssetPath = dependAssetInfo.AssetInfo.AssetPath;
                            if (result.ContainsKey(dependAssetPath) == false)
                                result.Add(dependAssetPath, dependAssetInfo.AssetInfo);
                        }
                    }
                }
            }
            return result.Values.ToList();
        }

        /// <summary>
        /// 创建 AssetBundleBuild 描述
        /// <para>功能：将当前 BuildBundleInfo 转换为 UnityEditor.AssetBundleBuild 结构，作为打包管线的输入。</para>
        /// <para>AssetBundleBuild.assetBundleName：使用当前 BundleName 作为 AB 包名。</para>
        /// <para>AssetBundleBuild.assetBundleVariant：统一置为空字符串，不再支持 Unity 的变种机制。</para>
        /// <para>AssetBundleBuild.assetNames：使用 GetAllPackAssetPaths() 作为待打包资源路径列表。</para>
        /// </summary>
        public UnityEditor.AssetBundleBuild CreatePipelineBuild()
        {
            // 注意：我们不再支持AssetBundle的变种机制
            AssetBundleBuild build = new AssetBundleBuild();
            build.assetBundleName = BundleName;
            build.assetBundleVariant = string.Empty;
            build.assetNames = GetAllPackAssetPaths();
            return build;
        }

        /// <summary>
        /// 获取所有写入补丁清单的资源
        /// <para>筛选条件：ECollectorType.MainAssetCollector。</para>
        /// <para>功能：返回当前 bundle 中需要写入补丁清单的主资源 BuildAssetInfo 数组。</para>
        /// </summary>
        public BuildAssetInfo[] GetAllManifestAssetInfos()
        {
            return AllPackAssets.Where(t => t.CollectorType == ECollectorType.MainAssetCollector).ToArray();
        }

        /// <summary>
        /// 创建 PackageBundle 描述
        /// <para>功能：根据当前 BuildBundleInfo 构造用于写入补丁清单的 PackageBundle 数据结构。</para>
        /// <para>写入字段：BundleName、UnityCRC、FileHash、FileCRC、FileSize、Encrypted。</para>
        /// <para>注意：PackageUnityHash 不写入清单，仅在构建阶段用于对齐 Unity 构建结果。</para>
        /// </summary>
        internal PackageBundle CreatePackageBundle()
        {
            PackageBundle packageBundle = new PackageBundle();
            packageBundle.BundleName = BundleName;
            packageBundle.UnityCRC = PackageUnityCRC;
            packageBundle.FileHash = PackageFileHash;
            packageBundle.FileCRC = PackageFileCRC;
            packageBundle.FileSize = PackageFileSize;
            packageBundle.Encrypted = Encrypted;
            return packageBundle;
        }
    }
}