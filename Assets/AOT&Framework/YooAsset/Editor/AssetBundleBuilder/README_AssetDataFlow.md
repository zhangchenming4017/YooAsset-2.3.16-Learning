# YooAsset AB 构建流程中的“资源信息类”总览

> 目标：梳理 AB 构建和运行时中所有和“资源/资源包信息”相关的核心类，它们在什么阶段出现、保存什么信息、谁创建、谁使用。
>
> 范围：以 ScriptableBuildPipeline（SBP）为主线，兼顾运行时。

---

## 1. 总体视角：几类“资源信息模型”

YooAsset 整体可以分成两大阶段：

- **编辑器构建期（Editor）**：围绕“如何收集资源、如何分包、如何构建”设计的数据结构。
- **运行时（Runtime）**：围绕“如何定位 / 下载 / 加载资源”设计的数据结构。

对应地，资源相关的核心类型可以按阶段归类：

### 1.1 编辑器构建期

- `YooAsset.Editor.AssetInfo`（Editor 版）
- `CollectAssetInfo`（收集阶段）
- `BuildAssetInfo`
- `BuildBundleInfo`
- `BuildMapContext`（聚合上述信息）
- `ReportAssetInfo` / `ReportBundleInfo` / `ReportIndependAsset`（构建报告中的视图）

### 1.2 运行时 & 清单

- `YooAsset.PackageAsset`
- `YooAsset.PackageBundle`
- `YooAsset.PackageManifest`
- `YooAsset.AssetInfo`（Runtime 版）

下面按“时间顺序 + 结构角色”来展开，每个类型都回答 4 个问题：

1. **保存了什么信息？**
2. **在哪里被创建？（哪一步 / 哪个 Task）**
3. **有什么用？（在这一步为什么需要它）**
4. **后面在哪里被使用？**

---

## 2. Editor 阶段：最早出现的资源信息

### 2.1 `YooAsset.Editor.AssetInfo`（Editor 版资源元数据）

**文件**：`Editor/Common/AssetInfo.cs`

**1）保存的信息**

- 资源在工程里的静态信息：
  - `AssetPath`：项目中的路径（如 `Assets/Textures/xxx.png`）。
  - `AssetGUID`：Unity GUID。
  - `AssetType`：主资源类型（`Texture2D`、`GameObject`、`SceneAsset` 等）。
  - `FileExtension`：后缀名（如 `.png`、`.prefab`）。

**2）在哪里创建**

- 收集阶段由各种收集器创建：
  - `AssetBundleCollector` 系列（`AssetBundleCollectorSettingData` 等）在 `BeginCollect` 过程中，遍历配置好的收集路径，为每个实际资源构造 `YooAsset.Editor.AssetInfo`。 

**3）用途**

- 这是**所有构建期资源信息的“基础单元”**：
  - 负责“扫描工程资源”的那一层。
  - 后续的 `CollectAssetInfo`、`BuildAssetInfo`、`BuildBundleInfo` 都会包含或引用它。

**4）被谁使用**

- `CollectAssetInfo` 持有 `AssetInfo`，代表“某条收集器规则收集到的一个资源”。
- `BuildAssetInfo` 进一步持有 `AssetInfo`，并在此之上增加 BundleName、Address、依赖信息等。

---

### 2.2 `CollectAssetInfo`（收集阶段的“主/静态/依赖资源条目”）

**文件**：`Editor/AssetBundleCollector/CollectAssetInfo.cs`

**1）保存的信息（典型字段）**

- `AssetInfo AssetInfo`：上面的 Editor 版资源信息。
- `ECollectorType CollectorType`：收集器类型（Main / Static / Depend）。
- `string BundleName`：收集规则决定的初始包名。
- `string Address`：可寻址地址。
- `List<string> AssetTags`：资源标签（仅对 Main 有效，后面会清理掉其它类型上的标签）。
- `List<AssetInfo> DependAssets`：**路径级别**的依赖资源列表（`UnityEditor.AssetDatabase.GetDependencies` 的结果封装）。

**2）在哪里创建**

- 在 `AssetBundleCollectorSettingData.Setting.BeginCollect(...)` 内，由各个 `Collector` 根据配置（过滤规则 / PackRule / AddressRule / Tags）生成。
- `TaskGetBuildMap.CreateBuildMap` 的开头：

```csharp
var collectResult = AssetBundleCollectorSettingData.Setting.BeginCollect(packageName, simulateBuild, useAssetDependencyDB);
List<CollectAssetInfo> allCollectAssets = collectResult.CollectAssets;
```

**3）用途**

- 表达“**规则层的结果**”：收集规则眼中每个资源应该打进哪个包、给什么地址、标签是什么、依赖关系（路径粒度）。

**4）后续使用**

- 由 `TaskGetBuildMap.CreateBuildMap` 转换成 `BuildAssetInfo`：
  - 步骤 3：为每个 `CollectAssetInfo` 创建 `BuildAssetInfo`。
  - 步骤 4、5：用 `CollectAssetInfo.DependAssets` 构建强类型依赖图 `BuildAssetInfo.AllDependAssetInfos`。

---

## 3. Editor 阶段：BuildMap 层的资源/包模型

### 3.1 `BuildAssetInfo`（构建期资源节点）

**文件**：`Editor/AssetBundleBuilder/BuildAssetInfo.cs`

**1）保存的信息**

在 `CollectAssetInfo.AssetInfo` 的基础上增强：

- 构建维度：
  - `ECollectorType CollectorType`
  - `string BundleName`：经过共享包决策等调整后的最终归属 Bundle。
  - `string Address`
  - `List<string> AssetTags`
- 依赖/引用关系：
  - `List<BuildAssetInfo> AllDependAssetInfos`：**资源级强类型依赖图**。
  - `HashSet<string> _referenceBundleNames`（私有）：“哪些 bundle 引用了我”，用于共享包分析。

**2）在哪里创建**

- 在 `TaskGetBuildMap.CreateBuildMap`：
  - 步骤 3：

```csharp
var buildAssetInfo = new BuildAssetInfo(collectAssetInfo.CollectorType,
    collectAssetInfo.BundleName,
    collectAssetInfo.Address,
    collectAssetInfo.AssetInfo);
buildAssetInfo.AddAssetTags(collectAssetInfo.AssetTags);
allBuildAssetInfos.Add(collectAssetInfo.AssetInfo.AssetPath, buildAssetInfo);
```

  - 步骤 4：对 `CollectAssetInfo.DependAssets` 中“仅依赖出现”的资源构造 `new BuildAssetInfo(dependAsset)`（CollectorType = None，初始没有 BundleName）。
  - 步骤 5：`SetDependAssetInfos` 建立 `AllDependAssetInfos`。

**3）用途**

- 构建期的核心“资源节点”：
  - 驱动共享包决策（引用计数 `GetReferenceBundleCount`）。
  - 提供资源级依赖图（后面推导资产级依赖包 `PackageAsset.DependBundleIDs`）。
  - 记录主资源的标签信息（后面传染给包标签）。

**4）后续使用**

- 被 `BuildBundleInfo` 聚合：`BuildMapContext.PackAsset(BuildAssetInfo)`。
- 被 `TaskCreateManifest.CreatePackageAssetList` 用来生成清单中的 `PackageAsset`。
- 被 `TaskCreateManifest.ProcessPacakgeAsset` 中的 `GetAssetDependBundleIDs` 读取，用来计算资产级依赖包 ID。

---

### 3.2 `BuildBundleInfo`（构建期包节点）

**文件**：`Editor/AssetBundleBuilder/BuildBundleInfo.cs`

**1）保存的信息**

- 包含关系：
  - `List<BuildAssetInfo> AllPackAssets`：这个包中的所有显式打包资源。
- 构建结果元数据：
  - `PackageUnityHash` / `PackageUnityCRC`
  - `PackageFileHash` / `PackageFileCRC` / `PackageFileSize`
  - `Encrypted`、`BuildOutputFilePath`、`EncryptedFilePath`、`PackageSourceFilePath`、`PackageDestFilePath`

**2）在哪里创建**

- 在 `BuildMapContext.PackAsset`：
  - `TaskGetBuildMap.CreateBuildMap` 步骤 10 中调用：

```csharp
foreach (var assetInfo in allPackAssets)
{
    context.PackAsset(assetInfo); // 内部以 BundleName 为 key 聚成 BuildBundleInfo
}
```

- 每个不同的 `BundleName` 会对应一个新的 `BuildBundleInfo`。

**3）用途**

- 对 Unity 构建管线而言：
  - `CreatePipelineBuild()` → 生成 `AssetBundleBuild`，告诉 Unity “这个包里要打哪些资源”。
- 对 YooAsset 构建后处理而言：
  - `TaskUpdateBundleInfo` 给其填充 Unity Hash/CRC、最终 hash/CRC/size、源/目标路径、加密标志等。
  - `CreatePackageBundle()` 再“压缩”为清单中的 `PackageBundle`。

**4）后续使用**

- SBP：`TaskBuilding_SBP` 用 `buildMapContext.GetPipelineBuilds()`（内部依赖每个 `BuildBundleInfo.CreatePipelineBuild`）作为构建输入。
- Manifest：`TaskCreateManifest.CreatePackageBundleList` 遍历所有 `BuildBundleInfo` 创建 `PackageBundle`。
- Report：`TaskCreateReport_*` 读取 `AllPackAssets` + 各种 hash/size，生成构建报告。

---

### 3.3 `BuildMapContext`（构建期资源/包的聚合）

**文件**：`Editor/AssetBundleBuilder/BuildMapContext.cs`

**1）保存的信息**

- `_bundleInfoDic : Dictionary<string, BuildBundleInfo>`
  - 所有包及其包含的 `BuildAssetInfo`、构建结果元数据。
- `SpriteAtlasAssetList : List<BuildAssetInfo>`
  - 所有图集资源，用于补充图集依赖。
- `IndependAssets : List<ReportIndependAsset>`
  - 被 Depend 收集到但零引用而被剔除的资源，供报告使用。
- `AssetFileCount`：参与构建的资源总数。
- `CollectCommand Command`：收集命令（是否启用 addressable、是否写 GUID 等）。

**2）在哪里创建**

- `TaskGetBuildMap_SBP.Run` 调用 `CreateBuildMap` 并 `context.SetContextObject(buildMapContext)`。

**3）用途**

- 构建期的全局“计划视图”：
  - 向 Unity 提供 `AssetBundleBuild[]` 输入。
  - 为后续的 Manifest / Report / Hash 冲突检测提供基础数据。
  - 驱动清单中 `AssetList` / `BundleList` 的构建。

**4）后续使用**

- `TaskBuilding_SBP`：生成 SBP 构建输入。
- `TaskVerifyBuildResult_SBP`：对比“计划包名集合”与实际 SBP 输出的 `BundleInfos.Keys`。
- `TaskUpdateBundleInfo_SBP`：对每个 `BuildBundleInfo` 填写最终 hash/CRC/size/路径。
- `TaskCreateManifest_SBP`：创建 `PackageAsset` / `PackageBundle`，以及图集等特殊依赖修正。
- `TaskCreateReport_SBP`：生产构建报告。

---

## 4. Editor 阶段：报告视图（简单提到）

- `ReportAssetInfo` / `ReportBundleInfo` / `ReportIndependAsset`：
  - 文件：`Editor/AssetBundleReporter/Report*.cs`
  - 数据来源：
    - `BuildMapContext.Collection`（`BuildBundleInfo` + `BuildAssetInfo`）。
    - `BuildMapContext.IndependAssets`。
    - `PackageManifest`（清单中的最终信息）。
  - 用途：
    - 提供给 Reporter 窗口显示构建统计、单资源归属、包大小贡献等。
  - 运行时不会用到，因此这里不展开细节。

---

## 5. Runtime 阶段：清单与运行时视图

### 5.1 `PackageAsset`（清单中的资源项）

**文件**：`Runtime/ResourcePackage/PackageAsset.cs`

**1）保存的信息**

- `Address`：可寻址地址。
- `AssetPath`：项目内资源路径（构建时写死）。
- `AssetGUID`：可选，取决于收集器设置。
- `AssetTags`：资源标签数组。
- `BundleID`：所属主包的索引（在 `PackageManifest.BundleList` 中的下标）。
- `DependBundleIDs`：依赖的包 ID 列表（资产级依赖 => 包级 ID 集）。
- `TempDataInEditor`：仅编辑器，用于在生成清单时暂存 `BuildAssetInfo`。

**2）在哪里创建**

- `TaskCreateManifest.CreatePackageAssetList`：从 `BuildBundleInfo.GetAllManifestAssetInfos()`（`BuildAssetInfo[]`）转换而来。
- 随后 `ProcessPacakgeAsset` 写入 `BundleID` 与 `DependBundleIDs`：
  - `BundleID`：通过 `_cachedBundleIndexIDs` 将 `BuildAssetInfo.BundleName` 转为包索引。
  - `DependBundleIDs`：
    - `GetAssetDependBundleIDs(mainAssetInfo)` 遍历 `BuildAssetInfo.AllDependAssetInfos`，
    - 筛出“有 BundleName 的依赖资源”，映射为依赖包 ID 集。

**3）用途**

- 清单中的资产视图：描述“这个主资源属于哪个包、它还依赖哪些包、它有哪些标签”。

**4）运行时如何使用**

- `PackageManifest.ConvertLocationToAssetInfo` / `ConvertAssetGUIDToAssetInfo` 通过各种映射找到对应的 `PackageAsset`：

```csharp
if (TryGetPackageAsset(assetPath, out PackageAsset packageAsset))
{
    AssetInfo assetInfo = new AssetInfo(PackageName, packageAsset, assetType);
    return assetInfo;
}
```

- `PackageManifest.GetAssetAllDependencies`：将 `DependBundleIDs` 翻译成 `PackageBundle` 依赖列表，供加载器预加载：

```csharp
public List<PackageBundle> GetAssetAllDependencies(PackageAsset packageAsset)
{
    List<PackageBundle> result = new(packageAsset.DependBundleIDs.Length);
    foreach (var dependID in packageAsset.DependBundleIDs)
        result.Add(GetMainPackageBundle(dependID));
    return result;
}
```

---

### 5.2 `PackageBundle`（清单中的包项）

**文件**：`Runtime/ResourcePackage/PackageBundle.cs`

**1）保存的信息**

- 基础属性：
  - `BundleName`：逻辑包名（与构建时的 BundleName 一致）。
  - `UnityCRC`：Unity 的 CRC，用于底层加载校验。
  - `FileHash` / `FileCRC` / `FileSize`：**最终发布文件**的哈希/CRC/大小（可能已加密）。
  - `Encrypted`：文件是否加密。
  - `Tags`：包标签（来自资源标签聚合）。
- 依赖关系：
  - `DependBundleIDs`：bundle 级依赖（来自 SBP / Builtin 构建结果）。
  - `ReferenceBundleIDs`：谁引用了这个包（仅运行时调试/统计用）。
- 文件名派生：
  - `BundleGUID => FileHash`。
  - `FileExtension`、`FileName` 通过 `ManifestTools` 与 `OutputNameStyle` 决定远程文件名。

**2）在哪里创建**

- `TaskCreateManifest.CreatePackageBundleList`：遍历所有 `BuildBundleInfo`，调用 `CreatePackageBundle()`。
- 随后：
  - `ProcessBundleDepends` 通过 `GetBundleDepends(context, bundleName)`（在 SBP 中读取 `IBundleBuildResults.BundleInfos[bundleName].Dependencies`）填充 `DependBundleIDs`。
  - `ProcessBundleTags` 根据 `PackageAsset.AssetTags` 把标签传播到 `PackageBundle.Tags`。
  - `ProcessBuiltinBundleDependency` 补充内置 Shader 包 / MonoScripts 包 / 图集相关的额外依赖和标签。
  - `PackageBundle.InitBundle(manifest)` 在运行时构造 `FileName` / `FileExtension`。

**3）用途**

- 清单中的包视图：描述“这个包叫什么、对应哪个物理文件、它加密了吗、依赖谁、谁引用了它、它的标签是什么”。

**4）运行时如何使用**

- `PackageManifest.GetMainPackageBundle(int bundleID)`：通过 ID 取主包。
- `PackageManifest.GetBundleAllDependencies(PackageBundle packageBundle)`：获取包的依赖包列表。
- 文件系统：通过 `BundleGUID`、`FileName`、`FileExtension` 等构造下载 URL 或本地路径，并用 `UnityCRC` / `FileCRC` 进行校验。

---

### 5.3 `PackageManifest`（清单根对象）

**文件**：`Runtime/ResourcePackage/PackageManifest.cs`

**1）保存的信息**

- 包的元数据（FileVersion、BuildPipeline、PackageName/Version/Note 等）。
- `List<PackageAsset> AssetList`
- `List<PackageBundle> BundleList`
- 各种映射字典（运行时填充，不序列化）：
  - `AssetDic`、`AssetPathMapping1`（location→AssetPath）、`AssetPathMapping2`（GUID→AssetPath）、`BundleDic1/2/3` 等。

**2）在哪里创建**

- `TaskCreateManifest.CreateManifestFile`：
  - 先用 `BuildMapContext` 和 `BuildParameters` 填充一个 `PackageManifest` 实例；
  - 写出 `.json` / `.bytes` / `.hash` / `.version`；
  - 再读取 `.bytes` 并使用 `ManifestRestoreServices` 反序列化，封装进 `ManifestContext`。

**3）用途**

- 构建阶段：作为清单写入磁盘的根对象。
- 运行时：
  - 在各个 `FileSystem` 中被加载，作为包的“配置中心”。
  - 负责地址解析、资产和包查找、依赖推导等。

**4）运行时使用示例**

- `ResourcePackage.InitializeAsync` 里加载清单，随后所有加载/下载接口都通过 `PackageManifest` 查询资源与包信息。

---

### 5.4 Runtime 版 `AssetInfo`（运行期资源视图）

**文件**：`Runtime/ResourcePackage/AssetInfo.cs`

**1）保存的信息**

- `PackageName`：所属包名。
- `AssetType`：期望加载的类型。
- `Error`：如果定位失败的错误信息。
- 引用底层：
  - `PackageAsset _packageAsset`：清单里的资源项。
- 便利属性：
  - `IsInvalid`：是否定位失败。
  - `Address`：透传 `_packageAsset.Address`。
  - `AssetPath`：透传 `_packageAsset.AssetPath`。
  - 内部 `GUID`：用于 Provider 唯一标识。

**2）在哪里创建**

- 运行时通过 `PackageManifest` 的帮助方法：

```csharp
public AssetInfo ConvertLocationToAssetInfo(string location, System.Type assetType)
{
    string assetPath = ConvertLocationToAssetInfoMapping(location);
    if (TryGetPackageAsset(assetPath, out PackageAsset packageAsset))
        return new AssetInfo(PackageName, packageAsset, assetType);
    else
        return new AssetInfo(PackageName, errorString);
}
```

**3）用途**

- API 层向上暴露的“资源句柄描述”：
  - `YooAssets.LoadAssetAsync(location, type)` 等接口都返回/依赖 `AssetInfo`。
- 对下：
  - Loader / Provider 据此知道“从哪个包加载哪个路径的资源”。

**4）后续使用**

- Loader 根据 `AssetInfo`：
  - 查出所属包、依赖包；
  - 决定使用哪个 `FileSystem` / 下载器；
  - 决定使用 AssetBundle.LoadFromFile / LoadAsset / LoadScene / 还是读 RawFile。

---

## 6. 一图记忆：从 Editor 到 Runtime 的“类链路”

可以用下面的链路来记每一层的职责（从左到右是时间顺序）：

```text
[Editor.AssetInfo]
    ↓ (BeginCollect)
[CollectAssetInfo]
    ↓ (TaskGetBuildMap)
[BuildAssetInfo] ──→ (聚合) ──→ [BuildBundleInfo]
    ↓ 依赖图                        ↓ 填写构建结果
[BuildMapContext] ──────→ (TaskCreateManifest)
   ├─ 生成 PackageAsset（主资源列表）
   └─ 生成 PackageBundle（包列表 + hash/CRC/size/encrypted）
                                  ↓ (写 .bytes/.json/.hash/.version)
                            [PackageManifest]
                                  ↓ (Runtime 反序列化)
                     ┌────────────┴────────────┐
                     ↓                         ↓
               [PackageAsset]             [PackageBundle]
                     ↓                         ↓
                 [AssetInfo]           FileSystem / Loader
```

---

## 7. 结合你在 `ScriptableBuildPipeline` 里的注释

你在 `ScriptableBuildPipeline.GetDefaultBuildPipeline` 末尾已经写了“不同阶段资源/资源包的情况”的注释，大致分为：

1. 收集阶段（`CollectAssetInfo`，`AssetBundleCollectorSetting`）。
2. 构建阶段（`BuildAssetInfo`、`BuildBundleInfo`，`TaskGetBuildMap`）。
3. Unity 构建阶段（`IBundleBuildResults`，`TaskBuilding_SBP`）。
4. 更新构建信息阶段（`TaskUpdateBundleInfo_SBP`）。
5. 清单生成阶段（`TaskCreateManifest_SBP`）。
6. 运行时消费阶段（`PackageManifest` + Runtime `AssetInfo`）。

本 README 就是把每个阶段用到的“资源信息类”拉平、横向对比了一遍：

- **同一资源在不同阶段对应不同的“视图类型”：**
  - Editor.AssetInfo → CollectAssetInfo → BuildAssetInfo → PackageAsset → Runtime.AssetInfo。
- **同一包在不同阶段也有不同视图：**
  - BuildBundleInfo → PackageBundle。

后续如果你要继续写注释，可以直接在源码中引用这个 README 文件名 `README_AssetDataFlow.md`，提醒自己“详细说明见文档”。
