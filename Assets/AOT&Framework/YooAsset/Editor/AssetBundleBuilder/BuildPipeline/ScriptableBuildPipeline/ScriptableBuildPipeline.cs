using System;
using System.Collections;
using System.Collections.Generic;

namespace YooAsset.Editor
{
    public class ScriptableBuildPipeline : IBuildPipeline
    {
        public BuildResult Run(BuildParameters buildParameters, bool enableLog)
        {
            if (buildParameters is ScriptableBuildParameters)
            {
                AssetBundleBuilder builder = new AssetBundleBuilder();
                return builder.Run(buildParameters, GetDefaultBuildPipeline(), enableLog);
            }
            else
            {
                throw new Exception($"Invalid build parameter type : {buildParameters.GetType().Name}");
            }
        }

        /// <summary>
        /// 获取默认的构建流程
        /// </summary>
        private List<IBuildTask> GetDefaultBuildPipeline()
        {
            List<IBuildTask> pipeline = new List<IBuildTask>
                {
                    new TaskPrepare_SBP(),
                    new TaskGetBuildMap_SBP(),
                    new TaskBuilding_SBP(),                 // 生成中间产物。使用 Unity 的 Scriptable Build Pipeline 包，生成中间产物到 {BuildOutputRoot}/{BuildTarget}/{PackageName}/{OutputFolderName}，文件名：就是你配置/规则确定的 BundleName（未加任何哈希/后缀处理）。伴随产物：SBP 构建结果（哈希/CRC/依赖）保存在 buildResults，并存放日志如 buildlogtep.json，可选 link.xml，构建结果通过 Result 返回。
                    new TaskVerifyBuildResult_SBP(),        // 对照计划校验构建结果。对比BuildBundleInfo与IBundleBuildResults.BundleInfos
                    new TaskEncryption_SBP(),               // （可选）加密。若启用加密，TaskEncryption_SBP 读中间目录的原始 .bundle → 生成 .encrypt → 标记 bundleInfo.Encrypted = true 并保存到 EncryptedFilePath
                    new TaskUpdateBundleInfo_SBP(),         // 决定“发布[源文件]（指“要被发布/拷贝”的AB文件源头”）、计算 FileHash/CRC/Size和“最终文件名/路。基于“加密与否”决定源文件路径，源文件：若加密，取 .encrypt；否则取原始 .bundle，最终文件名：根据 FileNameStyle（哈希名/原名/原名_哈希），用“源文件内容的 MD5”参与命名，最终路径：拼到“版本目录” {BuildOutputRoot}/{BuildTarget}/{PackageName}/{PackageVersion}/{最终文件名}，同时计算：最终入清单的 FileHash/CRC/Size（都来自“源文件”字节）
                    new TaskCreateManifest_SBP(),           // 生成清单产物（.version/.hash/.bytes）到最终的版本目录。清单写入：每个包的 FileName（与上一步生成的目标名一致）、FileHash、CRC、FileSize、Encrypted、依赖/标签等
                    new TaskCreateReport_SBP(),             // 生成构建报告（.report）到最终的版本目录。
                    new TaskCreatePackage_SBP(),            // 拷贝源文件到版本目录（发布落地）。把每个 PackageSourceFilePath 复制到 PackageDestFilePath；并复制 buildlogtep.json 和（可选）link.xml 到版本目录
                    new TaskCopyBuildinFiles_SBP(),         // （可选）首包拷贝。按策略把清单和部分/全部 AB 再拷到 StreamingAssets/yoo/PackageName
                    new TaskCreateCatalog_SBP()             // （可选）目录编目。生成内置资源目录（供运行时使用）
                };
            return pipeline;

            //? 中间产物与最终产物有什么不同？
            //!? 文件名：
            //!?    中间产物：原始BundleName
            //!?    最终产物：依据 FileNameStyle 可能带内容哈希或“原名_哈希”，用于发布与缓存一致性
            //!? 是否加密：
            //!?    中间产物：通常未加密。“发布文件名的扩展名”仍取原始 bundleName 的扩展名（即使加密），见 ManifestTools.GetRemoteBundleFileExtension。.encrypt 只存在于中间目录，最终发布文件仍按命名规则产出（哈希名/原名/原名_哈希）。
            //!?    最终产物：若启用加密，使用加密后的字节与命名，并将加密标志/CRC等写入清单
            //!? 存放位置与意义：
            //!?    中间产物：中间目录（OutputCache），构建工作区/缓存，可清理
            //!?    最终产物：版本目录（PackageVersion），发布目录，需长期保留，和清单一一对应
            //!? 附属文件：
            //!?    中间产物：构建日志、link.xml、Unity构建结果（Builtin管线会有 UnityManifest）
            //!?    最终产物：清单（.version/.hash/.bytes/.json/.report）+ 最终命名后的 AB 文件

            //? 清单文件（.version/.hash/.bytes/.json/.report）含义及用途
            //!? .bytes（Binary Manifest）
            //!?    文件名：{PackageName}_{PackageVersion}.bytes
            //!?    内容：资源清单的二进制形式（资产列表、Bundle 列表、依赖关系、每个 Bundle 的 FileName/FileHash/CRC/FileSize/Encrypted 等）
            //!?    是否处理：写入时会经 IManifestProcessServices.ProcessManifest 处理（可压缩/加密）；读取时用 IManifestRestoreServices.RestoreManifest 还原
            //!?    用途：运行时真正加载的清单数据源
            //!? .json（Readable Manifest）
            //!?    文件名：{PackageName}_{PackageVersion}.json
            //!?    内容：与 .bytes 相同的信息，但明文可读，便于调试/排查
            //!?    用途：开发与分析用；通常不在运行时读取
            //!? .hash（Manifest Hash）
            //!?    文件名：{PackageName}_{PackageVersion}.hash
            //!?    内容：对 .bytes 文件做 CRC32 得到的字符串（源码：HashUtility.FileCRC32(packagePath)）
            //!?    用途：快速比对清单是否变更（补丁/热更校验）
            //!? .version（Package Version）
            //!?    文件名：{PackageName}.version
            //!?    内容：纯文本，记录此次构建的版本号（PackageVersion）
            //!?    用途：记录当前版本；配合首包/热更流程决定目标版本
            //!? .report（Build Report）
            //!?    文件名：{PackageName}_{PackageVersion}.report（通过 YooAssetSettingsData.GetBuildReportFileName）
            //!?    内容：本次构建的统计与明细（总包数/大小、是否加密、每个 Bundle 的 FileName/FileHash/CRC/Size/标签；每个资源的归属与依赖等）
            //!?    用途：编辑器分析与排错，不参与运行时
            //!? 补充：
            //!?    buildlogtep.json：构建日志，由 TaskCreatePackage_SBP 一并复制到版本目录，便于留档/排查。
            //!?    link.xml（可选）：若启用写入（WriteLinkXML），用于 IL2CPP/Unity Linker 的裁剪保留，防止反射类型被裁掉。也会被复制到版本目录，且可进入首包目录。
            //!?    没加密时 EncryptedFilePath 为空且不会被使用；源文件就是中间目录里的 .bundle（BuildOutputFilePath）。
            //!?    启用加密时，中间目录会产出 .encrypt，源文件换为该加密文件；最终发布名仍按命名规则生成（扩展名保持与原始 bundle 一致），清单中会写入 Encrypted 标志及对应的哈希/CRC/大小，运行时用解密服务读取。

            //? 不同阶段资源/资源包的情况
            //!? 1.收集阶段（CollectAssetInfo，AssetBundleCollectorSetting）
            //!?    CollectAssetInfo.DependAssets：基于Unity的AssetDatabase.GetDependencies得到的“资源路径级”依赖列表（受IgnoreGetDependencies 影响）
            //!?    目的：为后面构建BuildAssetInfo的强类型依赖图准备原料
            //!? 2.构建阶段（BuildAssetInfo & BuildBundleInfo，TaskGetBuildMap）
            //!?    BuildAssetInfo.AllDependAssetInfos：把路径依赖替换成 BuildAssetInfo 引用，形成“资源节点依赖图”。
            //!?    BuildAssetInfo._referenceBundleNames / GetReferenceBundleCount(): 用于共享分包决策（引用计数）和剔除零引用
            //!?    BuildBundleInfo：只是把“显式需要打进某个 bundle 的资源”聚合，给 Unity 构建输入，不保存跨 bundle 依赖。 用途总结：
            //!?    作用：决策打包粒度（共享包、独立包、剔除零引用依赖）。 形成后续清单里“资产 → 依赖的其他 bundle”映射的依据（资产级依赖）。
            //!? 3.Unity构建阶段（IBundleBuildResults，TaskBuilding_SBP）
            //!?    AssetBundleBuild[]：只是把 BuildBundleInfo 转成 Unity 的输入格式，交给 SBP 构建，没有bundle之间的依赖关系。
            //!?    SBP内部任务（CalculateAssetDependencyData → GenerateBundlePacking → GenerateBundleMaps）会：
            //!?        1）扫描每个待写入对象的引用图（序列化对象引用）。
            //!?        2）归并出“哪些对象去了哪个 bundle”。
            //!?        3）当一个对象引用了另一个 bundle 中的对象，就记一条 bundle → bundle 的依赖边。
            //!?    结果产出到 IBundleBuildResults.BundleInfos[bundleName].Dependencies（这是“引擎权威的最终包依赖”）
            //!?    此时资源级依赖图可能被“剔除冗余、图集合并、脚本剥离”等行为调整，所以真正的 bundle 依赖只以引擎结果为准。
            //!? 4.更新构建信息阶段（TaskUpdateBundleInfo_SBP）
            //!?    为每个 BuildBundleInfo 记录 FileHash / FileCRC / FileSize / Encrypted / PackageSourceFilePath 等最终发布相关元数据（与依赖无关）。
            //!? 5.清单生成阶段（TaskCreateManifest_SBP / 基类 TaskCreateManifest）
            //!?    分两类依赖被写入清单：
            //!?    1）资产级依赖（PackageAsset.DependBundleIDs）：
            //!?    ProcessPacakgeAsset 内调用 GetAssetDependBundleIDs(mainAssetInfo)。
            //!?    使用 BuildAssetInfo.AllDependAssetInfos 过滤出“依赖中那些有独立 BundleName 的资源”，转换成“资产引用的 bundle ID 集合”。
            //!?    用途（运行时）：加载一个地址定位的主资源时，通过它的 DependBundleIDs 先加载依赖包，避免漏依赖。
            //!?    2）引擎/包级依赖（PackageBundle.DependBundleIDs）：
            //!?    ProcessBundleDepends 调用抽象 GetBundleDepends(context, bundleName)，在 SBP 子类中实现为读取 buildResults.BundleInfos[bundleName].Dependencies。
            //!?    排除自依赖后写入清单。
            //!?    用途（运行时）：如果你直接按 BundleName 加载一个主包，可用其 bundle 依赖列表做“预加载依赖包”或拓扑排序。
            //!?    3）两套依赖的意义：
            //!?    资产级：面向“按地址加载主资源”场景，粒度细
            //!?    Bundle级：面向“直接按包操作”时的调度与调试（例如检查多包共享、内置着色器、图集/脚本包引用）。
            //!?    Tag 传播与 stray bundle 警告也依赖资产级的映射（ProcessBundleTags）。
            //!? 6.运行时消费阶段（InitializationOperation + ResourcePackage）
            //!?    读取清单（PackageManifest）后：
            //!?    地址定位 → 找到主资源 → 取其 PackageAsset.DependBundleIDs → 加载依赖包 → 加载主包。
            //!?    或直接按 BundleName → 取 PackageBundle.DependBundleIDs → 深度优先 / 拓扑加载依赖。
            //!?    
        }
    }
}