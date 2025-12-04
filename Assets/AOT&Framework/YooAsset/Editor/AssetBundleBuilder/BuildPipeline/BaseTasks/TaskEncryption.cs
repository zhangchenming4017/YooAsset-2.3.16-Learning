using System;
using System.Linq;
using System.IO;
using System.Collections;
using System.Collections.Generic;

namespace YooAsset.Editor
{
    public class TaskEncryption
    {
        /// <summary>
        /// 加密文件
        /// </summary>
        public void EncryptingBundleFiles(BuildParametersContext buildParametersContext, BuildMapContext buildMapContext)
        {
            // 取出服务实例
            var encryptionServices = buildParametersContext.Parameters.EncryptionServices;
            // 若服务实例为空，返回
            if (encryptionServices == null)
                return;
            // 若类型是 EncryptionNone，返回
            if (encryptionServices.GetType() == typeof(EncryptionNone))
                return;

            int progressValue = 0;
            // 获取中间产物目录（OutputCache）
            string pipelineOutputDirectory = buildParametersContext.GetPipelineOutputDirectory();
            foreach (var bundleInfo in buildMapContext.Collection)
            {
                // 读取中间产物目录下 {pipelineOutputDirectory}/bundleName 原始.bundle
                EncryptFileInfo fileInfo = new EncryptFileInfo();
                fileInfo.BundleName = bundleInfo.BundleName;
                fileInfo.FileLoadPath = $"{pipelineOutputDirectory}/{bundleInfo.BundleName}";
                
                // 调用加密逻辑，获取加密结果
                var encryptResult = encryptionServices.Encrypt(fileInfo);

                // 若加密成功
                if (encryptResult.Encrypted)
                {
                    // 写入 {pipelineOutputDirectory}/bundleName.encrypt
                    string filePath = $"{pipelineOutputDirectory}/{bundleInfo.BundleName}.encrypt";
                    FileUtility.WriteAllBytes(filePath, encryptResult.EncryptedData);

                    // 为BuildBundleInfo 记录加密信息
                    bundleInfo.EncryptedFilePath = filePath;
                    bundleInfo.Encrypted = true;
                    BuildLogger.Log($"Bundle file encryption complete: {filePath}");
                }
                else
                {
                    bundleInfo.Encrypted = false;
                }

                // 进度条
                EditorTools.DisplayProgressBar("Encrypting bundle", ++progressValue, buildMapContext.Collection.Count);
            }
            EditorTools.ClearProgressBar();
        }
    }
}