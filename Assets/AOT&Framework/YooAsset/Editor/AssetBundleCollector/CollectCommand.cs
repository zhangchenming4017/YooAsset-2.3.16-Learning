
namespace YooAsset.Editor
{
    public enum ECollectFlags
    {
        None = 0,

        /// <summary>
        /// 不收集依赖资源
        /// 在 AssetBundleCollector.GetAllDependencies 中直接返回空列表。主/静态资源的CollectAssetInfo.DependAssets 为空。
        /// RemoveZeroReferenceAssets 阶段看不到任何“被依赖集合”，因此所有 DependAssetCollector 的条目都会被判定为“零引用”并被剔除。
        /// 共享打包/引用计数等依赖相关逻辑基本失效（没依赖图可用）。
        /// 构建极简、极快；Unity 在真实打包时仍会把“未显式分包”的依赖资源自动打进主资源所在包（隐式依赖）。
        /// </summary>
        IgnoreGetDependencies = 1 << 0,

        /// <summary>
        /// 忽略静态收集器
        /// 它让收集流程跳过 ECollectorType.StaticAssetCollector 类型的收集器（在 AssetBundleCollector.GetAllCollectAssets 处直接早退）。
        /// 静态收集器本意是“参与打包但不写清单”，常用于强制某批资源的分包形态（如合成大包）。忽略它们可以加速，也让分包仅由主收集器与依赖关系决定。
        /// </summary>
        IgnoreStaticCollector = 1 << 1,

        /// <summary>
        /// 忽略依赖收集器
        /// 直接跳过 ECollectorType.DependAssetCollector 的扫描与产物（GetAllCollectAssets 早退）。
        /// 但仍会为主/静态资源调用 GetDependencies 构建 DependAssets 列表。
        /// 没有“显式依赖项”的候选资产（因为依赖收集器被禁用了），但依赖图还在。
        /// 依赖资源会作为“隐式依赖”被打进引用它们的主包（如果未被分配独立包名）。
        /// 若启用共享打包规则（EnableSharePackRule），框架可依据引用关系给这些“隐式依赖”分配共享包名；否则它们通常随主资源进同一个包。
        /// </summary>
        IgnoreDependCollector = 1 << 2,

        /*
        1.仅开 IgnoreGetDependencies：
        只收集 Main/Static 的“显式资源”；Depend 列表为空；Depend 收集器即使启用也会被后续剔除；Unity 在打包时把依赖塞进对应主包。
        补充：Unity 的打包规则（不论内置 BuildPipeline 还是 SBP）是：当某资源被指定进某个 Bundle，而它依赖的其它资源没有被显式指定到别的 Bundle 时，这些依赖会被“自动拉入”该 Bundle（隐式依赖）。
        补充：在 YooAsset 中，开启 IgnoreGetDependencies 后，我们不再显式为依赖资源计算/分配 Bundle（也不会通过 Depend 收集器产出候选），因此这些依赖在 Unity 打包阶段会按规则自动并入各自的主资源所在的 Bundle。
        虽然构建很快，但常用依赖可能被多个主包各自打入，造成重复。要避免重复，需要依赖图（不开 IgnoreGetDependencies）+ 共享打包规则（或依赖收集器）把公共依赖抽成共享包。

        2.仅开 IgnoreDependCollector：
        仍计算 Main/Static 的依赖；不跑依赖收集器；若未启用共享规则，依赖随主包；启用共享规则则可把常用依赖拆到共享包。
        */
    }

    public class CollectCommand
    {
        /// <summary>
        /// 包裹名称
        /// </summary>
        public string PackageName { private set; get; }

        /// <summary>
        /// 忽略规则实例
        /// </summary>
        public IIgnoreRule IgnoreRule { private set; get; }


        /// <summary>
        /// 模拟构建模式
        /// </summary>
        public bool SimulateBuild
        {
            set
            {
                SetFlag(ECollectFlags.IgnoreGetDependencies, value);
                SetFlag(ECollectFlags.IgnoreStaticCollector, value);
                SetFlag(ECollectFlags.IgnoreDependCollector, value);
            }
        }

        /// <summary>
        /// 窗口收集模式
        /// </summary>
        public int CollectFlags { set; get; } = 0;

        /// <summary>
        /// 资源包名唯一化
        /// </summary>
        public bool UniqueBundleName { set; get; }

        /// <summary>
        /// 使用资源依赖数据库
        /// </summary>
        public bool UseAssetDependencyDB { set; get; }

        /// <summary>
        /// 启用可寻址资源定位
        /// </summary>
        public bool EnableAddressable { set; get; }

        /// <summary>
        /// 支持无后缀名的资源定位地址
        /// </summary>
        public bool SupportExtensionless { set; get; }

        /// <summary>
        /// 资源定位地址大小写不敏感
        /// </summary>
        public bool LocationToLower { set; get; }

        /// <summary>
        /// 包含资源GUID数据
        /// </summary>
        public bool IncludeAssetGUID { set; get; }

        /// <summary>
        /// 自动收集所有着色器
        /// </summary>
        public bool AutoCollectShaders { set; get; }

        private AssetDependencyCache _assetDependency;
        public AssetDependencyCache AssetDependency
        {
            get
            {
                if (_assetDependency == null)
                    _assetDependency = new AssetDependencyCache(UseAssetDependencyDB);
                return _assetDependency;
            }
        }

        public CollectCommand(string packageName, IIgnoreRule ignoreRule)
        {
            PackageName = packageName;
            IgnoreRule = ignoreRule;
        }

        /// <summary>
        /// 设置标记位
        /// </summary>
        public void SetFlag(ECollectFlags flag, bool isOn)
        {
            if (isOn)
                CollectFlags |= (int)flag;  // 开启指定标志位
            else
                CollectFlags &= ~(int)flag; // 关闭指定标志位
        }

        /// <summary>
        /// 查询标记位
        /// </summary>
        public bool IsFlagSet(ECollectFlags flag)
        {
            return (CollectFlags & (int)flag) != 0;
        }
    }
}