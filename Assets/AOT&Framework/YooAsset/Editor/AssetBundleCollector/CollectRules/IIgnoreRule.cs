
namespace YooAsset.Editor
{
    /// <summary>
    /// 资源忽略规则接口
    /// 是“全局负选”规则：无论主资源还是依赖资源，统一决定“哪些资源一律跳过”。
    /// 它不参与搜索，只在枚举出的资源上做排除判断。
    /// </summary>
    public interface  IIgnoreRule
    {
        bool IsIgnore(AssetInfo assetInfo);
    }
}