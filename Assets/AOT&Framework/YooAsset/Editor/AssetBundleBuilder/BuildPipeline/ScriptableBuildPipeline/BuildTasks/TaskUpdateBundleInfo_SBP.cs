using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;

namespace YooAsset.Editor
{
    /// <summary>
    /// TaskUpdateBundleInfo_SBP 用 PackageSourceFilePath（可能是加密后）计算 FileHash / FileCRC / FileSize，并根据 FileNameStyle 生成最终发布文件名（可能是哈希名、原名或“原名_哈希”），拼接出 PackageDestFilePath（位于版本目录）。
    /// </summary>
    public class TaskUpdateBundleInfo_SBP : TaskUpdateBundleInfo, IBuildTask
    {
        void IBuildTask.Run(BuildContext context)
        {
            UpdateBundleInfo(context);
        }

        //! 1.	PackageUnityHash / PackageUnityCRC：直接抄取上一步 Unity 的 Content Hash 与 CRC（只是存到 BuildBundleInfo，Hash 不写入清单，CRC 会写入）。

        /// <summary>
        /// 获取指定 Unity AssetBundle 的 Content Hash。该哈希同时受资源包内容与其依赖列表影响；
        /// 一旦依赖列表发生变化，Content Hash 也会随之变化。若在构建结果中未找到对应条目将抛出异常。
        /// </summary>
        protected override string GetUnityHash(BuildBundleInfo bundleInfo, BuildContext context)
        {
            // 注意：当资源包的依赖列表发生变化的时候，ContentHash也会发生变化！
            var buildResult = context.GetContextObject<TaskBuilding_SBP.BuildResultContext>();
            if (buildResult.Results.BundleInfos.TryGetValue(bundleInfo.BundleName, out var value))
            {
                return value.Hash.ToString();
            }
            else
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.NotFoundUnityBundleHash, $"Not found unity bundle hash : {bundleInfo.BundleName}");
                throw new Exception(message);
            }
        }
        /// <summary>
        /// 获取指定 Unity AssetBundle 的 Content CRC（来自 Unity 构建结果）。该值用于写入清单，若未找到对应条目将抛出异常。
        /// </summary>
        protected override uint GetUnityCRC(BuildBundleInfo bundleInfo, BuildContext context)
        {
            var buildResult = context.GetContextObject<TaskBuilding_SBP.BuildResultContext>();
            if (buildResult.Results.BundleInfos.TryGetValue(bundleInfo.BundleName, out var value))
            {
                return value.Crc;
            }
            else
            {
                string message = BuildLogger.GetErrorMessage(ErrorCode.NotFoundUnityBundleCRC, $"Not found unity bundle crc : {bundleInfo.BundleName}");
                throw new Exception(message);
            }
        }

        //! PackageFileHash / PackageFileCRC / PackageFileSize：对“最终用于发布的源文件”PackageSourceFilePath 的字节做 MD5、CRC32、Size 计算。
        //! 若启用了加密，源文件就是 .encrypt；否则就是原始 .bundle。这里的哈希与 CRC 是 YooAsset 自己基于最终分发物理文件重新计算的。
        protected override string GetBundleFileHash(BuildBundleInfo bundleInfo, BuildParametersContext buildParametersContext)
        {
            string filePath = bundleInfo.PackageSourceFilePath;
            return HashUtility.FileMD5(filePath);
        }
        protected override uint GetBundleFileCRC(BuildBundleInfo bundleInfo, BuildParametersContext buildParametersContext)
        {
            string filePath = bundleInfo.PackageSourceFilePath;
            return HashUtility.FileCRC32Value(filePath);
        }
        protected override long GetBundleFileSize(BuildBundleInfo bundleInfo, BuildParametersContext buildParametersContext)
        {
            string filePath = bundleInfo.PackageSourceFilePath;
            return FileUtility.GetFileSize(filePath);
        }
    }
}