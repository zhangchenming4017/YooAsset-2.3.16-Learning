using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace YooAsset.Editor
{
    /// <summary>
    /// *是对BuildParameters的包装，方便获取一些路径什么的
    /// </summary>
    public class BuildParametersContext : IContextObject
    {
        /// <summary>
        /// 构建参数
        /// </summary>
        public BuildParameters Parameters { private set; get; }


        public BuildParametersContext(BuildParameters parameters)
        {
            Parameters = parameters;
        }

        /// <summary>
        /// 检测构建参数是否合法
        /// </summary>
        public void CheckBuildParameters()
        {
            Parameters.CheckBuildParameters();
        }

        /// <summary>
        /// 获取构建管线的输出目录，例如EditorSimulateBuildParameters：C:\Projects\UnityProjects\HybridCLR_YooAsset_Test\Bundles\StandaloneWindows64\DefaultPackage\OutputCache
        /// 作用：存放构建过程中的中间产物与日志，不是发布目录。
        /// 内容：原始未处理的 .bundle、构建日志、临时清单、link.xml 等。
        /// 生命周期：仅供本次或多次构建复用/加速，安全删除不会影响已发布版本。
        /// 路径结构：BuildOutputRoot/BuildTarget/PackageName/OutputCache
        /// </summary>
        /// <returns></returns>
        public string GetPipelineOutputDirectory()
        {
            return Parameters.GetPipelineOutputDirectory();
        }

        /// <summary>
        /// 本次构建版本的最终产物目录，包含清单产物（.version/.hash/.bytes）和AB包的生成目录
        /// 如果配置了首包拷贝（BuildinFileCopyOption），被选中的清单/AB 会再被复制一份到 GetBuildinRootDirectory()，供运行时离线读取。
        /// 获取本次构建的补丁输出目录，例如EditorSimulateBuildParameters：C:\Projects\UnityProjects\HybridCLR_YooAsset_Test\Bundles\StandaloneWindows64\DefaultPackage\Simulate
        /// 作用：本次版本的“发布目录”，打出的清单与AB包最终落地在这里。
        /// 内容：清单文件（.version/.hash/.bytes）、最终命名后的 AssetBundle 文件等。
        /// 生命周期：随版本长期保留，用于分发与比对；不同版本各自独立。
        /// 路径结构：BuildOutputRoot/BuildTarget/PackageName/PackageVersion（编辑器模拟为 Simulate）
        /// </summary>
        public string GetPackageOutputDirectory()
        {
            return Parameters.GetPackageOutputDirectory();
        }

        /// <summary>
        /// 获取本次构建的补丁根目录
        /// 作用：一个包的“家目录”，承载该包所有版本目录和 OutputCache。
        /// 内容：OutputCache（上面那个目录）+ 各版本子目录（见下一个）。
        /// 生命周期：长期存在，跨版本共享。
        /// 路径结构：BuildOutputRoot/BuildTarget/PackageName
        /// </summary>
        public string GetPackageRootDirectory()
        {
            return Parameters.GetPackageRootDirectory();
        }

        /// <summary>
        /// 获取内置资源的根目录
        /// 作用：当配置了首包拷贝（BuildinFileCopyOption ≠ None）时，把被选中的清单/AB复制到这里，随应用打进 StreamingAssets，供离线/首包使用。
        /// 内容：被策略挑中的清单与部分或全部 AB（可按标签）。
        /// 生命周期：由拷贝策略决定，构建后用于运行时离线读取。
        /// 路径结构：BuildinFileRoot/PackageName（即 Assets/StreamingAssets/yoo/PackageName 下）
        /// </summary>
        public string GetBuildinRootDirectory()
        {
            return Parameters.GetBuildinRootDirectory();
        }
    }
}