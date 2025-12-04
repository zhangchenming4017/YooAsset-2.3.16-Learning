using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;

namespace YooAsset.Editor
{
    public abstract class TaskUpdateBundleInfo
    {
        public void UpdateBundleInfo(BuildContext context)
        {
            var buildParametersContext = context.GetContextObject<BuildParametersContext>();
            var buildMapContext = context.GetContextObject<BuildMapContext>();
            string pipelineOutputDirectory = buildParametersContext.GetPipelineOutputDirectory();   // 中间产物目录（OutputCache）
            string packageOutputDirectory = buildParametersContext.GetPackageOutputDirectory();     // 最终发布目录
            int outputNameStyle = (int)buildParametersContext.Parameters.FileNameStyle;

            // 1.检测文件名长度
            foreach (var bundleInfo in buildMapContext.Collection)
            {
                // NOTE：检测文件名长度不要超过260字符。
                string fileName = bundleInfo.BundleName;
                if (fileName.Length >= 260)
                {
                    string message = BuildLogger.GetErrorMessage(ErrorCode.CharactersOverTheLimit, $"Bundle file name character count exceeds limit : {fileName}");
                    throw new Exception(message);
                }
            }

            // 2.更新构建输出的文件路径
            foreach (var bundleInfo in buildMapContext.Collection)
            {
                bundleInfo.BuildOutputFilePath = $"{pipelineOutputDirectory}/{bundleInfo.BundleName}";
                if (bundleInfo.Encrypted)
                    bundleInfo.PackageSourceFilePath = bundleInfo.EncryptedFilePath;        //! 指向 TaskEncryption 生成的.encrypt加密文件(通常在同一中间目录)
                else
                    bundleInfo.PackageSourceFilePath = bundleInfo.BuildOutputFilePath;      //! 指向 Task_Building 生成的原始.bundle
            }

            // 3.更新文件其它信息，用“源文件”（指“要被发布/拷贝”的AB文件源头，如果启用了加密，则是加密后的文件；否则就是 Unity 打出来的原始 .bundle）计算并回填了最终要写进清单的文件元信息：
            foreach (var bundleInfo in buildMapContext.Collection)
            {
                bundleInfo.PackageUnityHash = GetUnityHash(bundleInfo, context);
                bundleInfo.PackageUnityCRC = GetUnityCRC(bundleInfo, context);
                bundleInfo.PackageFileHash = GetBundleFileHash(bundleInfo, buildParametersContext);
                bundleInfo.PackageFileCRC = GetBundleFileCRC(bundleInfo, buildParametersContext);
                bundleInfo.PackageFileSize = GetBundleFileSize(bundleInfo, buildParametersContext);
            }

            // 4.更新补丁包输出的文件路径
            foreach (var bundleInfo in buildMapContext.Collection)
            {
                string bundleName = bundleInfo.BundleName;
                string fileHash = bundleInfo.PackageFileHash;
                string fileExtension = ManifestTools.GetRemoteBundleFileExtension(bundleName);
                string fileName = ManifestTools.GetRemoteBundleFileName(outputNameStyle, bundleName, fileExtension, fileHash);  // 命名规则实现（哈希名/原名/原名_哈希）
                bundleInfo.PackageDestFilePath = $"{packageOutputDirectory}/{fileName}";
            }
        }

        protected abstract string GetUnityHash(BuildBundleInfo bundleInfo, BuildContext context);
        protected abstract uint GetUnityCRC(BuildBundleInfo bundleInfo, BuildContext context);
        protected abstract string GetBundleFileHash(BuildBundleInfo bundleInfo, BuildParametersContext buildParametersContext);
        protected abstract uint GetBundleFileCRC(BuildBundleInfo bundleInfo, BuildParametersContext buildParametersContext);
        protected abstract long GetBundleFileSize(BuildBundleInfo bundleInfo, BuildParametersContext buildParametersContext);
    }
}