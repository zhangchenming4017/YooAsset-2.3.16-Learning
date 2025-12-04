using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;

namespace YooAsset.Editor
{
    /// <summary>
    /// 构建参数
    /// </summary>
    public abstract class BuildParameters
    {
        /// <summary>
        /// 构建输出的根目录，C:/Projects/UnityProjects/HybridCLR_YooAsset_Test/Bundles
        /// </summary>
        public string BuildOutputRoot;

        /// <summary>
        /// 内置文件的根目录，C:/Projects/UnityProjects/HybridCLR_YooAsset_Test/Assets/StreamingAssets/yoo
        /// </summary>
        public string BuildinFileRoot;

        /// <summary>
        /// 构建管线名称
        /// </summary>
        public string BuildPipeline;

        /// <summary>
        /// 构建资源包类型
        /// </summary>
        public int BuildBundleType;

        /// <summary>
        /// 构建的平台，StandaloneWindows64
        /// </summary>
        public BuildTarget BuildTarget;

        /// <summary>
        /// 构建的包裹名称
        /// </summary>
        public string PackageName;

        /// <summary>
        /// 构建的包裹版本，Simulate
        /// </summary>
        public string PackageVersion;

        /// <summary>
        /// 构建的包裹备注
        /// </summary>
        public string PackageNote;

        /// <summary>
        /// 清空构建缓存文件
        /// </summary>
        public bool ClearBuildCacheFiles = false;

        /// <summary>
        /// 使用资源依赖缓存数据库
        /// 说明：开启此项可以极大提高资源收集速度！
        /// </summary>
        public bool UseAssetDependencyDB = false;

        /// <summary>
        /// 启用共享资源打包
        /// </summary>
        public bool EnableSharePackRule = false;

        /// <summary>
        /// 对单独引用的共享资源进行独立打包，默认开启
        /// 说明：关闭该选项单独引用的共享资源将会构建到引用它的资源包内！
        /// </summary>
        public bool SingleReferencedPackAlone = true;

        /// <summary>
        /// 验证构建结果
        /// </summary>
        public bool VerifyBuildingResult = false;

        /// <summary>
        /// 资源包名称样式
        /// </summary>
        public EFileNameStyle FileNameStyle = EFileNameStyle.HashName;

        /// <summary>
        /// 内置文件的拷贝选项
        /// </summary>
        public EBuildinFileCopyOption BuildinFileCopyOption = EBuildinFileCopyOption.None;

        /// <summary>
        /// 内置文件的拷贝参数
        /// </summary>
        public string BuildinFileCopyParams;

        /// <summary>
        /// 资源包加密服务类
        /// </summary>
        public IEncryptionServices EncryptionServices;

        /// <summary>
        /// 资源清单加密服务类
        /// </summary>
        public IManifestProcessServices ManifestProcessServices;

        /// <summary>
        /// 资源清单解密服务类
        /// </summary>
        public IManifestRestoreServices ManifestRestoreServices;

        private string _pipelineOutputDirectory = string.Empty;
        private string _packageOutputDirectory = string.Empty;
        private string _packageRootDirectory = string.Empty;
        private string _buildinRootDirectory = string.Empty;

        /// <summary>
        /// 检测构建参数是否合法
        /// </summary>
        public virtual void CheckBuildParameters()
        {
            // 检测当前是否正在构建资源包
            if (UnityEditor.BuildPipeline.isBuildingPlayer)
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.ThePipelineIsBuiding, "The pipeline is buiding, please try again after finish !");
                throw new Exception(message);
            }

            // 检测构建参数合法性
            if (BuildTarget == BuildTarget.NoTarget)
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.NoBuildTarget, "Please select the build target platform !");
                throw new Exception(message);
            }
            if (string.IsNullOrEmpty(BuildOutputRoot))
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.BuildOutputRootIsNullOrEmpty, "Build output root is null or empty !");
                throw new Exception(message);
            }
            if (string.IsNullOrEmpty(BuildinFileRoot))
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.BuildinFileRootIsNullOrEmpty, "Buildin file root is null or empty !");
                throw new Exception(message);
            }
            if (string.IsNullOrEmpty(BuildPipeline))
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.BuildPipelineIsNullOrEmpty, "Build pipeline is null or empty !");
                throw new Exception(message);
            }
            if (BuildBundleType == (int)EBuildBundleType.Unknown)
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.BuildBundleTypeIsUnknown, $"Build bundle type is unknown {BuildBundleType} !");
                throw new Exception(message);
            }
            if (string.IsNullOrEmpty(PackageName))
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.PackageNameIsNullOrEmpty, "Package name is null or empty !");
                throw new Exception(message);
            }
            if (string.IsNullOrEmpty(PackageVersion))
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.PackageVersionIsNullOrEmpty, "Package version is null or empty !");
                throw new Exception(message);
            }

            // 设置默认备注信息
            if (string.IsNullOrEmpty(PackageNote))
            {
                PackageNote = DateTime.Now.ToString();
            }
        }


        /// <summary>
        /// 管线的中间/缓存输出（如日志、临时文件，不是最终发布目录。其中大量 .bundle、link.xml、buildlogtep.json 等正是构建任务链的中间产物与日志/辅助文件。
        /// .bundle文件是 TaskBuilding_BBP 产出的“原始/未处理”包，后续任务会据此做校验、加密、重命名/归档等。
        /// link.xml：用于代码裁剪（IL2CPP/Unity Linker）的保留列表，防止从 AB 中反射到的类型被裁剪。
        /// 获取构建管线的输出目录，{BuildOutputRoot}/{BuildTarget}/{PackageName}/{YooAssetSettings.OutputFolderName}。
        /// 例如EditorSimulateBuildParameters：C:\Projects\UnityProjects\HybridCLR_YooAsset_Test\Bundles\StandaloneWindows64\DefaultPackage\OutputCache
        /// </summary>
        /// <returns></returns>
        public virtual string GetPipelineOutputDirectory()
        {
            if (string.IsNullOrEmpty(_pipelineOutputDirectory))
            {
                _pipelineOutputDirectory = $"{BuildOutputRoot}/{BuildTarget}/{PackageName}/{YooAssetSettings.OutputFolderName}";
            }
            return _pipelineOutputDirectory;
        }

        /// <summary>
        /// 本次构建版本的最终产物目录，包含清单产物（.version/.hash/.bytes）和AB包的生成目录
        /// 如果配置了首包拷贝（BuildinFileCopyOption），被选中的清单/AB 会再被复制一份到 GetBuildinRootDirectory()，供运行时离线读取。
        /// 获取本次构建的补丁输出目录，{BuildOutputRoot}/{BuildTarget}/{PackageName}/{PackageVersion}。
        /// 例如EditorSimulateBuildParameters：C:\Projects\UnityProjects\HybridCLR_YooAsset_Test\Bundles\StandaloneWindows64\DefaultPackage\Simulate
        /// </summary>
        public virtual string GetPackageOutputDirectory()
        {
            if (string.IsNullOrEmpty(_packageOutputDirectory))
            {
                _packageOutputDirectory = $"{BuildOutputRoot}/{BuildTarget}/{PackageName}/{PackageVersion}";
            }
            return _packageOutputDirectory;
        }

        /// <summary>
        /// 构建输出“包根目录”（版本父目录），用于存放该包的所有版本目录和 OutputCache
        /// 获取本次构建的补丁根目录{BuildOutputRoot}/{BuildTarget}/{PackageName}
        /// </summary>
        public virtual string GetPackageRootDirectory()
        {
            if (string.IsNullOrEmpty(_packageRootDirectory))
            {
                _packageRootDirectory = $"{BuildOutputRoot}/{BuildTarget}/{PackageName}";
            }
            return _packageRootDirectory;
        }

        /// <summary>
        /// 首包拷贝目标，仅当 BuildinFileCopyOption 选择了拷贝时使用
        /// 获取内置资源的根目录，StreamingAssets/yoo/<PackageName>
        /// </summary>
        public virtual string GetBuildinRootDirectory()
        {
            if (string.IsNullOrEmpty(_buildinRootDirectory))
            {
                _buildinRootDirectory = $"{BuildinFileRoot}/{PackageName}";
            }
            return _buildinRootDirectory;
        }
    }
}