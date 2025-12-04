
namespace YooAsset.Editor
{
    /// <summary>
    /// 首包资源文件的拷贝方式
    /// 被“清空”和“拷贝到”的目标目录是每个包的内置目录：Assets/StreamingAssets/yoo/<PackageName>/。
    /// 被“拷贝”的来源目录是本次构建版本的输出目录：<项目根>/Bundles/<BuildTarget>/<PackageName>/<PackageVersion>/。
    /// </summary>
    public enum EBuildinFileCopyOption
    {
        /// <summary>
        /// 不拷贝任何文件
        /// </summary>
        None = 0,

        /// <summary>
        /// 先清空已有文件，然后拷贝所有文件
        /// </summary>
        ClearAndCopyAll,

        /// <summary>
        /// 先清空已有文件，然后按照资源标签拷贝文件
        /// </summary>
        ClearAndCopyByTags,

        /// <summary>
        /// 不清空已有文件，直接拷贝所有文件（可能与旧文件并存）
        /// </summary>
        OnlyCopyAll,

        /// <summary>
        /// 不清空已有文件，直接按照资源标签拷贝文件
        /// </summary>
        OnlyCopyByTags,
    }
}