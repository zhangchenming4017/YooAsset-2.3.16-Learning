using UnityEditor;
using UnityEngine;

namespace YooAsset.Editor
{
    /// <summary>
    /// *组装编辑器模式的BuildParameters（构建参数），构建对应模式的构建管线
    /// </summary>
    public static class AssetBundleSimulateBuilder
    {
        /// <summary>
        /// 模拟构建：生成“虚拟Bundle”的清单产物（.version/.hash/.bytes），不产出真实AB文件
        /// 返回的 PackageRootDirectory 就是 Editor 模式下需要的 packageRoot
        /// </summary>
        public static PackageInvokeBuildResult SimulateBuild(PackageInvokeBuildParam buildParam)
        {
            string packageName = buildParam.PackageName;
            string buildPipelineName = buildParam.BuildPipelineName;

            // 仅处理指定的模拟构建管线
            if (buildPipelineName == "EditorSimulateBuildPipeline")
            {
                // 1) 组装构建参数（用于“模拟管线”）
                var buildParameters = new EditorSimulateBuildParameters();
                // 清单输出根目录（如 <ProjectRoot>/YooAssets），由 Helper 统一生成，避免硬编码
                buildParameters.BuildOutputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot();// C:/Projects/UnityProjects/HybridCLR_YooAsset_Test/Bundles
                // StreamingAssets 根路径（模拟构建不拷贝内置文件，但保持字段一致）
                buildParameters.BuildinFileRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot();// C:/Projects/UnityProjects/HybridCLR_YooAsset_Test/Assets/StreamingAssets/yoo
                // 指定构建管线为 EditorSimulate
                buildParameters.BuildPipeline = EBuildPipeline.EditorSimulateBuildPipeline.ToString();// EditorSimulateBuildPipeline
                // 关键：使用 VirtualBundle，清单中不指向真实AB，而是指向工程内 AssetPath
                buildParameters.BuildBundleType = (int)EBuildBundleType.VirtualBundle;// 1
                // 使用当前编辑器平台作为目标平台（影响输出目录结构）
                buildParameters.BuildTarget = EditorUserBuildSettings.activeBuildTarget;// StandaloneWindows64
                // 包名与版本（模拟构建通常固定为 "Simulate"）
                buildParameters.PackageName = packageName;
                buildParameters.PackageVersion = "Simulate";
                // 生成文件命名风格（即使是模拟构建，清单与命名规则也需一致）
                buildParameters.FileNameStyle = EFileNameStyle.HashName;// HashName
                // 不拷贝到 StreamingAssets（模拟构建无需内置文件）
                buildParameters.BuildinFileCopyOption = EBuildinFileCopyOption.None;
                buildParameters.BuildinFileCopyParams = string.Empty;
                // 使用 Unity 的依赖数据库进行依赖分析，速度快、与工程一致
                buildParameters.UseAssetDependencyDB = true;

                // 2) 运行模拟构建管线：生成清单（.version/.hash/.bytes）到输出目录
                var pipeline = new EditorSimulateBuildPipeline();
                BuildResult buildResult = pipeline.Run(buildParameters, false);
                if (buildResult.Success)
                {
                    // 3) 返回给上层：用于 Editor 文件系统初始化的根目录
                    var reulst = new PackageInvokeBuildResult();
                    reulst.PackageRootDirectory = buildResult.OutputPackageDirectory;
                    return reulst;
                }
                else
                {
                    Debug.LogError(buildResult.ErrorInfo);
                    throw new System.Exception($"{nameof(EditorSimulateBuildPipeline)} build failed !");
                }
            }
            else
            {
                // 支持扩展：可插入其它自定义管线
                throw new System.NotImplementedException(buildPipelineName);
            }
        }
    }
}