using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;
using UnityEditor.Build.Pipeline.Tasks;

namespace YooAsset.Editor
{
    /// <summary>
    /// 生成中间产物。
    /// 使用 Unity 的 Scriptable Build Pipeline 包，生成原始AB包（.bundle文件，已经可以使用了）到 {BuildOutputRoot}/{BuildTarget}/{PackageName}/{OutputFolderName}，
    /// 文件名：就是你配置/规则确定的 BundleName（未加任何哈希/后缀处理）。
    /// 伴随产物：SBP 构建结果（哈希/CRC/依赖，是构建结果元数据）保存在 IBundleBuildResults，并存放日志如 buildlogtep.json，link.xml（若 WriteLinkXML = true），构建结果通过 Result 返回，还包含序列化临时块、打包命令产物、编译/依赖缓存（多位于 Library/BuildCache/Accelerator）等。
    /// 注意：这里还没有 YooAsset 的清单（manifest）、report、版本号文件，也没有“最终哈希命名”文件。
    /// </summary>
    public class TaskBuilding_SBP : IBuildTask
    {
        public class BuildResultContext : IContextObject
        {
            /// <summary>
            /// BuildResultContext.Results是 SBP（Scriptable Build Pipeline）返回的“本次构建结果数据容器”，核心用途是向后续任务提供：
            /// <para>1. BundleInfos(Dictionary<string, BundleDetails>)每个条目含：Hash(内容哈希，TaskUpdateBundleInfo_SBP 用于 PackageUnityHash)、Crc(Unity 计算的 CRC，写入 PackageUnityCRC)、Dependencies(其它 bundle 名列表，用于后续生成依赖关系)、Size / ObjectIDMap / SerializedFileIndices 等（内部写入辅助信息）</para>
            /// <para>2. WriteResults / BuildResults: 资产与场景的写入映射（用于定位对象/依赖、生成 link.xml 等）</para>
            /// <para>3. BuildTarget / ScriptCompilationResult 等（有助于裁剪与诊断）YooAsset 后续只挑它关心的：Hash、Crc、依赖结构。</para>
            /// </summary>
            public IBundleBuildResults Results;
            public string BuiltinShadersBundleName;
            public string MonoScriptsBundleName;

            //? 这里的哈希/CRC/依赖具体是什么？
            //!? 是构建结果元数据，保存在 IBundleBuildResults（内存）及部分日志/索引中
            //!? Unity Content Hash（SBP计算）：
            //!?    来源：IBundleBuildResults.BundleInfos[bundleName].Hash
            //!?    用途：用于增量构建命中、可复现性、结果确定性、追踪包是否“内容发生变化”、对齐 Unity 构建；YooAsset记录到 PackageUnityHash（不入清单，仅追踪）。
            //!? PackageFileHash（YooAsset自己算的MD5）：
            //!?    来源：对“最终发布文件字节”（可能已加密）做 MD5
            //!?    用途：用于生成最终发布名、跨版本复用判断、哈希冲突检测（入清单：FileHash）。
            //!? Unity CRC（引擎算）：
            //!?    来源：SBP 结果中的 Crc（或 Builtin 管线 GetCRCForAssetBundle）
            //!?    用途：用于 AssetBundle.LoadFromFile 加载校验（入清单：UnityCRC）。
            //!? PackageFileCRC（YooAsset算）：
            //!?    来源：对“最终发布文件字节”（可能已加密）做 CRC32
            //!?    用途：用于下载/缓存校验（入清单：FileCRC）
            //!? 依赖（BundleDependencies）（bundle → 依赖bundle名 列表）：
            //!?    来源：IBundleBuildResults.BundleInfos[bundleName].Dependencies
            //!?    用途：用于运行时先加载依赖包，再加载主包（入清单：DependBundleIDs）
            //!? 
            //!? 
        }

        void IBuildTask.Run(BuildContext context)
        {
            var buildParametersContext = context.GetContextObject<BuildParametersContext>();                      // 获取构建参数上下文
            var buildMapContext = context.GetContextObject<BuildMapContext>();                                         // 获取资源构建上下文
            var scriptableBuildParameters = buildParametersContext.Parameters as ScriptableBuildParameters;    // 将构建参数转成对应管线的类型

            //! 1. 内容输入：把 BuildMapContext.GetPipelineBuilds() 生成的 AssetBundleBuild[] 包装成 BundleBuildContent。
            var buildContent = new BundleBuildContent(buildMapContext.GetPipelineBuilds());         //! 创建容器 BundleBuildContent（SBP），包装“要构建的内容 AssetBundleBuild 集”。
            //? 为什么 BuildBundleInfo.CreatePipelineBuild 不显式加“依赖列表”
            //!? 依赖资产是否“独立分包”在 TaskGetBuildMap 阶段就决定了（通过 BuildAssetInfo.HasBundleName/共享打包规则等），CreatePipelineBuild 只负责把“本包显式条目”交给引擎。
            //!? 真正的“跨包依赖列表”由 SBP 在构建时计算（CalculateAssetDependencyData → GenerateBundleMaps），产出到 IBundleBuildResults.BundleInfos[bundleName].Dependencies。
            //!? 

            // 开始构建
            IBundleBuildResults buildResults;
            //! 2. 构建参数：用 ScriptableBuildParameters.GetBundleBuildParameters() 生成 BundleBuildParameters（平台、输出目录、压缩、缓存、link.xml 等）。
            var buildParameters = scriptableBuildParameters.GetBundleBuildParameters();             //! 创建构建参数 BundleBuildParameters（SBP），设策略（平台/压缩/缓存/标志位/输出目录）

            //! Built-in Shaders：当项目里资源依赖了内置着色器但你没显式收集时，SBP 可生成一个包含内置着色器的专用包，免得运行时报丢 Shader。
            string builtinShadersBundleName = scriptableBuildParameters.BuiltinShadersBundleName;
            //! Mono Scripts（MonoScript 资产）：供构建期分析与工具（例如生成 link.xml、依赖计算等）使用；它并不是让你在运行时从 AB 动态加载脚本代码。HybridCLR 下脚本由 AOT/热更程序集提供，不需要把脚本作为“资源”打进 AB。因此通常 MonoScriptsBundleName 置空SBP 就不会添加 CreateMonoScriptBundle 任务，也就不生成这个特殊包）
            string monoScriptsBundleName = scriptableBuildParameters.MonoScriptsBundleName;
            
            //! 3. 任务列表，得到SBP 的任务序列。这里的 IBuildTask 和先前构建管线中的 IBuildTask 是命名同名但不同命名空间的不同接口，这里的IBuildTask把“打 AB 的具体步骤”拆成可组合的任务（切平台、算依赖、打包布局、写文件、压缩、追加 Hash、生成 link.xml、可选内置 Shader 包和 MonoScript 包等）。
            var taskList = SBPBuildTasks.Create(builtinShadersBundleName, monoScriptsBundleName);

            //! 4. 执行构建，真正执行 Unity 的打包，输入/输出：BundleBuildParameters（怎么构）、BundleBuildContent（构什么）、IList<IBuildTask>（SBP任务序列）、out IBundleBuildResults（结果：哈希/CRC/依赖/输出路径等）、ReturnCode（<0 失败）。产生物：中间目录中的 .bundle、buildlogtep.json、可选 link.xml、缓存命中信息等。
            ReturnCode exitCode = ContentPipeline.BuildAssetBundles(buildParameters, buildContent, out buildResults, taskList);
            if (exitCode < 0)
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.UnityEngineBuildFailed, $"UnityEngine build failed ! ReturnCode : {exitCode}");
                throw new Exception(message);
            }

            // 说明：解决因为特殊资源包导致验证失败。
            // 例如：当项目里没有着色器，如果有依赖内置着色器就会验证失败。
            if (string.IsNullOrEmpty(builtinShadersBundleName) == false)
            {
                if (buildResults.BundleInfos.ContainsKey(builtinShadersBundleName))
                    buildMapContext.CreateEmptyBundleInfo(builtinShadersBundleName);
            }
            if (string.IsNullOrEmpty(monoScriptsBundleName) == false)
            {
                if (buildResults.BundleInfos.ContainsKey(monoScriptsBundleName))
                    buildMapContext.CreateEmptyBundleInfo(monoScriptsBundleName);
            }

            BuildLogger.Log("UnityEngine build success!");
            BuildResultContext buildResultContext = new BuildResultContext();
            buildResultContext.Results = buildResults;
            buildResultContext.BuiltinShadersBundleName = builtinShadersBundleName;
            buildResultContext.MonoScriptsBundleName = monoScriptsBundleName;
            context.SetContextObject(buildResultContext);
        }
    }
}