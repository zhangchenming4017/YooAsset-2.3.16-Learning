#if UNITY_2019_4_OR_NEWER
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace YooAsset.Editor
{
    [BuildPipelineAttribute(nameof(EBuildPipeline.ScriptableBuildPipeline))]
    internal class ScriptableBuildPipelineViewer : BuildPipelineViewerBase
    {
        protected TemplateContainer Root;
        protected TextField _buildOutputField;
        protected TextField _buildVersionField;
        protected PopupField<Type> _encryptionServicesField;
        protected PopupField<Type> _manifestProcessServicesField;
        protected PopupField<Type> _manifestRestoreServicesField;
        protected EnumField _compressionField;
        protected EnumField _outputNameStyleField;
        protected EnumField _copyBuildinFileOptionField;
        protected TextField _copyBuildinFileTagsField;
        protected Toggle _clearBuildCacheToggle;
        protected Toggle _useAssetDependencyDBToggle;

        public override void CreateView(VisualElement parent)
        {
            // 加载布局文件
            var visualAsset = UxmlLoader.LoadWindowUXML<ScriptableBuildPipelineViewer>();
            if (visualAsset == null)
                return;

            Root = visualAsset.CloneTree();
            Root.style.flexGrow = 1f;
            parent.Add(Root);

            // 输出目录
            _buildOutputField = Root.Q<TextField>("BuildOutput");
            SetBuildOutputField(_buildOutputField);

            // 构建版本
            _buildVersionField = Root.Q<TextField>("BuildVersion");
            SetBuildVersionField(_buildVersionField);

            // 加密方法
            var popupContainer = Root.Q("PopupContainer");
            _encryptionServicesField = CreateEncryptionServicesField(popupContainer);
            _manifestProcessServicesField = CreateManifestProcessServicesField(popupContainer);
            _manifestRestoreServicesField = CreateManifestRestoreServicesField(popupContainer);

            // 压缩方式选项
            _compressionField = Root.Q<EnumField>("Compression");
            SetCompressionField(_compressionField);

            // 输出文件名称样式
            _outputNameStyleField = Root.Q<EnumField>("FileNameStyle");
            SetOutputNameStyleField(_outputNameStyleField);

            // 首包文件拷贝参数
            _copyBuildinFileTagsField = Root.Q<TextField>("CopyBuildinFileParam");
            SetCopyBuildinFileTagsField(_copyBuildinFileTagsField);
            SetCopyBuildinFileTagsVisible(_copyBuildinFileTagsField);

            // 首包文件拷贝选项
            _copyBuildinFileOptionField = Root.Q<EnumField>("CopyBuildinFileOption");
            SetCopyBuildinFileOptionField(_copyBuildinFileOptionField, _copyBuildinFileTagsField);

            // 清理构建缓存
            _clearBuildCacheToggle = Root.Q<Toggle>("ClearBuildCache");
            SetClearBuildCacheToggle(_clearBuildCacheToggle);

            // 使用资源依赖数据库
            _useAssetDependencyDBToggle = Root.Q<Toggle>("UseAssetDependency");
            SetUseAssetDependencyDBToggle(_useAssetDependencyDBToggle);

            // 构建按钮
            var buildButton = Root.Q<Button>("Build");
            buildButton.clicked += BuildButton_clicked;
        }
        private void BuildButton_clicked()
        {
            if (EditorUtility.DisplayDialog("提示", $"开始构建资源包[{PackageName}]！", "Yes", "No"))
            {
                EditorTools.ClearUnityConsole();
                //? 为什么用 delayCall 而不是直接调用
                //!? EditorApplication.delayCall 是一个一次性的主线程回调队列：把委托排到“下一帧（下一次 Editor 更新循环）”执行。
                //!? 确保 UI 有机会刷新：刚弹过的对话框关闭、控制台清空等需要一帧重绘；延后到下一次更新后再执行构建，用户能看到控制台已清空、按钮状态已更新。
                //!? 这里不用直接调用 ExecuteBuild，是为了让当前 UI 事件完整结束、界面与对话框先刷新/关闭，再开始耗时且可能引发布局/导入/编译的构建流程，避免重入与时序问题。
                //!? 避免构建期间的域重载/资源导入与当前事件交叉：ExecuteBuild 里会写文件、触发导入，甚至可能引发脚本重编译与域重载；把它放到下一帧更安全。
                //!? 用途：避开当前 UI 事件的重入与时序问题，让 UI/对话框先收尾，再启动重型构建逻辑。
                EditorApplication.delayCall += ExecuteBuild;
            }
            else
            {
                Debug.LogWarning("[Build] 打包已经取消");
            }
        }

        /// <summary>
        /// 执行构建
        /// </summary>
        protected virtual void ExecuteBuild()
        {
            var fileNameStyle = AssetBundleBuilderSetting.GetPackageFileNameStyle(PackageName, PipelineName);
            var buildinFileCopyOption = AssetBundleBuilderSetting.GetPackageBuildinFileCopyOption(PackageName, PipelineName);
            var buildinFileCopyParams = AssetBundleBuilderSetting.GetPackageBuildinFileCopyParams(PackageName, PipelineName);
            var compressOption = AssetBundleBuilderSetting.GetPackageCompressOption(PackageName, PipelineName);
            var clearBuildCache = AssetBundleBuilderSetting.GetPackageClearBuildCache(PackageName, PipelineName);
            var useAssetDependencyDB = AssetBundleBuilderSetting.GetPackageUseAssetDependencyDB(PackageName, PipelineName);

            ScriptableBuildParameters buildParameters = new ScriptableBuildParameters();
            buildParameters.BuildOutputRoot = AssetBundleBuilderHelper.GetDefaultBuildOutputRoot();
            buildParameters.BuildinFileRoot = AssetBundleBuilderHelper.GetStreamingAssetsRoot();
            buildParameters.BuildPipeline = PipelineName.ToString();             // ScriptableBuildPipeline
            buildParameters.BuildBundleType = (int)EBuildBundleType.AssetBundle;
            buildParameters.BuildTarget = BuildTarget;
            buildParameters.PackageName = PackageName;
            buildParameters.PackageVersion = _buildVersionField.value;           // DateTime.Now.ToString("yyyy-MM-dd") + "-" + DateTime.Now.Hour * 60 + DateTime.Now.Minute
            buildParameters.EnableSharePackRule = true;
            buildParameters.VerifyBuildingResult = true;
            buildParameters.FileNameStyle = fileNameStyle;
            buildParameters.BuildinFileCopyOption = buildinFileCopyOption;
            buildParameters.BuildinFileCopyParams = buildinFileCopyParams;
            buildParameters.CompressOption = compressOption;
            buildParameters.ClearBuildCacheFiles = clearBuildCache;
            buildParameters.UseAssetDependencyDB = useAssetDependencyDB;
            buildParameters.EncryptionServices = CreateEncryptionServicesInstance();
            buildParameters.ManifestProcessServices = CreateManifestProcessServicesInstance();
            buildParameters.ManifestRestoreServices = CreateManifestRestoreServicesInstance();
            buildParameters.BuiltinShadersBundleName = GetBuiltinShaderBundleName();

            ScriptableBuildPipeline pipeline = new ScriptableBuildPipeline();
            var buildResult = pipeline.Run(buildParameters, true);
            if (buildResult.Success)
                EditorUtility.RevealInFinder(buildResult.OutputPackageDirectory);
        }

        /// <summary>
        /// 内置着色器资源包名称
        /// 注意：和自动收集的着色器资源包名保持一致！
        /// </summary>
        protected string GetBuiltinShaderBundleName()
        {
            var uniqueBundleName = AssetBundleCollectorSettingData.Setting.UniqueBundleName;
            var packRuleResult = DefaultPackRule.CreateShadersPackRuleResult();
            return packRuleResult.GetBundleName(PackageName, uniqueBundleName);
        }

        /// <summary>
        /// Mono脚本的资源包名称
        /// </summary>
        protected string GetMonoScriptsBundleName()
        {
            var uniqueBundleName = AssetBundleCollectorSettingData.Setting.UniqueBundleName;
            var packRuleResult = DefaultPackRule.CreateMonosPackRuleResult();
            return packRuleResult.GetBundleName(PackageName, uniqueBundleName);
        }
    }
}
#endif