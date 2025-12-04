using System;
using System.Linq;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.Build.Pipeline.Interfaces;

namespace YooAsset.Editor
{
    public class TaskVerifyBuildResult_SBP : IBuildTask
    {
        void IBuildTask.Run(BuildContext context)
        {
            var buildParametersContext = context.GetContextObject<BuildParametersContext>();
            var buildParameters = buildParametersContext.Parameters as ScriptableBuildParameters;

            // 验证构建结果
            if (buildParameters.VerifyBuildingResult)
            {
                var buildResultContext = context.GetContextObject<TaskBuilding_SBP.BuildResultContext>();
                VerifyingBuildingResult(context, buildResultContext.Results);
            }
        }

        /// <summary>
        /// 验证构建结果
        /// </summary>
        private void VerifyingBuildingResult(BuildContext context, IBundleBuildResults buildResults)
        {
            var buildMapContext = context.GetContextObject<BuildMapContext>();
            //! 实际产出集，来自 buildResults.BundleInfos.Keys，即 SBP 真正构建出的每个 Bundle 的名称，由 TaskBuilding_SBP 执行 ContentPipeline.BuildAssetBundles 后得到）。。
            List<string> unityBuildContent = buildResults.BundleInfos.Keys.ToList();        // Unity SBP 输出的每个 Bundle 的名称（与 AssetBundleBuild.assetBundleName 一致，未带最终发布时的哈希重命名）。BuildBundleInfo -> AssetBundleBuild -> unityBuildContent

            //! 计划内容集，来自构建前的打包规划，由 TaskGetBuildMap 阶段生成。
            List<string> planningContent = buildMapContext.Collection.Select(t => t.BundleName).ToList();

            //! 用 Except 做双向差集（实际-计划、计划-实际），分别定位“多出来的包”和“缺失的包”，便于给出准确告警与抛错。

            // 2. 验证差异
            List<string> exceptBundleList1 = unityBuildContent.Except(planningContent).ToList();
            if (exceptBundleList1.Count > 0)
            {
                foreach (var exceptBundle in exceptBundleList1)
                {
                    string warning = BuildLogger.GetErrorMessage(ErrorCode.UnintendedBuildBundle, $"Found unintended build bundle : {exceptBundle}");
                    BuildLogger.Warning(warning);
                }

                string exception = BuildLogger.GetErrorMessage(ErrorCode.UnintendedBuildResult, $"Unintended build, See the detailed warnings !");
                throw new Exception(exception);
            }

            // 3. 验证差异
            List<string> exceptBundleList2 = planningContent.Except(unityBuildContent).ToList();
            if (exceptBundleList2.Count > 0)
            {
                foreach (var exceptBundle in exceptBundleList2)
                {
                    string warning = BuildLogger.GetErrorMessage(ErrorCode.UnintendedBuildBundle, $"Found unintended build bundle : {exceptBundle}");
                    BuildLogger.Warning(warning);
                }

                string exception = BuildLogger.GetErrorMessage(ErrorCode.UnintendedBuildResult, $"Unintended build, See the detailed warnings !");
                throw new Exception(exception);
            }

            BuildLogger.Log("Build results verify success!");
        }
    }
}