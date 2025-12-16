# YooAsset 标签（Tag）系统说明

> 目录：`Assets/AOT&Framework/YooAsset/Editor/AssetBundleCollector/README_AssetTag.md`
>
> 适用版本：本项目内置的 YooAsset 2.3.16
>
> 约定：文中提到的“首包”，指 **Unity Player 构建出来的安装包中，位于 `StreamingAssets/yoo/<PackageName>` 的那部分 YooAsset 资源集合**。这些文件由 Unity 在打包时一并拷入最终安装包，玩家首次启动时即可直接从本地读取，无需远程下载。

本文只围绕本项目代码，说明 **标签 Tag 在 YooAsset 中从配置 → 收集 → 构建 → 运行时使用** 的完整链路，并精确标注关键脚本和函数，方便在修改或调试时快速定位。

---

## 1. 标签的定位：它是什么？

在 YooAsset 中，**Tag 本质是一个业务含义的字符串**，用于给资源和资源包做“内容分组 / DLC 标记 / 首包内容标记”。

它贯穿的主要数据结构有：

- 资源收集期：
  - `BuildAssetInfo.AssetTags`  
    文件：`Editor/AssetBundleBuilder/BuildAssetInfo.cs`
- 构建清单期：
  - `PackageAsset.AssetTags`  
    文件：`Runtime/ResourcePackage/PackageAsset.cs`
  - `PackageBundle.Tags`  
    文件：`Runtime/ResourcePackage/PackageBundle.cs`
- 运行时：
  - `PackageManifest` 上的 `AssetList`、`BundleList` + 各自的标签字段  
    文件：`Runtime/ResourcePackage/PackageManifest.cs`

从使用角度看，主要有三个场景：

1. **构建时**：控制“首包内置哪些 AssetBundle”（也就是哪些 Bundle 会被拷贝进 `StreamingAssets/yoo/<PackageName>`，随 Unity Player 一起打进安装包）。  
2. **运行时**：按标签**下载** / **解压** / **查询资源信息**，常用于 DLC / 章节包 / 扩展内容。  
3. **编辑器 UI**：展示一个包下可用的全部标签，方便配置首包拷贝参数等。

> 补充：Unity 在构建 Player 时，会把整个 `Assets/StreamingAssets` 目录原封不动地拷贝到目标平台的固定位置（例如 Standalone 的 `*_Data/StreamingAssets/`、Android APK 内的 `assets/`）。YooAsset 只是决定“往 `StreamingAssets/yoo/<PackageName>` 放哪些清单和 Bundle”，从而决定哪些资源属于首包本地资源，不需要远程下载。

---

## 2. 标签从哪里来：配置层（Collector 界面）

### 2.1 配置来源：Group 和 Collector

标签的原始配置写在 **分组 Group 和收集器 Collector 的 `AssetTags` 字段** 上（字符串，使用 `;` 分隔），定义文件大致在：

- `Editor/AssetBundleCollector/AssetBundleCollectorGroup.cs`
- `Editor/AssetBundleCollector/AssetBundleCollector.cs`

一个包裹 `AssetBundleCollectorPackage` 中包含若干 Group：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Editor/AssetBundleCollector/AssetBundleCollectorPackage.cs
[Serializable]
public class AssetBundleCollectorPackage
{
    public string PackageName = string.Empty;
    public List<AssetBundleCollectorGroup> Groups = new List<AssetBundleCollectorGroup>();

    /// <summary>
    /// 获取所有的资源标签
    /// </summary>
    public List<string> GetAllTags()
    {
        HashSet<string> result = new HashSet<string>();
        foreach (var group in Groups)
        {
            // Group 上的标签
            List<string> groupTags = EditorTools.StringToStringList(group.AssetTags, ';');
            foreach (var tag in groupTags)
            {
                if (result.Contains(tag) == false)
                    result.Add(tag);
            }

            // Collector 上的标签
            foreach (var collector in group.Collectors)
            {
                List<string> collectorTags = EditorTools.StringToStringList(collector.AssetTags, ';');
                foreach (var tag in collectorTags)
                {
                    if (result.Contains(tag) == false)
                        result.Add(tag);
                }
            }
        }
        return result.ToList();
    }
}
```

这个方法被上层用来**统计某个包裹下所有可能出现的标签**。

### 2.2 打开 Builder 窗口时如何拿到“全部标签”

`AssetBundleCollectorSettingData.GetPackageAllTags` 封装了对 `GetAllTags` 的调用：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Editor/AssetBundleCollector/AssetBundleCollectorSettingData.cs
public static string GetPackageAllTags(string packageName)
{
    var allTags = Setting.GetPackageAllTags(packageName);
    return string.Join(";", allTags);
}
```

`Setting.GetPackageAllTags` 内部会遍历 `AssetBundleCollectorPackage`，调用上面的 `GetAllTags()`。

**用途：**

- 在 Builder 窗口里，配置首包拷贝参数（`BuildinFileCopyParams`）时，可以弹出一个列表或提示，方便用户选择已有标签；
- 是 UI / 配置 层面对 Tag 的一个“总览”。

---

## 3. 收集阶段：标签如何落到 BuildAssetInfo

### 3.1 收集结果：`CollectAssetInfo`

收集器（`AssetBundleCollector`）和 Group 会把符合规则的资源整理为 `CollectAssetInfo` 列表：

- 入口：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Editor/AssetBundleCollector/AssetBundleCollectorPackage.cs
public List<CollectAssetInfo> GetCollectAssets(CollectCommand command)
{
    Dictionary<string, CollectAssetInfo> result = new Dictionary<string, CollectAssetInfo>(10000);

    // 收集打包资源
    foreach (var group in Groups)
    {
        var temper = group.GetAllCollectAssets(command);
        foreach (var collectAsset in temper)
        {
            if (result.ContainsKey(collectAsset.AssetInfo.AssetPath) == false)
                result.Add(collectAsset.AssetInfo.AssetPath, collectAsset);
            else
                throw new Exception($"The collecting asset file is existed : {collectAsset.AssetInfo.AssetPath}");
        }
    }

    // ...（地址去重检查省略）
    return result.Values.ToList();
}
```

`AssetBundleCollectorGroup.GetAllCollectAssets` 内部会根据 Group/Collector 的 `AssetTags`，把标签挂到每个主资源的 `CollectAssetInfo` 上（详见 `Editor/AssetBundleCollector/CollectAssetInfo.cs` 和 `AssetBundleCollectorGroup.cs`）。

### 3.2 转换到构建期结构：`BuildAssetInfo.AssetTags`

在构建管线中，`TaskGetBuildMap` 会把 `CollectAssetInfo` 转成 `BuildAssetInfo`（关键脚本：

- `Editor/AssetBundleBuilder/BuildPipeline/BaseTasks/TaskGetBuildMap.cs`
- `Editor/AssetBundleBuilder/BuildAssetInfo.cs`

`BuildAssetInfo` 中有：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Editor/AssetBundleBuilder/BuildAssetInfo.cs
public class BuildAssetInfo
{
    /// <summary>
    /// 资源的分类标签（收集阶段配置 + 规整之后的结果）
    /// </summary>
    public readonly List<string> AssetTags = new List<string>();

    public void AddAssetTags(List<string> tags)
    {
        if (_isAddAssetTags)
            throw new Exception("Should never get here !");
        _isAddAssetTags = true;

        foreach (var tag in tags)
        {
            if (AssetTags.Contains(tag) == false)
            {
                AssetTags.Add(tag);
            }
        }
    }
}
```

- 收集器中的标签（来自 Group / Collector）会在 `TaskGetBuildMap` 中统一调用 `AddAssetTags` 写入。
- `BuildAssetInfo` 是“构建期资源节点”，后续所有清单/依赖/首包逻辑都以它为基准。

**这个阶段的关键点：**

> **每个“主资源”在 Build 阶段拥有一份 `AssetTags`，它来源于收集配置，是后续所有 Tag 逻辑的根。**

---

## 4. 构建清单：标签如何进入 PackageManifest

构建管线中，生成清单的核心脚本是：

- `Editor/AssetBundleBuilder/BuildPipeline/BaseTasks/TaskCreateManifest.cs`
- SBP 管线对应任务：`Editor/AssetBundleBuilder/BuildPipeline/ScriptableBuildPipeline/BuildTasks/TaskCreateManifest_SBP.cs`

### 4.1 资源级：`PackageAsset.AssetTags`

`TaskCreateManifest.CreateManifestFile` 中调用 `CreatePackageAssetList`：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Editor/AssetBundleBuilder/BuildPipeline/BaseTasks/TaskCreateManifest.cs
private List<PackageAsset> CreatePackageAssetList(BuildMapContext buildMapContext)
{
    List<PackageAsset> result = new List<PackageAsset>(1000);
    foreach (var bundleInfo in buildMapContext.Collection)
    {
        var assetInfos = bundleInfo.GetAllManifestAssetInfos();
        foreach (var assetInfo in assetInfos)
        {
            PackageAsset packageAsset = new PackageAsset();
            packageAsset.Address = buildMapContext.Command.EnableAddressable ? assetInfo.Address : string.Empty;
            packageAsset.AssetPath = assetInfo.AssetInfo.AssetPath;
            packageAsset.AssetGUID = buildMapContext.Command.IncludeAssetGUID ? assetInfo.AssetInfo.AssetGUID : string.Empty;
            packageAsset.AssetTags = assetInfo.AssetTags.ToArray(); // ☆ 这里把 BuildAssetInfo 的标签复制到清单
            packageAsset.TempDataInEditor = assetInfo;
            result.Add(packageAsset);
        }
    }

    // ...排序省略
    return result;
}
```

运行时代码对应：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Runtime/ResourcePackage/PackageAsset.cs
[Serializable]
internal class PackageAsset
{
    public string[] AssetTags;

    public bool HasTag(string[] tags)
    {
        if (tags == null || tags.Length == 0)
            return false;
        if (AssetTags == null || AssetTags.Length == 0)
            return false;

        foreach (var tag in tags)
        {
            if (AssetTags.Contains(tag))
                return true;
        }
        return false;
    }
}
```

> 至此：**清单中的每个资源 (`PackageAsset`) 已经有了自己的标签数组。**

### 4.2 Bundle 级：`PackageBundle.Tags`（传播标签给依赖包）

同一个清单任务里，`ProcessBundleTags` 负责把“资源标签”映射到“Bundle 标签”：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Editor/AssetBundleBuilder/BuildPipeline/BaseTasks/TaskCreateManifest.cs
private void ProcessBundleTags(PackageManifest manifest)
{
    // 1. 清空所有 Bundle 的标签
    foreach (var packageBundle in manifest.BundleList)
    {
        packageBundle.Tags = Array.Empty<string>();
    }

    // 2. 先缓存：哪些 Bundle 拿到哪些标签
    foreach (var packageAsset in manifest.AssetList)
    {
        var assetTags = packageAsset.AssetTags;
        int bundleID = packageAsset.BundleID;

        // 主资源所在的主包
        CacheBundleTags(bundleID, assetTags);

        if (packageAsset.DependBundleIDs != null)
        {
            // 依赖到的所有 Bundle
            foreach (var dependBundleID in packageAsset.DependBundleIDs)
            {
                CacheBundleTags(dependBundleID, assetTags);
            }
        }
    }

    // 3. 把缓存写回 BundleList
    for (int index = 0; index < manifest.BundleList.Count; index++)
    {
        var packageBundle = manifest.BundleList[index];
        if (_cacheBundleTags.TryGetValue(index, out var value))
        {
            packageBundle.Tags = value.ToArray();
        }
        else
        {
            // 没有任何主资源引用的游离包（一般是被 SBP 剔重后留下的）
            string warning = BuildLogger.GetErrorMessage(ErrorCode.FoundStrayBundle,
                $"Found stray bundle ! Bundle ID : {index} Bundle name : {packageBundle.BundleName}");
            BuildLogger.Warning(warning);
        }
    }
}
```

运行时期对应的 Bundle 结构：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Runtime/ResourcePackage/PackageBundle.cs
[Serializable]
internal class PackageBundle
{
    /// <summary>资源包的分类标签</summary>
    public string[] Tags;

    public bool HasTag(string[] tags) { ... }
    public bool HasAnyTags() { ... }
}
```

**关键理解：**

- 某个主资源打了标签 `"Newbie"`：
  - 它所在的主包会有 `Tags` 包含 `"Newbie"`；
  - 它依赖到的所有 Bundle 也会有 `"Newbie"`；
- 这保证后续操作“按标签处理 Bundle”时能拿到完整依赖闭包，而不用再自己手动处理依赖图。

> 从概念上看，Tag 就是“资源/Bundle 的业务分类标签”。YooAsset 通过它来决定：
> - 哪些 Bundle 要进首包（被复制到 `StreamingAssets/yoo/<PackageName>`，随 Player 一起分发）；
> - 哪些 Bundle 留在远程，由运行时按标签增量下载或解压，常用于章节、DLC、皮肤等扩展内容。

---

## 5. 构建后：首包拷贝为什么要看标签？

首包拷贝任务基类：

- `Editor/AssetBundleBuilder/BuildPipeline/BaseTasks/TaskCopyBuildinFiles.cs`
- SBP 对应实现：`Editor/AssetBundleBuilder/BuildPipeline/ScriptableBuildPipeline/BuildTasks/TaskCopyBuildinFiles_SBP.cs`

核心逻辑：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Editor/AssetBundleBuilder/BuildPipeline/BaseTasks/TaskCopyBuildinFiles.cs
internal void CopyBuildinFilesToStreaming(BuildParametersContext buildParametersContext, PackageManifest manifest)
{
    EBuildinFileCopyOption copyOption = buildParametersContext.Parameters.BuildinFileCopyOption;
    string packageOutputDirectory = buildParametersContext.GetPackageOutputDirectory();
    string buildinRootDirectory = buildParametersContext.GetBuildinRootDirectory();
    string buildPackageName = buildParametersContext.Parameters.PackageName;
    string buildPackageVersion = buildParametersContext.Parameters.PackageVersion;

    // ... 先拷贝 manifest.bytes / .hash / .version

    // 拷贝文件列表（所有文件）
    if (copyOption == EBuildinFileCopyOption.ClearAndCopyAll || copyOption == EBuildinFileCopyOption.OnlyCopyAll)
    {
        foreach (var packageBundle in manifest.BundleList)
        {
            string sourcePath = $"{packageOutputDirectory}/{packageBundle.FileName}";
            string destPath = $"{buildinRootDirectory}/{packageBundle.FileName}";
            EditorTools.CopyFile(sourcePath, destPath, true);
        }
    }

    // 拷贝文件列表（带标签的文件）
    if (copyOption == EBuildinFileCopyOption.ClearAndCopyByTags || copyOption == EBuildinFileCopyOption.OnlyCopyByTags)
    {
        string[] tags = buildParametersContext.Parameters.BuildinFileCopyParams.Split(';');
        foreach (var packageBundle in manifest.BundleList)
        {
            if (packageBundle.HasTag(tags) == false)
                continue;

            string sourcePath = $"{packageOutputDirectory}/{packageBundle.FileName}";
            string destPath = $"{buildinRootDirectory}/{packageBundle.FileName}";
            EditorTools.CopyFile(sourcePath, destPath, true);
        }
    }

    AssetDatabase.Refresh();
    BuildLogger.Log($"Buildin files copy complete: {buildinRootDirectory}");
}
```

配套配置项：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Editor/AssetBundleBuilder/EBuildinFileCopyOption.cs
public enum EBuildinFileCopyOption
{
    None,
    ClearAndCopyAll,
    ClearAndCopyByTags,
    OnlyCopyAll,
    OnlyCopyByTags,
}

// 文件：Assets/AOT&Framework/YooAsset/Editor/AssetBundleBuilder/BuildParameters.cs
public EBuildinFileCopyOption BuildinFileCopyOption = EBuildinFileCopyOption.None;
public string BuildinFileCopyParams; // 形如 "TagA;TagB"
```

**为什么要看标签？**

- 运行时「内置文件系统」会从 `StreamingAssets/yoo/<PackageName>` 目录读取内置的清单和 Bundle，作为首包资源或离线资源；
- 我们通常希望：
  - 把“基础内容 / 首次必需内容”（例如：主菜单、新手关）放进首包；
  - 把“后续章节 / 体积较大内容”留给运行时按需下载；
- 构建期已经为 Bundle 填好了标签 `PackageBundle.Tags`，并且保证了**主资源标签会自动传播到依赖包**；

因此，只需要：

- 在 Collector 配置中给“新手关 + 其资源”打上 `Newbie`；
- 在 Builder 窗口中配置：`BuildinFileCopyOption = ClearAndCopyByTags`，`BuildinFileCopyParams = "Newbie"`；
- `TaskCopyBuildinFiles` 即可只把带 `Newbie` 标签的所有 Bundle（包含依赖）拷到 `StreamingAssets/yoo/<PackageName>`。

> 结合前面的约定：**这里只讨论的“首包拷贝”指的是往 Unity Player 安装包中的 `StreamingAssets/yoo/<PackageName>` 拷贝哪些 YooAsset 资源，而不是另一个单独的“资源包格式”。**

> **总结一句：首包拷贝按标签筛选，就是利用同一套 Tag 系统来控制“首包内置哪些内容”。**

---

## 6. 运行时的标签使用示例

运行时代码主要在：

- `Runtime/ResourcePackage/PackageManifest.cs`
- `Runtime/ResourcePackage/PlayMode/PlayModeImpl.cs`
- `Runtime/YooAssetsExtension.cs`

### 6.1 按标签获取资源列表（AssetInfo）

`PackageManifest` 支持按标签取资源：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Runtime/ResourcePackage/PackageManifest.cs
public AssetInfo[] GetAssetInfosByTags(string[] tags)
{
    List<AssetInfo> result = new List<AssetInfo>(AssetList.Count);
    foreach (var packageAsset in AssetList)
    {
        if (packageAsset.HasTag(tags))
        {
            AssetInfo assetInfo = new AssetInfo(PackageName, packageAsset, null);
            result.Add(assetInfo);
        }
    }
    return result.ToArray();
}
```

对业务层的封装：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Runtime/YooAssetsExtension.cs
public static AssetInfo[] GetAssetInfos(string tag)
{
    DebugCheckDefaultPackageValid();
    return _defaultPackage.GetAssetInfos(tag);
}

public static AssetInfo[] GetAssetInfos(string[] tags)
{
    DebugCheckDefaultPackageValid();
    return _defaultPackage.GetAssetInfos(tags);
}
```

**示例：在游戏代码中按标签取资源列表**

```csharp
// 假设你把新手引导所有 UI / 场景资源打上了 "NewbieUI" 标签
var newbieAssets = YooAssets.GetAssetInfos("NewbieUI");
foreach (var assetInfo in newbieAssets)
{
    UnityEngine.Debug.Log($"Newbie asset: {assetInfo.AssetPath}");
}
```

> 适用场景：做内容浏览器、资源统计、批量预加载（根据标签）等。

### 6.2 按标签创建下载器（下载远程 Bundle）

在 `PlayModeImpl` 内部：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Runtime/ResourcePackage/PlayMode/PlayModeImpl.cs
public List<BundleInfo> GetDownloadListByTags(PackageManifest manifest, string[] tags)
{
    if (manifest == null)
        return new List<BundleInfo>();

    List<BundleInfo> result = new List<BundleInfo>(1000);
    foreach (var packageBundle in manifest.BundleList)
    {
        var fileSystem = GetBelongFileSystem(packageBundle);
        if (fileSystem == null)
            continue;

        if (fileSystem.NeedDownload(packageBundle))
        {
            // 如果未带任何标记，则统一下载（视为基础内容）
            if (packageBundle.HasAnyTags() == false)
            {
                result.Add(new BundleInfo(fileSystem, packageBundle));
            }
            else
            {
                // 查询 DLC 资源：只有带指定 Tag 的才下载
                if (packageBundle.HasTag(tags))
                {
                    result.Add(new BundleInfo(fileSystem, packageBundle));
                }
            }
        }
    }
    return result;
}
```

对外封装：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Runtime/YooAssetsExtension.cs
public static ResourceDownloaderOperation CreateResourceDownloader(string tag, int downloadingMaxNumber, int failedTryAgain)
{
    DebugCheckDefaultPackageValid();
    return _defaultPackage.CreateResourceDownloader(new string[] { tag }, downloadingMaxNumber, failedTryAgain);
}

public static ResourceDownloaderOperation CreateResourceDownloader(string[] tags, int downloadingMaxNumber, int failedTryAgain)
{
    DebugCheckDefaultPackageValid();
    return _defaultPackage.CreateResourceDownloader(tags, downloadingMaxNumber, failedTryAgain);
}
```

**示例：按标签下载 DLC 内容**

```csharp
// 下载所有打上 "Chapter1" 标签的内容
var downloader = YooAssets.CreateResourceDownloader("Chapter1", downloadingMaxNumber: 5, failedTryAgain: 3);

if (downloader.TotalDownloadCount == 0)
{
    UnityEngine.Debug.Log("Chapter1 已经全部在本地，无需下载。");
}
else
{
    downloader.BeginDownload();
    // 在 Update 或协程里轮询 downloader.Status / Progress
}
```

### 6.3 按标签创建解压器（解压内置或缓存 Bundle）

在 `PlayModeImpl` 中：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Runtime/ResourcePackage/PlayMode/PlayModeImpl.cs
public List<BundleInfo> GetUnpackListByTags(PackageManifest manifest, string[] tags)
{
    if (manifest == null)
        return new List<BundleInfo>();

    List<BundleInfo> result = new List<BundleInfo>(1000);
    foreach (var packageBundle in manifest.BundleList)
    {
        var fileSystem = GetBelongFileSystem(packageBundle);
        if (fileSystem == null)
            continue;

        if (fileSystem.NeedUnpack(packageBundle))
        {
            if (packageBundle.HasTag(tags))
            {
                var bundleInfo = new BundleInfo(fileSystem, packageBundle);
                result.Add(bundleInfo);
            }
        }
    }
    return result;
}
```

封装 API：

```csharp
// 文件：Assets/AOT&Framework/YooAsset/Runtime/YooAssetsExtension.cs
public static ResourceUnpackerOperation CreateResourceUnpacker(string tag, int unpackingMaxNumber, int failedTryAgain)
{
    DebugCheckDefaultPackageValid();
    return _defaultPackage.CreateResourceUnpacker(tag, unpackingMaxNumber, failedTryAgain);
}

public static ResourceUnpackerOperation CreateResourceUnpacker(string[] tags, int unpackingMaxNumber, int failedTryAgain)
{
    DebugCheckDefaultPackageValid();
    return _defaultPackage.CreateResourceUnpacker(tags, unpackingMaxNumber, failedTryAgain);
}
```

**示例：首包安装后解压某些标签内容**

```csharp
// 解压所有标记为 "Newbie" 的内置 Bundle
var unpacker = YooAssets.CreateResourceUnpacker("Newbie", unpackingMaxNumber: 4, failedTryAgain: 2);

if (unpacker.TotalUnpackCount > 0)
{
    unpacker.BeginUnpack();
}
```

---

## 7. 小结：如何在项目中合理使用 Tag？

1. **在收集器配置中确定你的“内容分层”策略**：
   - 例如：`Base`（基础必带）、`Newbie`（新手引导）、`Chapter1`、`Chapter2`、`Hero_A`、`Skin_X` 等；
   - 将这些 Tag 配置在 Group 或 Collector 的 `AssetTags` 字段中。

2. **利用首包拷贝的 Tag 策选**：
   - `BuildinFileCopyOption = ClearAndCopyByTags / OnlyCopyByTags`；
   - `BuildinFileCopyParams = "Base;Newbie"`；
   - 确保安装包中只内置“基础 + 新手”相关资源，其余内容留给远程下载或后续解压。

3. **在运行时用标签驱动下载 / 解压 / 查询**：
   - `YooAssets.CreateResourceDownloader("Chapter1", ...)` 按章节下载；
   - `YooAssets.CreateResourceUnpacker("Newbie", ...)` 解压新手内容；
   - `YooAssets.GetAssetInfos("Skin_X")` 检索特定皮肤资源列表。

4. **记住依赖传播规则**：
   - 标签打在“主资源”上，在构建清单时会自动传播给其主包 + 依赖包；
   - 因此，一般只需关注给“逻辑入口资源”（场景、预制体等）打标，而不用手动处理每个依赖文件。

5. **再强调一次“首包”和 YooAsset 资源包的关系**：
   - 首包指的是 Unity Player 安装包整体，其中的一部分内容由 YooAsset 管理，位于 `StreamingAssets/yoo/<PackageName>`；
   - YooAsset 的构建输出目录（`Bundles/<BuildTarget>/<PackageName>/<PackageVersion>/`）只是中间/最终资源产物所在位置，哪些产物进入首包完全由 `TaskCopyBuildinFiles` + Tag 决定；
   - 也就是说：**YooAsset 负责产出资源包，并通过 Tag+首包拷贝规则决定这批资源包是“随安装包发”还是“运行时按需下”。**

---

如果以后你在修改 **收集规则 / 构建管线 / 首包策略 / DLC 策略** 时忘记 Tag 的走向，可以按以下顺序快速回顾：

1. 配置：`AssetBundleCollectorPackage` / `AssetBundleCollectorGroup` / `AssetBundleCollector`（`AssetTags` 字段）。
2. 收集：`CollectAssetInfo` → `BuildAssetInfo.AssetTags`。  
3. 清单：`TaskCreateManifest` 写入 `PackageAsset.AssetTags`，并在 `ProcessBundleTags` 中生成 `PackageBundle.Tags`。  
4. 构建后：`TaskCopyBuildinFiles` 按标签拷贝首包内置 Bundle（进入 Player 的 `StreamingAssets/yoo/<PackageName>`）。  
5. 运行时：
   - `PackageManifest.GetAssetInfosByTags`
   - `PlayModeImpl.GetDownloadListByTags / GetUnpackListByTags`
   - `YooAssets` 上的一系列 `CreateResourceDownloader` / `CreateResourceUnpacker` / `GetAssetInfos`。
