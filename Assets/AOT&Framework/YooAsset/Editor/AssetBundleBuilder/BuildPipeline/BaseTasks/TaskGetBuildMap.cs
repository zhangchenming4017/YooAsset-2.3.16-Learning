using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;

namespace YooAsset.Editor
{
    public class TaskGetBuildMap
    {
        /// <summary>
        /// 生成资源构建上下文
        /// </summary>
        public BuildMapContext CreateBuildMap(bool simulateBuild, BuildParameters buildParameters)
        {
            BuildMapContext context = new BuildMapContext();
            var packageName = buildParameters.PackageName;

            Dictionary<string, BuildAssetInfo> allBuildAssetInfos = new Dictionary<string, BuildAssetInfo>(1000);

            // 1. 获取所有收集器收集的资源（这里也可以叫主资源。但在其他地方，主资源也可以理解为ECollectorType == MainAssetCollector的收集器收集到的资源）
            bool useAssetDependencyDB = buildParameters.UseAssetDependencyDB;
            var collectResult = AssetBundleCollectorSettingData.Setting.BeginCollect(packageName, simulateBuild, useAssetDependencyDB);
            List<CollectAssetInfo> allCollectAssets = collectResult.CollectAssets;  // allCollectAssets是收集器[主动收集的资源]

            // 2. 剔除未被[main/static收集器]引用的依赖项资源，剔除 [Depend收集器收集资源] 中未被 [Main/Static收集器收集资源] 依赖的资源，如果没有设置Depend收集器直接返回不做剔除。
            RemoveZeroReferenceAssets(context, allCollectAssets);

            // 3. 录入所有[主动收集资源]，剔除非主资源的AssetTags
            foreach (var collectAssetInfo in allCollectAssets)
            {
                //! 按道理是不应该出现重复的资源
                if (allBuildAssetInfos.ContainsKey(collectAssetInfo.AssetInfo.AssetPath))
                {
                    throw new Exception($"Should never get here !");
                }

                //? 为什么这里需要清除非主资源的AssetTags
                //!? 标签只对“主资源”有意义（会写入清单 AssetList，用于首包拷贝按标签、内容审计等）。
                //!? 静态/依赖收集器产物不会出现在清单的 AssetList 中，给它们保留标签会产生歧义，甚至影响“按标签拷贝首包”。
                if (collectAssetInfo.CollectorType != ECollectorType.MainAssetCollector)
                {
                    if (collectAssetInfo.AssetTags.Count > 0)
                    {
                        collectAssetInfo.AssetTags.Clear();
                        string warning = BuildLogger.GetErrorMessage(ErrorCode.RemoveInvalidTags, $"Remove asset tags that don't work, see the asset collector type : {collectAssetInfo.AssetInfo.AssetPath}");
                        BuildLogger.Warning(warning);
                    }
                }
                //! 对“主动收集”的资源（Main/Static/Depend）所在的资源包(BundleName)由 PackRule 直接给出（CollectAssetInfo.BundleName）。
                var buildAssetInfo = new BuildAssetInfo(collectAssetInfo.CollectorType, collectAssetInfo.BundleName, collectAssetInfo.Address, collectAssetInfo.AssetInfo);
                buildAssetInfo.AddAssetTags(collectAssetInfo.AssetTags);
                allBuildAssetInfos.Add(collectAssetInfo.AssetInfo.AssetPath, buildAssetInfo);
            }

            // 4. 录入所有[主动收集资源][依赖的其它资源](可能不在收集器收集的资源中)并添加引用关系
            foreach (var collectAssetInfo in allCollectAssets)
            {
                string bundleName = collectAssetInfo.BundleName;    //! 获取当前资源所在包名
                foreach (var dependAsset in collectAssetInfo.DependAssets)
                {
                    if (allBuildAssetInfos.TryGetValue(dependAsset.AssetPath, out var value))
                    {
                        value.AddReferenceBundleName(bundleName);   //! 为当前资源的依赖资源添加引用关系
                    }
                    //? 真的可能存在不在allBuildAssetInfos中的资源吗？在3. 录入所有收集器主动收集的资源中不是把allCollectAssets（包括三种收集器类型）中所有的assetPath代表的资源加入了吗？因为我们不是在AssetDependencyDatabase种递归获取了所有依赖资源吗？
                    //!? 许多依赖资源并没有被任何收集器显式收集（尤其是材质/贴图/动画/Shader 等）。它们只作为“依赖项”出现在 CollectAssetInfo.DependAssets 中。
                    //!? 这些“仅依赖出现”的资源，需要在步骤4补建对应的 BuildAssetInfo(dependAsset)，其 CollectorType == None，以便：
                    //!? 1.参与引用计数（AddReferenceBundleName）。2.参与后续共享打包/着色器打包的“二次分配”。3.建立强类型依赖图（步骤5）
                    //!? 即使依赖是通过 AssetDependencyDatabase 递归得到的，它们最初也不在“主动收集集合”里，所以必须在步骤4补建。

                    //! 未出现在allBuildAssetInfos的依赖资源，则创建BuildAssetInfo，但没有设置包名
                    else
                    {
                        //! 这里的BuildAssetInfo只设置了AssetInfo，没有设置Address、BundleName等。这些未被显式收集的资源所在资源包BundleName会在后续6/7步设置，没有设置包名的资源会在9被剔除
                        var buildAssetInfo = new BuildAssetInfo(dependAsset);
                        buildAssetInfo.AddReferenceBundleName(bundleName);      // 注意：这里的bundleName不是资源打包后所在的包名，而是引用了该资源所在包的包名。
                        allBuildAssetInfos.Add(dependAsset.AssetPath, buildAssetInfo);
                    }
                }
            }

            // 5. 填充所有[主动收集资源]的[依赖列表]
            foreach (var collectAssetInfo in allCollectAssets)
            {
                var dependAssetInfos = new List<BuildAssetInfo>(collectAssetInfo.DependAssets.Count);
                foreach (var dependAsset in collectAssetInfo.DependAssets)
                {
                    if (allBuildAssetInfos.TryGetValue(dependAsset.AssetPath, out BuildAssetInfo value))
                        dependAssetInfos.Add(value);
                    //! 在4中，按道理我们已经录入了所有可能用到的资源，即主动收集的资源（Main/Static/Depend(可能有裁剪)）和它们所依赖的资源
                    else
                        throw new Exception("Should never get here !");
                }
                allBuildAssetInfos[collectAssetInfo.AssetInfo.AssetPath].SetDependAssetInfos(dependAssetInfos);
            }

            // 6. 自动收集所有依赖的着色器
            if (collectResult.Command.AutoCollectShaders)
            {
                // 获取着色器打包规则结果
                PackRuleResult shaderPackRuleResult = DefaultPackRule.CreateShadersPackRuleResult();
                string shaderBundleName = shaderPackRuleResult.GetBundleName(collectResult.Command.PackageName, collectResult.Command.UniqueBundleName);
                foreach (var buildAssetInfo in allBuildAssetInfos.Values)
                {
                    //? 我在YooAsset官方项目里面默认的Shader收集器组的里面的收集器类型都是StaticAssetCollector，FilterRuleName要么是CollectShader要么是CollectShaderVariants，按道理来说这些着色器CollectAssetInfo的CollectorType也是StaticAssetCollector怎么会是ECollectorType.None
                    //!? 只有显式被收集器收集的着色器才会是 StaticAssetCollector；很多时候项目没有为所有 Shader 建收集器，它们仅作为依赖出现（步骤4补建，类型就是 None）。
                    //!? 自动收集着色器逻辑只针对 CollectorType == None 的 Shader 分包，避免覆盖你显式配置的 Shader 收集器结果。
                    //!? 若你已通过 Static/Main 收集了 Shader，这些条目在步骤3就有 BundleName，步骤6不会改动它们。
                    if (buildAssetInfo.CollectorType == ECollectorType.None)
                    {
                        if (buildAssetInfo.AssetInfo.IsShaderAsset())
                        {
                            buildAssetInfo.SetBundleName(shaderBundleName);
                        }
                    }
                }
            }

            // 7. 计算共享资源的包名
            if (buildParameters.EnableSharePackRule)
            {
                //? 这个前置和后置处理似乎都还没实现，可能现版本的YooAsset还没有这个功能吧？
                //!? 这两个是“扩展钩子”，默认 no-op，留给自定义管线或后续版本扩展。
                //!? 实际的共享打包核心逻辑在 ProcessingPackShareBundle：
                //!? 对“尚未分配 BundleName”的依赖资源，依据引用计数（GetReferenceBundleCount()）和 SingleReferencedPackAlone 决策生成共享包名（GetShareBundleName → 目录路径 + 后缀 → GetShareBundleName(...).GetShareBundleName(...)）。
                PreProcessPackShareBundle(buildParameters, collectResult.Command, allBuildAssetInfos);
                foreach (var buildAssetInfo in allBuildAssetInfos.Values)
                {
                    //! 会对“尚未分配 BundleName”的依赖资产，根据引用计数和 SingleReferencedPackAlone 为其生成共享包名。
                    if (buildAssetInfo.HasBundleName() == false)
                    {
                        ProcessingPackShareBundle(buildParameters, collectResult.Command, buildAssetInfo);
                    }
                }
                PostProcessPackShareBundle(buildParameters, collectResult.Command, allBuildAssetInfos);
            }

            // 8. 记录关键信息
            context.AssetFileCount = allBuildAssetInfos.Count;
            context.Command = collectResult.Command;

            // 9. 移除不参与构建的资源
            List<BuildAssetInfo> removeBuildList = new List<BuildAssetInfo>();
            foreach (var buildAssetInfo in allBuildAssetInfos.Values)
            {
                //? 为什么没有包名就是不参与构建的资源
                //!? Unity 的打包 API 只对“被显式指定到某个 Bundle 的资源”生成条目；未指定 Bundle 的依赖会作为“隐式依赖”被自动打进引用它们的主包。
                //!? 我们在步骤6/7已经给“需要独立/共享分包”的依赖资源分配好 BundleName；剩下未分配的，就是应该走“隐式依赖”的那一类，不该再作为独立构建条目（否则会产生重复或冲突）。
                if (buildAssetInfo.HasBundleName() == false)
                    removeBuildList.Add(buildAssetInfo);
            }
            foreach (var removeValue in removeBuildList)
            {
                allBuildAssetInfos.Remove(removeValue.AssetInfo.AssetPath);
            }

            // 10. 构建资源列表
            var allPackAssets = allBuildAssetInfos.Values.ToList();
            //! 如果打包资源为空，则报错，并写入日志
            if (allPackAssets.Count == 0)
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.PackAssetListIsEmpty, "The pack asset info is empty !");
                throw new Exception(message);
            }
            //! 以包名为键将资源分为不同的BuildBundleInfo，在BuildBundleInfo中通过AssetPath来区分不同的资源
            foreach (var assetInfo in allPackAssets)
            {
                context.PackAsset(assetInfo);
            }

            return context;
        }
        private void RemoveZeroReferenceAssets(BuildMapContext context, List<CollectAssetInfo> allCollectAssets)
        {
            // 1. 检测依赖资源收集器是否存在，不存在则返回
            if (allCollectAssets.Exists(x => x.CollectorType == ECollectorType.DependAssetCollector) == false)
                return;

            // 2. 获取所有主资源的依赖资源集合
            HashSet<string> allDependAsset = new HashSet<string>();
            foreach (var collectAsset in allCollectAssets)
            {
                var collectorType = collectAsset.CollectorType;
                if (collectorType == ECollectorType.MainAssetCollector || collectorType == ECollectorType.StaticAssetCollector)
                {
                    foreach (var dependAsset in collectAsset.DependAssets)
                    {
                        if (allDependAsset.Contains(dependAsset.AssetPath) == false)
                            allDependAsset.Add(dependAsset.AssetPath);
                    }
                }
            }

            // 3. 找出所有零引用的依赖资源集合
            List<CollectAssetInfo> removeList = new List<CollectAssetInfo>();
            foreach (var collectAssetInfo in allCollectAssets)
            {
                var collectorType = collectAssetInfo.CollectorType;
                if (collectorType == ECollectorType.DependAssetCollector)
                {
                    if (allDependAsset.Contains(collectAssetInfo.AssetInfo.AssetPath) == false)
                        removeList.Add(collectAssetInfo);
                }
            }

            // 4. 移除所有零引用的依赖资源
            foreach (var removeValue in removeList)
            {
                //! 发出警告日志：BuildLogger.Warning(ErrorCode.FoundUndependedAsset, ... )
                string warning = BuildLogger.GetErrorMessage(ErrorCode.FoundUndependedAsset, $"Found undepended asset and remove it : {removeValue.AssetInfo.AssetPath}");
                BuildLogger.Warning(warning);

                //! 非模拟管线都会执行 TaskCreateReport，这些信息最终被写入构建报告 BuildReport，供排查与审计使用
                var independAsset = new ReportIndependAsset();
                independAsset.AssetPath = removeValue.AssetInfo.AssetPath;
                independAsset.AssetGUID = removeValue.AssetInfo.AssetGUID;
                independAsset.AssetType = removeValue.AssetInfo.AssetType.ToString();
                independAsset.FileSize = FileUtility.GetFileSize(removeValue.AssetInfo.AssetPath);
                context.IndependAssets.Add(independAsset);

                allCollectAssets.Remove(removeValue);
            }
        }

        #region 共享资源打包规则
        /// <summary>
        /// 共享资源打包前置处理
        /// </summary>
        protected virtual void PreProcessPackShareBundle(BuildParameters buildParameters, CollectCommand command, Dictionary<string, BuildAssetInfo> allBuildAssetInfos)
        {
        }

        /// <summary>
        /// 共享资源打包机制
        /// 如果引用数小于等于1 且 SingleReferencedPackAlone为false，那么这个单引用资源就没有BundleName，后续会根据Unity的打包机制进入引用它的包中。
        /// 除此之外的引用资源（包括单引用资源）就会添加设置好的共享资源包的包名，被打进共享资源包（[共享资源包名]跟父文件夹名和包名有关）。
        /// </summary>
        protected virtual void ProcessingPackShareBundle(BuildParameters buildParameters, CollectCommand command, BuildAssetInfo buildAssetInfo)
        {
            PackRuleResult packRuleResult = GetShareBundleName(buildAssetInfo);     // 父文件夹路径 + .bundle扩展名
            if (packRuleResult.IsValid() == false)      // 包名和扩展名不为空
                return;

            // 处理单个引用的共享资源
            if (buildAssetInfo.GetReferenceBundleCount() <= 1)
            {
                if (buildParameters.SingleReferencedPackAlone == false)
                    return;
            }

            // 设置共享资源包名
            string shareBundleName = packRuleResult.GetShareBundleName(command.PackageName, command.UniqueBundleName);  // 替换'/'、'·'、'.'为'_'，同时加上“share”，默认格式（{packageName}_）share_{bundleName}.{_bundleExtension}
            buildAssetInfo.SetBundleName(shareBundleName);
        }
        private PackRuleResult GetShareBundleName(BuildAssetInfo buildAssetInfo)
        {
            string bundleName = Path.GetDirectoryName(buildAssetInfo.AssetInfo.AssetPath);
            PackRuleResult result = new PackRuleResult(bundleName, DefaultPackRule.AssetBundleFileExtension);
            return result;
        }

        /// <summary>
        /// 共享资源打包后置处理
        /// </summary>
        protected virtual void PostProcessPackShareBundle(BuildParameters buildParameters, CollectCommand command, Dictionary<string, BuildAssetInfo> allBuildAssetInfos)
        {
        }
        #endregion
    }
}