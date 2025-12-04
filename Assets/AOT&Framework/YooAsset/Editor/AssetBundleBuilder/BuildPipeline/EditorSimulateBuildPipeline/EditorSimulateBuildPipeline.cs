
using System.Collections.Generic;
using System;

namespace YooAsset.Editor
{
    public class EditorSimulateBuildPipeline : IBuildPipeline
    {
        public BuildResult Run(BuildParameters buildParameters, bool enableLog)
        {
            if (buildParameters is EditorSimulateBuildParameters)
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
                    new TaskPrepare_ESBP(),                 // 校验与准备：调用 BuildParameters.CheckBuildParameters()，不做任何真实打包相关的准备（如构建缓存、压缩、加密配置等仅做透传/记录）。
                    new TaskGetBuildMap_ESBP(),             // 收集资源与分包：根据收集规则（Collector）产出“要打进包”的资源图谱，写入 BuildMapContext。这一步决定每个资源归属哪个“Bundle”（虚拟 Bundle）。
                    new TaskUpdateBundleInfo_ESBP(),        // 写入虚拟 Bundle 的元信息：不依赖 Unity 的打包输出。让清单具备“可用的 Bundle 元数据”，但这些元数据指向“工程资源”，而不是物理 AB 文件。
                    new TaskCreateManifest_ESBP()           // 生成清单产物：把收集到的包、资源、依赖、标签等写入清单，并在版本目录写出(.version/.hash/.bytes)。返回的 BuildResult.OutputPackageDirectory 即该版本目录（被上层当作 packageRoot 返回）
                };
            return pipeline;
        }
    }
}