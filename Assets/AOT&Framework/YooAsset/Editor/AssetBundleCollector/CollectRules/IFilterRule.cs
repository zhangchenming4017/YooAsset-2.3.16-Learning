
namespace YooAsset.Editor
{
    public struct FilterRuleData
    {
        public string AssetPath;
        public string CollectPath;
        public string GroupName;
        public string UserData;

        public FilterRuleData(string assetPath, string collectPath, string groupName, string userData)
        {
            AssetPath = assetPath;
            CollectPath = collectPath;
            GroupName = groupName;
            UserData = userData;
        }
    }

    /// <summary>
    /// 资源过滤规则接口
    /// 是“正选”规则：按收集器维度决定“哪些资源要被当作候选主资源来收集”。
    /// 它既参与“如何搜到候选资源”（FindAssetType），也参与“命中后是否收集”（IsCollectAsset）。
    /// </summary>
    public interface IFilterRule
    {
        /// <summary>
        /// 搜寻的资源类型
        /// 说明：使用引擎方法搜索获取所有资源列表
        /// </summary>
        string FindAssetType { get; }

        /// <summary>
        /// 验证搜寻的资源是否为收集资源
        /// </summary>
        /// <returns>如果收集该资源返回TRUE</returns>
        bool IsCollectAsset(FilterRuleData data);
    }
}