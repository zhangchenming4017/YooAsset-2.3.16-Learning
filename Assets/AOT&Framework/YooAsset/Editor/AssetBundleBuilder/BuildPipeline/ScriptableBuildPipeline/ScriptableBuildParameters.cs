using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Pipeline;
using UnityEditor.Build.Pipeline.Interfaces;

namespace YooAsset.Editor
{
    public class ScriptableBuildParameters : BuildParameters
    {
        /// <summary>
        /// 压缩选项
        /// </summary>
        public ECompressOption CompressOption = ECompressOption.Uncompressed;

        /// <summary>
        /// 从文件头里剥离Unity版本信息
        /// </summary>
        public bool StripUnityVersion = false;

        /// <summary>
        /// 禁止写入类型树结构（可以降低包体和内存并提高加载效率）
        /// </summary>
        public bool DisableWriteTypeTree = false;

        /// <summary>
        /// 忽略类型树变化（无效参数）
        /// </summary>
        public bool IgnoreTypeTreeChanges = true;

        /// <summary>
        /// 自动建立资源对象对图集的依赖关系
        /// </summary>
        public bool TrackSpriteAtlasDependencies = false;


        /// <summary>
        /// 生成代码防裁剪配置
        /// </summary>
        public bool WriteLinkXML = true;

        /// <summary>
        /// 缓存服务器地址
        /// </summary>
        public string CacheServerHost;

        /// <summary>
        /// 缓存服务器端口
        /// </summary>
        public int CacheServerPort;


        /// <summary>
        /// 内置着色器资源包名称
        /// </summary>
        public string BuiltinShadersBundleName;

        /// <summary>
        /// Mono脚本资源包名称
        /// </summary>
        public string MonoScriptsBundleName;


        /// <summary>
        /// 获取可编程构建管线的构建参数
        /// <para>构建参数的作用：描述“如何/在哪儿构建”的全局构建参数（平台、输出目录、压缩、类型树/版本号写入、缓存/CacheServer、是否写 link.xml 等）。</para>
        /// <para>BundleBuildParameters.BuildTargetGroup：由当前 BuildTarget 推导对应的 BuildTargetGroup（影响平台宏、导入设置、压缩策略等）。</para>
        /// <para>BundleBuildParameters.OutputFolder：管线“中间产物/缓存”输出目录（非最终发布目录），SBP 会把构建日志、原始 .bundle、link.xml 等写到这里。</para>
        /// <para>BundleBuildParameters.BundleCompression：设定压缩策略，Uncompressed:优点：打包/加载更快、CPU 压力低；缺点：体积大、下载时间长。LZMA:优点：体积最小（单文件整体压缩）；缺点：首包解压慢、随机访问性能差。LZ4:优点：加载快（块压缩、可随机读取）、体积适中；通常是运行时的折中选择。</para>
        /// <para>BundleBuildParameters.ContentBuildFlags：内容构建标志。StripUnityVersion:从序列化文件头去掉 Unity 版本号，减少差异字节，有利于二进制对比/增量发布；对加载无功能性影响。DisableWriteTypeTree:可减包、降内存、略提速加载。关闭类型树后，跨版本兼容性与某些反射/类型变更排查会受限，需确保资源与运行时类型版本匹配。</para>
        /// <para>BundleBuildParameters.UseCache：启用 SBP 的构建缓存（Library/BuildCache 或 Unity Accelerator），基于输入/依赖哈希命中，可显著加快增量构建。</para>
        /// <para>BundleBuildParameters.CacheServerHost/CacheServerPort：指定远端缓存（Unity Accelerator/Cache Server）。本地团队共享时非常有用：同一资源/平台的构建结果可被复用。未配置主机/端口时，依然会使用本地缓存（UseCache=true）CI 或多人协同强烈建议配置加速器，能大幅缩短构建时间。</para>
        /// <para>BundleBuildParameters.WriteLinkXML（默认 true）：让 SBP 根据 AB 中的托管引用生成 link.xml，用于 Player 构建阶段防止 IL2CPP/Unity Linker 把被 AB 间接引用的类型裁剪掉。写入到“管线中间输出目录”。后续你的应用打包流程应把这些 link.xml 合并纳入 Player 构建。</para>
        /// <para>在 Scriptable Build Pipeline 路线（SBP）必须同时提供 BundleBuildParameters（构建配置）与 AssetBundleBuild[]（内容清单），SBP 才能产出 AB 与 IBundleBuildResults（含哈希/CRC/依赖等）。</para>
        /// <para>在 Unity 旧版内置管线中（非 SBP）不用 BundleBuildParameters，而是用 BuildAssetBundleOptions（来自 BuiltinBuildParameters.GetBundleBuildOptions()）+ AssetBundleBuild[] 调用 BuildPipeline.BuildAssetBundles(...)。</para>
        /// </summary>
        public BundleBuildParameters GetBundleBuildParameters()
        {
            var targetGroup = UnityEditor.BuildPipeline.GetBuildTargetGroup(BuildTarget);
            var pipelineOutputDirectory = GetPipelineOutputDirectory();
            var buildParams = new BundleBuildParameters(BuildTarget, targetGroup, pipelineOutputDirectory);

            if (CompressOption == ECompressOption.Uncompressed)
                buildParams.BundleCompression = UnityEngine.BuildCompression.Uncompressed;
            else if (CompressOption == ECompressOption.LZMA)
                buildParams.BundleCompression = UnityEngine.BuildCompression.LZMA;
            else if (CompressOption == ECompressOption.LZ4)
                buildParams.BundleCompression = UnityEngine.BuildCompression.LZ4;
            else
                throw new System.NotImplementedException(CompressOption.ToString());

            if (StripUnityVersion)
                buildParams.ContentBuildFlags |= UnityEditor.Build.Content.ContentBuildFlags.StripUnityVersion; // Build Flag to indicate the Unity Version should not be written to the serialized file.
            if (DisableWriteTypeTree)
                buildParams.ContentBuildFlags |= UnityEditor.Build.Content.ContentBuildFlags.DisableWriteTypeTree; //Do not include type information within the built content.

            buildParams.UseCache = true;
            buildParams.CacheServerHost = CacheServerHost;
            buildParams.CacheServerPort = CacheServerPort;
            buildParams.WriteLinkXML = WriteLinkXML;

            return buildParams;
        }
    }
}