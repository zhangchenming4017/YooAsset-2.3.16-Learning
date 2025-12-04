using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;

namespace YooAsset.Editor
{
    /// <summary>
    /// 真正的“待打包单元”
    /// 在TaskGetBuildMap步骤10由 BuildMapContext.PackAsset 构建并维护：一个 BundleName → N 个 BuildAssetInfo。
    /// 额外能力：
    /// 1.CreatePipelineBuild() 生成 Unity 的 AssetBundleBuild（assetNames = 本包显式打入的资产路径）。
    /// 2.记录打包后尺寸、哈希、CRC、加密结果等构建产物信息。
    /// 作用：把“资产节点”按最终 BundleName 聚拢，作为 BuildPipeline/SBP 的输入，以及供 TaskCreateManifest/TaskCreateReport 使用。
    /// </summary>
    public class BuildBundleInfo
    {
        #region 补丁文件的关键信息
        /// <summary>
        /// Unity引擎生成的哈希值（构建内容的哈希值）
        /// 来源：Unity 构建产物的内容哈希：
        /// • BuiltinBuildPipeline：TaskUpdateBundleInfo_BBP.GetUnityHash 通过 UnityManifest.GetAssetBundleHash(...)
        /// • ScriptableBuildPipeline：TaskUpdateBundleInfo_SBP.GetUnityHash 通过 buildResult.Results.BundleInfos[bundleName].Hash
        /// 含义：Unity 对 bundle 内容（含依赖集变化）计算出的 Content Hash；依赖改变也会变
        /// 用途：仅记录在构建阶段便于追踪/对齐 Unity 构建结果
        /// 是否入清单：否（CreatePackageBundle 未写入）
        /// </summary>
        public string PackageUnityHash { set; get; }

        /// <summary>
        /// Unity引擎生成的CRC
        /// 来源：Unity 对构建出的 AssetBundle 计算的 CRC：
        /// • BuiltinBuildPipeline：BuildPipeline.GetCRCForAssetBundle(filePath, out crc)
        /// • SBP：buildResult.Results.BundleInfos[bundleName].Crc
        /// 含义：Unity 官方校验码，用于 AssetBundle 加载校验
        /// 用途（运行时）：写入清单为 PackageBundle.UnityCRC，被文件系统/解密加载使用
        /// • 如 DefaultCacheFileSystem.LoadEncryptedAssetBundle* 传入 FileLoadCRC = bundle.UnityCRC
        /// 是否入清单：是（CreatePackageBundle 赋给 UnityCRC）
        /// </summary>
        public uint PackageUnityCRC { set; get; }

        /// <summary>
        /// 文件哈希值
        /// 来源：对“最终用于发布/复制的文件”（可能已加密）的字节做 MD5
        /// • 计算对象：PackageSourceFilePath（若加密则为加密后文件）
        /// • 代码：HashUtility.FileMD5(filePath)
        /// 含义：补丁侧的“最终文件内容哈希”，与 Unity 内容哈希不同，且受加密影响
        /// 用途：
        /// • 生成发布文件名：TaskUpdateBundleInfo 第4步
        /// • • ManifestTools.GetRemoteBundleFileName(..., fileHash) → PackageDestFilePath
        /// • 检测哈希冲突：TaskCreateManifest.CheckBundleHashConflict（加密或原生文件二进制相同会冲突）
        /// • 作为清单字段：PackageBundle.FileHash（常用于跨版本是否复用同一物理文件的判断/对比）
        /// 是否入清单：是（CreatePackageBundle 赋给 FileHash）
        /// </summary>
        public string PackageFileHash { set; get; }

        /// <summary>
        /// 文件CRC
        /// 来源：对“最终用于发布/复制的文件”（可能已加密）的字节做 CRC32
        /// • 代码：HashUtility.FileCRC32Value(filePath)
        /// 含义：补丁文件完整性校验码，和 PackageFileHash 一致基于最终字节
        /// 用途：下载/缓存校验（运行时 Downloader 或 FileSystem 校验完整性）；便于快速校验与差错定位
        /// 是否入清单：是（CreatePackageBundle 赋给 FileCRC）
        /// </summary>
        public uint PackageFileCRC { set; get; }

        /// <summary>
        /// 文件大小
        /// 来源：最终文件大小（字节）
        /// • 代码：FileUtility.GetFileSize(filePath)
        /// 含义：用于进度统计、配额估算、快速校验
        /// 用途：下载进度/校验（与 CRC 搭配）；也会写入清单
        /// 是否入清单：是（CreatePackageBundle 赋给 FileSize）
        /// </summary>
        public long PackageFileSize { set; get; }

        /// <summary>
        /// 构建输出的文件路径
        /// 含义：构建管线输出目录下，Unity 打出的原始 AB 文件路径
        /// 填充：TaskUpdateBundleInfo 第2步设为 pipelineOutputDirectory/bundleName
        /// 用途：计算 PackageUnityCRC（Builtin 流程）
        /// </summary>
        public string BuildOutputFilePath { set; get; }

        /// <summary>
        /// 补丁包的源文件路径
        /// 含义：“用于生成清单和拷贝到补丁目录的源文件”
        /// 规则：加密则取 EncryptedFilePath，否则取 BuildOutputFilePath
        /// 用途：作为 PackageFileHash/CRC/Size 的计算对象；TaskCreatePackage_BBP 拷贝到目标
        /// </summary>
        public string PackageSourceFilePath { set; get; }

        /// <summary>
        /// 补丁包的目标文件路径
        /// 含义：最终发布目录下的补丁文件路径
        /// 生成：TaskUpdateBundleInfo 第4步，使用 FileNameStyle + PackageFileHash + 扩展名决定
        /// 用途：TaskCreatePackage_BBP 将 PackageSourceFilePath 拷贝到该处；后续清单/首包拷贝均以清单里的 FileName 为准（与此一致）
        /// </summary>
        public string PackageDestFilePath { set; get; }

        /// <summary>
        /// 加密生成文件的路径
        /// 注意：如果未加密该路径为空
        /// 含义：若启用加密，生成的“加密后文件”的路径；未加密则为空
        /// 影响：若 Encrypted == true，PackageSourceFilePath = EncryptedFilePath
        /// </summary>
        public string EncryptedFilePath { set; get; }
        #endregion

        /// <summary>
        /// 资源节点集合，键：AssetPath，值：资源节点
        /// <para>作用：去重与快速查找 O(1)，内部使用（private），保证同一包内不会重复加入同一资源</para>
        /// </summary>
        private readonly Dictionary<string, BuildAssetInfo> _packAssetDic = new Dictionary<string, BuildAssetInfo>(100);

        /// <summary>
        /// 参与构建的资源列表
        /// <para>注意：不包含零依赖资源和冗余资源</para>
        /// <para>作用：顺序遍历、稳定输出，保留插入顺序，便于生成可预期/稳定的构建输入与清单</para>
        /// </summary>
        public readonly List<BuildAssetInfo> AllPackAssets = new List<BuildAssetInfo>(100);

        /// <summary>
        /// 资源包名称
        /// </summary>
        public string BundleName { private set; get; }

        /// <summary>
        /// 加密文件
        /// 含义：该 bundle 是否被加密
        /// 用途：决定 PackageSourceFilePath，并写入清单 PackageBundle.Encrypted；运行时文件系统据此选择解密加载流程（见 DefaultCacheFileSystem.LoadEncryptedAssetBundle*）
        /// </summary>
        public bool Encrypted { set; get; }


        public BuildBundleInfo(string bundleName)
        {
            BundleName = bundleName;
        }

        /// <summary>
        /// 添加一个打包资源
        /// </summary>
        public void PackAsset(BuildAssetInfo buildAsset)
        {
            string assetPath = buildAsset.AssetInfo.AssetPath;
            if (_packAssetDic.ContainsKey(assetPath))
                throw new System.Exception($"Should never get here ! Asset is existed : {assetPath}");

            //? 这两个有什么区别？为什么需要两个？
            _packAssetDic.Add(assetPath, buildAsset);
            AllPackAssets.Add(buildAsset);
        }

        /// <summary>
        /// 是否包含指定资源
        /// </summary>
        public bool IsContainsPackAsset(string assetPath)
        {
            return _packAssetDic.ContainsKey(assetPath);
        }

        /// <summary>
        /// 获取构建的资源路径列表
        /// </summary>
        public string[] GetAllPackAssetPaths()
        {
            return AllPackAssets.Select(t => t.AssetInfo.AssetPath).ToArray();
        }

        /// <summary>
        /// 获取构建的主资源信息
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
        /// 创建AssetBundleBuild类：把一个逻辑包BuildBundleInfo转成AssetBundleBuild
        /// <para>它是 Unity 编辑器侧的输入描述类型，用来告诉打包管线“这个包叫什么、要把哪些资源打进这个包、是否有变种名”。
        /// </para>
        /// <para>AssetBundleBuild.assetBundleName：AB包名</para>
        /// <para>AssetBundleBuild.assetBundleVariant：变种默认为空</para>
        /// <para>AssetBundleBuild.assetNames：待打包资源路径列表</para>
        /// </summary>
        public UnityEditor.AssetBundleBuild CreatePipelineBuild()
        {
            // 注意：我们不再支持AssetBundle的变种机制
            AssetBundleBuild build = new AssetBundleBuild();
            build.assetBundleName = BundleName;
            build.assetBundleVariant = string.Empty;
            build.assetNames = GetAllPackAssetPaths();
            return build;

            //? 什么是Unity的变种机制
            //!? 核心是同一个逻辑包名配多个“变种名”（如 characters.hd / characters.sd），构建时会产出多个变种包，运行时按“当前激活的变种列表”选择加载哪个变种。
            //!? 变种由 AssetBundleBuild.assetBundleVariant 指定，最终文件名会是 bundleName.variant 的形式。
            //? YooAsset 不支持的原因（结合代码与常见痛点）
            //!? 构建输入被固定为“无变种”的 AssetBundleBuild，清单与运行时也没有任何“按变种选择”的逻辑。
            //!? 变种会显著复杂化：依赖解析与清单引用、跨版本复用、下载与缓存一致性、加密（不同变种二进制不同）、以及地址定位都需要感知“变种”，而 YooAsset 的清单与运行时加载路径以“单一 BundleName + 文件名风格 + FileHash”为基准，选择直接放弃变种，保证一包一名、规则稳定。
            //!? 唯一触及变种的地方在 CreatePipelineBuild()这里，明确将 build.assetBundleVariant 置为空字符串
        }

        /// <summary>
        /// 获取所有写入补丁清单的资源（ECollectorType.MainAssetCollector）
        /// </summary>
        public BuildAssetInfo[] GetAllManifestAssetInfos()
        {
            return AllPackAssets.Where(t => t.CollectorType == ECollectorType.MainAssetCollector).ToArray();
        }

        /// <summary>
        /// 创建PackageBundle类
        /// 注意：PackageUnityHash 不写入清单
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