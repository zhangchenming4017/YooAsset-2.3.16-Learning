using System.Collections;
using System.Collections.Generic;

namespace YooAsset.Editor
{
    /// <summary>
    /// 收集阶段的“素材”
    /// 来源：各收集器根据 Filter/Pack/Address 规则产出。
    /// 内容：CollectorType、BundleName（初始由 PackRule 决定，仅对“主动收集”的资源有值）、Address、AssetInfo、AssetTags、DependAssets（主/静态资源的依赖图）。
    /// 作用：表达“规则层”的结果，尚未具备“构建期”所需的引用计数、共享包决策等数据。
    /// </summary>
    public class CollectAssetInfo
    {
        /// <summary>
        /// 收集器类型
        /// </summary>
        public ECollectorType CollectorType { private set; get; }

        /// <summary>
        /// 资源包名称
        /// 此资源包名不是收集器Package的名字，而是最后资源打进的AB名字
        /// </summary>
        public string BundleName { private set; get; }

        /// <summary>
        /// 可寻址地址，一般是包名/文件夹名/null + '_' + 文件名(不含Asset/...路径前缀和扩展名)
        /// </summary>
        public string Address { private set; get; }

        /// <summary>
        /// 资源信息
        /// </summary>
        public AssetInfo AssetInfo { private set; get; }

        /// <summary>
        /// 资源分类标签
        /// </summary>
        public List<string> AssetTags { private set; get; }

        /// <summary>
        /// 依赖的资源列表(包括直接依赖和间接依赖)
        /// 如果Command.IsFlagSet(ECollectFlags.IgnoreGetDependencies)为真，则为空
        /// </summary>
        public List<AssetInfo> DependAssets = new List<AssetInfo>();


        public CollectAssetInfo(ECollectorType collectorType, string bundleName, string address, AssetInfo assetInfo, List<string> assetTags)
        {
            CollectorType = collectorType;
            BundleName = bundleName;
            Address = address;
            AssetInfo = assetInfo;
            AssetTags = assetTags;
        }
    }
}