using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;

namespace YooAsset.Editor
{
    public class TaskCreateCatalog
    {
        /// <summary>
        /// 生成内置资源记录文件
        /// </summary>
        internal void CreateCatalogFile(BuildParametersContext buildParametersContext)
        {
            // 获取内置文件的目录StreamingAssets/yoo/包名
            string buildinRootDirectory = buildParametersContext.GetBuildinRootDirectory();
            // 获取包名
            string buildPackageName = buildParametersContext.Parameters.PackageName;
            var manifestServices = buildParametersContext.Parameters.ManifestRestoreServices;
            CatalogTools.CreateCatalogFile(manifestServices, buildPackageName, buildinRootDirectory);

            // 刷新目录
            AssetDatabase.Refresh();
        }
    }
}