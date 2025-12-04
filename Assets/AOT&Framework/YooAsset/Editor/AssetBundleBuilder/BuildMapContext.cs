using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Pipeline;

namespace YooAsset.Editor
{
    /// <summary>
    /// 保存：BuildBundleInfo集合、资源收集命令Command等
    /// </summary>
    public class BuildMapContext : IContextObject
    {
        /// <summary>
        /// 资源包集合，键：包名，值：待打包单元BuildBundleInfo 在TaskGetBuildMap第10步
        /// </summary>
        private readonly Dictionary<string, BuildBundleInfo> _bundleInfoDic = new Dictionary<string, BuildBundleInfo>(10000);

        /// <summary>
        /// 图集资源集合
        /// </summary>
        public readonly List<BuildAssetInfo> SpriteAtlasAssetList = new List<BuildAssetInfo>(10000);

        /// <summary>
        /// 未被依赖的资源列表，后续在TaskCreateReport中被写入构建报告 BuildReport，供排查与审计使用。在TaskGetBuildMap第4步
        /// 1.可追踪性：构建结束后有据可查，哪些资源被依赖收集器扫描到了但实际上没有被任何主/静态资源引用，从而被剔除。
        /// 2.配置纠错：方便你调整收集器规则（Filter/Pack/Address）或项目引用关系，避免“误收集却无引用”的资源占用构建时间或潜在造成包体冗余。
        /// 3.质量审计/CI：报告可被比对，若零引用列表异常增多可作为构建门禁信号。
        /// 4.尺寸优化：ReportIndependAsset 持有 FileSize，便于评估本次剔除了多少“无效资源”。
        /// </summary>
        public readonly List<ReportIndependAsset> IndependAssets = new List<ReportIndependAsset>(1000);

        /// <summary>
        /// 参与构建的资源总数
        /// 说明：包括主动收集的资源以及其依赖的所有资源
        /// </summary>
        public int AssetFileCount;

        /// <summary>
        /// 资源收集命令
        /// </summary>
        public CollectCommand Command { set; get; }

        /// <summary>
        /// 资源包信息列表
        /// </summary>
        public Dictionary<string, BuildBundleInfo>.ValueCollection Collection
        {
            get
            {
                return _bundleInfoDic.Values;
            }
        }


        /// <summary>
        /// 添加一个打包资源
        /// </summary>
        public void PackAsset(BuildAssetInfo assetInfo)
        {
            string bundleName = assetInfo.BundleName;
            if (string.IsNullOrEmpty(bundleName))
                throw new Exception("Should never get here !");

            if (_bundleInfoDic.TryGetValue(bundleName, out BuildBundleInfo bundleInfo))
            {
                bundleInfo.PackAsset(assetInfo);
            }
            else
            {
                BuildBundleInfo newBundleInfo = new BuildBundleInfo(bundleName);
                newBundleInfo.PackAsset(assetInfo);
                _bundleInfoDic.Add(bundleName, newBundleInfo);
            }

            // 统计所有的精灵图集
            if (assetInfo.AssetInfo.IsSpriteAtlas())
            {
                SpriteAtlasAssetList.Add(assetInfo);
            }
        }

        /// <summary>
        /// 是否包含资源包
        /// </summary>
        public bool IsContainsBundle(string bundleName)
        {
            return _bundleInfoDic.ContainsKey(bundleName);
        }

        /// <summary>
        /// 获取资源包信息，如果没找到返回NULL
        /// </summary>
        public BuildBundleInfo GetBundleInfo(string bundleName)
        {
            if (_bundleInfoDic.TryGetValue(bundleName, out BuildBundleInfo result))
            {
                return result;
            }
            throw new Exception($"Should never get here ! Not found bundle : {bundleName}");
        }

        /// <summary>
        /// 获取构建管线里需要的数据
        /// 汇总所有 BuildBundleInfo，生成 AssetBundleBuild[]
        /// 在实际构建时，这个数组就是 Unity 打包 API/SBP 的“直接输入”
        /// · 旧版内置API：  AssetBundleManifest unityManifest = BuildPipeline.BuildAssetBundles(outputDir, buildMapContext.GetPipelineBuilds(), buildOptions, target);
        /// ·       SBP：  var buildContent = new BundleBuildContent(buildMapContext.GetPipelineBuilds());
        //                 ReturnCode exitCode = ContentPipeline.BuildAssetBundles(parameters, buildContent, out results, taskList);
        /// </summary>
        public UnityEditor.AssetBundleBuild[] GetPipelineBuilds()
        {
            List<UnityEditor.AssetBundleBuild> builds = new List<UnityEditor.AssetBundleBuild>(_bundleInfoDic.Count);
            foreach (var bundleInfo in _bundleInfoDic.Values)
            {
                builds.Add(bundleInfo.CreatePipelineBuild());
            }
            return builds.ToArray();
        }

        /// <summary>
        /// 创建空的资源包
        /// </summary>
        public void CreateEmptyBundleInfo(string bundleName)
        {
            if (IsContainsBundle(bundleName) == false)
            {
                var bundleInfo = new BuildBundleInfo(bundleName);
                _bundleInfoDic.Add(bundleName, bundleInfo);
            }
        }
    }
}