using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;

namespace YooAsset.Editor
{
    public class TaskPrepare_SBP : IBuildTask
    {
        void IBuildTask.Run(BuildContext context)
        {
            var buildParametersContext = context.GetContextObject<BuildParametersContext>();
            var buildParameters = buildParametersContext.Parameters as ScriptableBuildParameters;

            // 检测基础构建参数
            buildParametersContext.CheckBuildParameters();

            // 检测是否有未保存场景
            if (EditorTools.HasDirtyScenes())
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.FoundUnsavedScene, "Found unsaved scene !");
                throw new Exception(message);
            }

            // 删除包裹目录。目的：做一次“彻底的干净构建”，避免历史中间产物/旧版本残留对本次构建产生干扰
            if (buildParameters.ClearBuildCacheFiles)
            {
                // Deletes the build cache directory.
                UnityEditor.Build.Pipeline.Utilities.BuildCache.PurgeCache(false);          //! 清 SBP 的全局构建缓存

                string packageRootDirectory = buildParameters.GetPackageRootDirectory();    //! 包根目录
                if (EditorTools.DeleteDirectory(packageRootDirectory))
                {
                    BuildLogger.Log($"Delete package root directory: {packageRootDirectory}");
                }
            }

            // 检测包裹输出目录是否存在。目的：排除出现同名版本目录的情况
            string packageOutputDirectory = buildParameters.GetPackageOutputDirectory();    //! 版本输出目录（最终产物目录）
            if (Directory.Exists(packageOutputDirectory))
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.PackageOutputDirectoryExists, $"Package outout directory exists: {packageOutputDirectory}");
                throw new Exception(message);
            }

            // 如果输出目录不存在。目的：创建中间输出目录保存TaskBuilding_SBP调用ContentPipeline.BuildAssetBundles(...)产生的原始 .bundle和清单等，后续在TaskUpdateBundleInfo、TaskCreatePackage_SBP
            string pipelineOutputDirectory = buildParameters.GetPipelineOutputDirectory();  //! 管线中间输出目录
            if (EditorTools.CreateDirectory(pipelineOutputDirectory))
            {
                BuildLogger.Log($"Create pipeline output directory: {pipelineOutputDirectory}");
            }

            // 检测内置着色器资源包名称
            if (string.IsNullOrEmpty(buildParameters.BuiltinShadersBundleName))
            {
                string warning = BuildLogger.GetErrorMessage(ErrorCode.BuiltinShadersBundleNameIsNull, $"Builtin shaders bundle name is null. It will cause resource redundancy !");
                BuildLogger.Warning(warning);
            }
        }
    }
}