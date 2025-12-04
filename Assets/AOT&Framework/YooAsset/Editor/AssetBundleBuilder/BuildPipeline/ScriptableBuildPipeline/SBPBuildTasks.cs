using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;

namespace UnityEditor.Build.Pipeline.Tasks
{
    public static class SBPBuildTasks
    {
        public static IList<IBuildTask> Create(string builtInShaderBundleName, string monoScriptsBundleName)
        {
            var buildTasks = new List<IBuildTask>();

            // Setup
            buildTasks.Add(new SwitchToBuildPlatform());                // 1. 将构建环境切到目标平台（BuildTarget/BuildTargetGroup），确保导入设置、平台宏、Player 脚本编译等按目标平台进行。
            buildTasks.Add(new RebuildSpriteAtlasCache());              // 2. 维护/重建精灵图集的缓存，保证依赖与打包使用最新的图集数据。

            // Player Scripts
            buildTasks.Add(new BuildPlayerScripts());                   // 3. 编译 Player 使用的托管脚本程序集（按目标平台/裁剪设置），为后续依赖分析与 link.xml 生成提供输入。
            buildTasks.Add(new PostScriptsCallback());                  // 4. 脚本编译后的回调钩子，给后续任务机会读取/处理编译产物信息。

            // Dependency
            buildTasks.Add(new CalculateSceneDependencyData());         // 5. 扫描场景，计算场景中引用的资源与对象依赖图。
#if UNITY_2019_3_OR_NEWER
            buildTasks.Add(new CalculateCustomDependencyData());        // 6. 计算“自定义依赖”数据（供扩展点/定制增量判断、哈希参与等使用）。
#endif
            buildTasks.Add(new CalculateAssetDependencyData());         // 7. 对非场景资源做依赖分析，建立对象级/资源级依赖关系图，为后续打包决策（Packing）与写入（Writing）提供依据。
            buildTasks.Add(new StripUnusedSpriteSources());             // 8. 剔除未被使用的精灵源数据，减少冗余输入，保证图集和依赖更精确。
            if (string.IsNullOrEmpty(builtInShaderBundleName) == false)
                buildTasks.Add(new CreateBuiltInShadersBundle(builtInShaderBundleName));    // 指定包名：把项目依赖到的 Unity 内置着色器打进一个专用 bundle，避免运行时缺失内置 Shader。
            if (string.IsNullOrEmpty(monoScriptsBundleName) == false)
                buildTasks.Add(new CreateMonoScriptBundle(monoScriptsBundleName));          // 如指定包名：把 MonoScript 资产打进一个专用 bundle，便于后续生成 link.xml、依赖追踪等（并非运行时从 AB 动态加载脚本代码）。
            buildTasks.Add(new PostDependencyCallback());               // 9. 依赖阶段的后置回调，留给扩展处理依赖图。

            // Packing
            buildTasks.Add(new GenerateBundlePacking());                // 10. 将对象/资源映射到具体的 Bundle（依据传入的 AssetBundleBuild[] 与依赖关系），形成打包计划。
            buildTasks.Add(new UpdateBundleObjectLayout());             // 11. 调整与固化 Bundle 内对象的布局/顺序，提升确定性与稳定性（有助于增量与哈希稳定）。
            buildTasks.Add(new GenerateBundleCommands());               // 12. 生成底层“写入命令”（Write Commands），描述要如何把哪些对象写到哪些文件。
            buildTasks.Add(new GenerateSubAssetPathMaps());             // 13. 生成子资源路径映射（如一个资源文件内的多个子资源：子贴图、子对象等）。
            buildTasks.Add(new GenerateBundleMaps());                   // 14. 生成 Bundle 级别的映射信息（依赖、引用、文件与对象映射），供结果与后续步骤使用。
            buildTasks.Add(new PostPackingCallback());                  // 15. 打包映射阶段的后置回调，允许扩展或微调写入计划。

            // Writing 16/17:打包初始AB
            buildTasks.Add(new WriteSerializedFiles());                 // 16. 按写入命令把要写入 Bundle 的对象序列化成中间文件块（.bundle 内容体）（写入命令的执行产物，尚未归档/压缩）。
            buildTasks.Add(new ArchiveAndCompressBundles());            // 17. 将序列化文件归档并按设定 BundleCompression（Uncompressed/LZ4/LZMA）压缩，生成最终 AB 二进制。
            buildTasks.Add(new AppendBundleHash());                     // 18. 计算并附加/记录每个 Bundle 的内容哈希（供结果、后续命名/校验使用）。
            buildTasks.Add(new GenerateLinkXml());                      // 19. 依据构建内容与托管引用生成 link.xml，用于 Player 构建阶段防裁剪（保留被 AB 间接引用到的类型）。
            buildTasks.Add(new PostWritingCallback());                  // 20. 写入阶段后置回调，允许最后的清理或扩展处理。

            return buildTasks;

            //? 和 YooAsset 后续的衔接
            //!? TaskUpdateBundleInfo_SBP：将“中间产物的 bundle 路径”登记为 BuildOutputFilePath；若加密则切换为 EncryptedFilePath；计算最终 FileHash/ CRC / Size 并决定“发布文件名 / 路径”（版本目录）。
            //!? TaskCreatePackage_SBP：把源文件（可能是加密后的）拷贝到版本目录；复制 link.xml / build 日志等。
            //!? TaskCreateManifest_SBP / Report：生成清单与报告，清单里写入最终文件名、FileHash / CRC / Size、依赖、Encrypted 标记等。

            //! 在收集器收集阶段，构建的CollectAssetInfo、BuildAssetInfo具有依赖关系、待构建单元BuildBundleInfo不记录包之间依赖关系，虽然BuildAssetInfo记录了引用包名，主要用于计算共享资源的包名（引用计数）、剔除零引用资源、构建资产级依赖（GetAssetDependBundleIDs）、标签传播、报告生成。
            //! 因此我们提供的（根据BuildBundleInfo构建的）AssetBundleBuild[]也不包含资源和资源、资源和包之间的依赖关系，AssetBundleBuild则是AB包构建图，提供了包名、和包中所有资源的路径AssetPath。
            //! 真实AB包中的资源的依赖关系是在 7.CalculateAssetDependencyData、10.GenerateBundlePacking、14.GenerateBundleMaps中完成的。1.	扫描每个待写入对象的引用图（序列化对象引用）2.	归并出“哪些对象去了哪个 bundle”。3	当一个对象引用了另一个 bundle 中的对象，就记一条 bundle → bundle 的依赖边。
            //! 结果写入 IBundleBuildResults.BundleInfos[bundleName].Dependencies（这是“引擎权威的最终包依赖”）。此时资源级依赖图可能被“剔除冗余、图集合并、脚本剥离”等行为调整，所以真正的 bundle 依赖只以引擎结果为准。
        }
    }
}