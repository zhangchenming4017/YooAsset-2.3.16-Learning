# YooAsset 包构建与运行时概览（包 / 清单 / Catalog / 文件系统 / 模式）

> 目录：`Assets/AOT&Framework/YooAsset/Editor/AssetBundleBuilder/README_PackagesAndRuntime.md`
>
> 适用版本：本项目内置的 YooAsset 2.3.16
>
> 本文作为对以下几个 README 的“总纲”与补充：
> - `README_AssetDataFlow.md`：侧重构建期 Asset → Bundle → Manifest 的数据流；
> - `README_AssetTag.md`：侧重 Tag 在收集/构建/首包/运行时的作用。
>
> 本文则站在更高一层，从“**包 Package 的生命周期**”视角，把：
> - 构建管线（SBP）；
> - 清单产物（.bytes/.json/.hash/.version/.report）；
> - Catalog；
> - 文件系统（FileSystem）；
> - 运行模式（PlayMode）；
>
> 串成一条完整的理解线，方便以后查阅和回忆。

---

## 1. 名词表：先把几个容易混的词对上号

- **Package（包裹）**
  - 编辑器配置层：`AssetBundleCollectorPackage`（收集规则、Tag 等）。
  - 构建/运行层：`ResourcePackage`（运行时你拿到的 `YooAssets.CreatePackage/ GetPackage`）。

- **PackageManifest（资源清单）**
  - 类型：`Runtime/ResourcePackage/PackageManifest.cs`。
  - 内容：AssetList + BundleList + 依赖 + Tag + FileHash/CRC/Size 等。
  - 磁盘上的主要表现形式：
    - `{PackageName}_{PackageVersion}.bytes`（二进制清单，运行时真正用它）；
    - `{PackageName}_{PackageVersion}.json`（可读清单，只给人看）。

- **清单辅助文件**
  - `{PackageName}_{PackageVersion}.hash`：对 `.bytes` 做 CRC32，给热更/校验用。
  - `{PackageName}.version`：记录当前包使用的版本号（例如 v1.0），给首包/版本查询用。
  - `{PackageName}_{PackageVersion}.report`：构建报告（统计/明细），不参与运行时。

- **Catalog（内置目录）**
  - 类型：`Runtime/FileSystem/DefaultBuildinFileSystem/DefaultBuildinFileCatalog.cs`；
  - 生成工具：`CatalogTools.CreateCatalogFile`，对应构建任务 `TaskCreateCatalog_SBP`；
  - 内容：`List<(BundleGUID, FileName)>`，描述“**首包目录（StreamingAssets）里实际存在的 Bundle 子集**”。

- **FileSystem（文件系统）**
  - 一组实现 `IFileSystem` 的组件，回答“某个逻辑 Bundle 现在在哪个物理位置可以读到？”
  - 典型实现：
    - 默认内置文件系统：`DefaultBuildinFileSystem`（StreamingAssets/yoo/...）；
    - 默认缓存文件系统：DefaultCacheFileSystem（persistentDataPath/... + 远程下载）；
    - Web 专用文件系统：WebServerFileSystem / 微信小游戏 FS 等。

- **PlayMode（运行模式）**
  - 枚举：`EPlayMode`；
  - 主要：
    - `EditorSimulateMode`：编辑器模拟；
    - `OfflinePlayMode`：纯首包离线；
    - `HostPlayMode`：首包 + 缓存 + 远程（真正的热更模式）；
    - `WebPlayMode`：WebGL/小游戏专用模式。

---

## 2. 构建期：从收集到版本目录（回顾）

这一节只做极简回顾，详细可看 `README_AssetDataFlow.md`，这里只把后面会引用的几个关键点列出来。

### 2.1 收集阶段：Asset → BuildAssetInfo

- 收集配置：`Editor/AssetBundleCollector/AssetBundleCollectorPackage/Group/Collector`；
- 产物：`CollectAssetInfo` → `BuildAssetInfo`（在 `TaskGetBuildMap` 中转换）。
- `BuildAssetInfo` 的重要字段：
  - `BundleName`：打入哪个 Bundle；
  - `AllDependAssetInfos`：资源级依赖图；
  - `AssetTags`：收集配置打上的标签。

### 2.2 Unity 构建阶段：BuildBundleInfo → SBP

- `TaskGetBuildMap_SBP` 聚合为 `BuildBundleInfo`；
- `TaskBuilding_SBP` 调用 Unity 的 Scriptable Build Pipeline（SBP）：
  - 输入：`AssetBundleBuild[]`；
  - 输出：中间目录 `{BuildOutputRoot}/{BuildTarget}/{PackageName}/{OutputFolderName}` 下的 `.bundle` 文件（文件名是原始 BundleName），外加 `IBundleBuildResults`（哈希/依赖等）。

### 2.3 更新 Bundle 信息：TaskUpdateBundleInfo_SBP

- 基于 SBP 输出的 .bundle / .encrypt，决定：
  - 最终发布文件名（可能带哈希、原名_哈希等，受 `EFileNameStyle` 影响）；
  - 源文件路径（加密则取 `.encrypt`，否则取 `.bundle`）；
  - `FileHash/CRC/FileSize/Encrypted` 等写回到 `BuildBundleInfo`。

### 2.4 生成清单：TaskCreateManifest_SBP

核心代码：`Editor/AssetBundleBuilder/BuildPipeline/BaseTasks/TaskCreateManifest.cs`。

- 构建 `PackageManifest`：
  - `AssetList = CreatePackageAssetList(...)`：
    - 把每个主资源（BuildAssetInfo）转换为 `PackageAsset`；
    - 写入：`Address/AssetPath/AssetGUID/AssetTags` 等；
  - `BundleList = CreatePackageBundleList(...)`：
    - 把每个 BuildBundleInfo 转换为 `PackageBundle`；
    - 写入：`BundleName` 等基本信息（最终 FileName 会由 ManifestTools Init 时计算）。

- 处理依赖与 Tag：
  1. `ProcessPacakgeAsset`：
     - 为每个 `PackageAsset` 填 `BundleID`（它所在的主包索引）；
     - 通过 `BuildAssetInfo.AllDependAssetInfos` 计算 `PackageAsset.DependBundleIDs`（资产级依赖）；
  2. `ProcessBundleDepends`：
     - 用 `IBundleBuildResults.BundleInfos[bundleName].Dependencies` 生成 `PackageBundle.DependBundleIDs`（包级依赖）；
  3. `ProcessBundleTags`：
     - 主资源的 Tag 会传播到它的主包和依赖包，写入 `PackageBundle.Tags`。

- 写盘：
  - `{PackageName}_{PackageVersion}.json`：`ManifestTools.SerializeToJson`；
  - `{PackageName}_{PackageVersion}.bytes`：`ManifestTools.SerializeToBinary`；
  - `{PackageName}_{PackageVersion}.hash`：`HashUtility.FileCRC32(.bytes)`；
  - `{PackageName}.version`：写入 `PackageVersion`。

> 这一步之后，**版本目录**（`Bundles/<BuildTarget>/<PackageName>/<PackageVersion>/`）就具备了：
> - 完整的 `PackageManifest` 清单（.bytes/.json/.hash/.version）；
> - 最终命名后的 AB 文件（在 `TaskCreatePackage_SBP` 完成复制后）。

---

## 3. 首包：从版本目录到 StreamingAssets 的子集

构建完“版本目录”后，YooAsset 还可以选择把一部分内容拷到 Unity 的 `StreamingAssets` 目录，形成“首包内置资源”。

### 3.1 首包拷贝：TaskCopyBuildinFiles_SBP

文件：`Editor/AssetBundleBuilder/BuildPipeline/BaseTasks/TaskCopyBuildinFiles.cs`。

脚本管线中的调用：`TaskCopyBuildinFiles_SBP`：

```csharp
var buildParametersContext = context.GetContextObject<BuildParametersContext>();
var manifestContext = context.GetContextObject<ManifestContext>();
if (buildParametersContext.Parameters.BuildinFileCopyOption != EBuildinFileCopyOption.None)
{
    CopyBuildinFilesToStreaming(buildParametersContext, manifestContext.Manifest);
}
```

- 源目录：版本目录：`buildParametersContext.GetPackageOutputDirectory()`；
- 目标目录：首包目录：`buildParametersContext.GetBuildinRootDirectory()` → 约定为：
  - `Assets/StreamingAssets/yoo/<PackageName>/`

拷贝逻辑（简化）：

1. 如有需要先清空目标目录：
   - `ClearAndCopyAll` / `ClearAndCopyByTags`
2. 拷贝清单文件三件套：
   - `{PackageName}_{PackageVersion}.bytes`；
   - `{PackageName}_{PackageVersion}.hash`；
   - `{PackageName}.version`；
3. 拷贝 Bundle 文件：
   - `OnlyCopyAll / ClearAndCopyAll`：全部 `manifest.BundleList` 对应的 `FileName`；
   - `OnlyCopyByTags / ClearAndCopyByTags`：只拷贝 `packageBundle.HasTag(tags)` 为 true 的 Bundle。

至此，**首包目录中实际存在的内容是：**

- 清单 .bytes/.hash/.version（必备）；
- 部分或全部 AB 文件（按首包策略/Tag 决定）。

> 注意：首包目录最终会被 Unity 在 Player 构建时整体打进安装包（StreamingAssets），成为玩家首次安装就具备的本地资源集合。

### 3.2 首包 Catalog：TaskCreateCatalog_SBP

文件：

- `Editor/AssetBundleBuilder/BuildPipeline/BaseTasks/TaskCreateCatalog.cs`
- `Editor/AssetBundleBuilder/BuildPipeline/ScriptableBuildPipeline/BuildTasks/TaskCreateCatalog_SBP.cs`
- `Runtime/FileSystem/DefaultBuildinFileSystem/CatalogTools.cs`
- `Runtime/FileSystem/DefaultBuildinFileSystem/DefaultBuildinFileCatalog.cs`

构建管线中的调用：

```csharp
// TaskCreateCatalog_SBP
var buildParametersContext = context.GetContextObject<BuildParametersContext>();
if (buildParametersContext.Parameters.BuildinFileCopyOption != EBuildinFileCopyOption.None)
{
    CreateCatalogFile(buildParametersContext);
}
```

`TaskCreateCatalog.CreateCatalogFile`：

```csharp
string buildinRootDirectory = buildParametersContext.GetBuildinRootDirectory(); // StreamingAssets/yoo/<PackageName>
string buildPackageName = buildParametersContext.Parameters.PackageName;
var manifestServices = buildParametersContext.Parameters.ManifestRestoreServices;
CatalogTools.CreateCatalogFile(manifestServices, buildPackageName, buildinRootDirectory);
```

关键实现：`CatalogTools.CreateCatalogFile`（核心思想）：

1. 读取首包目录下的 `.version`：
   - 得到 `packageVersion`；
2. 读取对应 `.bytes` 清单：
   - `PackageManifest packageManifest = ManifestTools.DeserializeFromBinary(...);`
3. 构建 `FileName -> BundleGUID` 映射（遍历 `packageManifest.BundleList`）；
4. 遍历首包目录 `StreamingAssets/yoo/<PackageName>` 中所有实际存在的文件：
   - 过滤掉 `.meta` 和白名单（`.bytes/.hash/.version/.json/.report/link.xml/catalog自身` 等非 Bundle 文件）；
   - 对每个剩下的 `fileInfo.Name`
     - 若在映射表中找到对应 BundleGUID：
       - 向 `DefaultBuildinFileCatalog.Wrappers` 添加 `(BundleGUID, FileName)`；
     - 否则打一个警告。
5. 最后写出：
   - `yoo_buildin_catalog.json`（给人看）；
   - `yoo_buildin_catalog.bytes`（运行时 `DefaultBuildinFileSystem` 读取）。

> 用一句话概括：**Catalog 是基于清单 + 首包目录实际文件，生成的一份“首包中真实存在的 Bundle 子集列表”。**

---

## 4. 运行时：PlayMode 与文件系统组合

入口逻辑在：`Assets/AOT&Framework/Boot.cs` → `PatchOperation` → `FsmInitializePackage`。

### 4.1 Boot：初始化 YooAsset 与补丁流程

```csharp
// Boot.Start
YooAssets.Initialize();

// 启动补丁状态机
var operation = new PatchOperation("DefaultPackage", PlayMode);
YooAssets.StartOperation(operation);
yield return operation;

// 补丁流程完成后，设置默认包
var gamePackage = YooAssets.GetPackage("DefaultPackage");
YooAssets.SetDefaultPackage(gamePackage);
```

- `PlayMode` 在 `Boot` 的 Inspector 中配置（默认 `EditorSimulateMode`）。
- `PatchOperation` 内部状态机从 `FsmInitializePackage` 开始按模式初始化包。

### 4.2 FsmInitializePackage：按模式选择文件系统

文件：`Assets/AOT&Framework/PatchLogic/FsmNode/FsmInitializePackage.cs`。

关键代码：

```csharp
var playMode = (EPlayMode)_machine.GetBlackboardValue("PlayMode");
var packageName = (string)_machine.GetBlackboardValue("PackageName");

// 创建资源包裹类
var package = YooAssets.TryGetPackage(packageName) ?? YooAssets.CreatePackage(packageName);

InitializationOperation initializationOperation = null;
```

#### 4.2.1 EditorSimulateMode（编辑器模拟模式）

```csharp
if (playMode == EPlayMode.EditorSimulateMode)
{
    var buildResult = EditorSimulateModeHelper.SimulateBuild(packageName);
    var packageRoot = buildResult.PackageRootDirectory;
    var createParameters = new EditorSimulateModeParameters();
    createParameters.EditorFileSystemParameters = FileSystemParameters.CreateDefaultEditorFileSystemParameters(packageRoot);
    initializationOperation = package.InitializeAsync(createParameters);
}
```

- 目的：**不依赖真实 AB 文件与 StreamingAssets，直接通过 AssetDatabase 模拟加载行为，同时保持和真实 PlayMode 接口一致。**
- `SimulateBuild`：快速生成一份模拟清单，`packageRoot` 指向模拟构建输出；
- `CreateDefaultEditorFileSystemParameters`：
  - 内部构建一个基于 AssetDatabase 的文件系统，
  - 使 `LoadAssetAsync` 等调用在编辑器中直接走工程资源，而不是 AB 文件。

> 适合：开发阶段快速迭代，不需要真正跑打包/热更。

#### 4.2.2 OfflinePlayMode（单机离线模式）

```csharp
if (playMode == EPlayMode.OfflinePlayMode)
{
    var createParameters = new OfflinePlayModeParameters();
    createParameters.BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters();
    initializationOperation = package.InitializeAsync(createParameters);
}
```

- 只配置了 `BuildinFileSystemParameters`：
  - 告诉 YooAsset 使用 `DefaultBuildinFileSystem`；
  - 默认根目录：`StreamingAssets/yoo/<PackageName>`（内部基于 `Application.streamingAssetsPath`）。
- 没有 Cache / Remote 文件系统：
  - 表示不访问任何远程地址，也不下载；
  - 所有清单与 Bundle 必须事先由构建管线拷入首包目录。

> 适合：纯本地游戏 / Demo / 不做热更时的简单配置。

#### 4.2.3 HostPlayMode（联机热更模式）

```csharp
if (playMode == EPlayMode.HostPlayMode)
{
    string defaultHostServer = GetHostServerURL();
    string fallbackHostServer = GetHostServerURL();
    IRemoteServices remoteServices = new RemoteServices(defaultHostServer, fallbackHostServer);
    var createParameters = new HostPlayModeParameters();
    createParameters.BuildinFileSystemParameters = FileSystemParameters.CreateDefaultBuildinFileSystemParameters();
    createParameters.CacheFileSystemParameters = FileSystemParameters.CreateDefaultCacheFileSystemParameters(remoteServices);
    initializationOperation = package.InitializeAsync(createParameters);
}
```

- 同时配置：
  1. `BuildinFileSystemParameters`：
     - 使用 `DefaultBuildinFileSystem` 管理首包资源：
       - 初始化时读取 `StreamingAssets/yoo/<PackageName>` 中的：
         - `{PackageName}.version`；
         - `{PackageName}_{PackageVersion}.bytes`（清单）；
         - `yoo_buildin_catalog.bytes`（上一步构建生成的 Catalog）；
       - 建立“首包中哪些 Bundle 已经存在”的映射。
  2. `CacheFileSystemParameters`（带 `IRemoteServices`）：
     - 使用缓存文件系统 + 远程下载服务：
       - 查询本地缓存目录是否已有某个 Bundle；
       - 没有则通过 `IRemoteServices.GetRemoteMainURL(fileName)` 得到下载地址，从服务器下载到缓存目录，再参与后续读取。

> 适合：正式运维的联机热更模式：
> - 一部分内容随首包发（首包拷贝）；
> - 后续版本/增量包走远程下载 + 本地缓存。

#### 4.2.4 WebPlayMode（WebGL / 小游戏模式）

```csharp
if (playMode == EPlayMode.WebPlayMode)
{
#if UNITY_WEBGL && WEIXINMINIGAME && !UNITY_EDITOR
    var createParameters = new WebPlayModeParameters();
	string defaultHostServer = GetHostServerURL();
    string fallbackHostServer = GetHostServerURL();
    string packageRoot = $"{WeChatWASM.WX.env.USER_DATA_PATH}/__GAME_FILE_CACHE";
    IRemoteServices remoteServices = new RemoteServices(defaultHostServer, fallbackHostServer);
    createParameters.WebServerFileSystemParameters = WechatFileSystemCreater.CreateFileSystemParameters(packageRoot, remoteServices);
    initializationOperation = package.InitializeAsync(createParameters);
#else
    var createParameters = new WebPlayModeParameters();
    createParameters.WebServerFileSystemParameters = FileSystemParameters.CreateDefaultWebServerFileSystemParameters();
    initializationOperation = package.InitializeAsync(createParameters);
#endif
}
```

- Web 平台下：
  - 传统的 StreamingAssets 文件系统和磁盘缓存模型有所不同（如浏览器沙盒、小游戏的专用存储）；
  - 因此 YooAsset 提供了 `WebServerFileSystem` / WechatFS 等专用实现：
    - 负责通过 HTTP/小游戏 API 访问包体与增量内容；
    - 视平台情况管理本地沙盒缓存。
- 不再显式配置 `BuildinFileSystem` / `CacheFileSystem`，而是让 `WebServerFileSystem` 统一承接 Web 平台的“文件来源”。

> 适合：WebGL 发布 / 微信小游戏等场景，由平台能力决定文件访问方式。

---

## 5. 汇总视图：模式 × 清单 × Catalog × 文件系统

可以用一个表简单记住这几者的关系：

| PlayMode              | 构建要求                                      | 使用的清单                               | 是否用 Catalog | 使用的 FileSystem 参数                                                                 |
|-----------------------|----------------------------------------------|------------------------------------------|----------------|----------------------------------------------------------------------------------------|
| EditorSimulateMode    | 可选模拟构建（EditorSimulateModeHelper）     | 模拟清单（内部生成）                      | 否             | `EditorFileSystemParameters`（AssetDatabase 模拟）                                     |
| OfflinePlayMode       | 必须做正常构建 + 可选首包拷贝                | `{P}_{V}.bytes/.hash/.version`（版本目录 & 首包） | 若配置首包拷贝 | `BuildinFileSystemParameters`（DefaultBuildinFileSystem，仅首包，不下载）            |
| HostPlayMode          | 必须做正常构建 + 推荐配置首包拷贝 + Catalog | 同上                                      | 是（推荐）      | `BuildinFileSystemParameters` + `CacheFileSystemParameters(remote)`（首包 + 缓存 + 远程） |
| WebPlayMode           | 对应平台的 Web 部署                         | 同上                                      | 视实现而定      | `WebServerFileSystemParameters`（HTTP/小游戏 FS + 沙盒缓存）                           |

其中：

- **清单（PackageManifest）**：所有模式共享的“逻辑真相”，始终来源于 `{PackageName}_{PackageVersion}.bytes`。
- **Catalog（DefaultBuildinFileCatalog）**：
  - 只在使用 `DefaultBuildinFileSystem` 时有意义；
  - 由 `TaskCreateCatalog_SBP` 针对首包目录生成，描述“首包中有哪些 Bundle 已经存在”。
- **文件系统**：根据 PlayMode 组合不同实现，决定“某个 Bundle 现在从哪里读”。

---

## 6. 如何在项目中运用这些概念？

1. **设计构建策略**：
   - 收集规则/Tag → 决定 Bundle 粒度与内容分层；
   - `EBuildinFileCopyOption` + Tag → 决定哪些 Bundle 进入首包（StreamingAssets）；
   - 版本部署结构 `{PackageName}_{PackageVersion}.*` → 决定服务器/CDN 目录。

2. **选择合适 PlayMode 做本地开发与发布**：
   - 开发期：优先用 `EditorSimulateMode`；
   - 联机联调：切 `HostPlayMode` + 本地 HTTP 服务器；
   - 纯单机：`OfflinePlayMode` + 足够的首包拷贝。

3. **理解运行时行为时，优先问三件事**：
   1. 当前 Package 正在使用哪个 PlayMode？
   2. 这个 PlayMode 下，启用了哪些 FileSystem？
   3. 某个 Bundle/资源对应的 FileName / BundleGUID 是什么？（由 PackageManifest 决定）

   然后再去看：
   - 这个 Bundle 是否在首包 Catalog 里？（DefaultBuildinFileSystem）
   - 如果不在，缓存里有没有？（CacheFileSystem）
   - 都没有时，远程 URL 是什么？（IRemoteServices）

---

以后如果你在阅读构建/补丁/加载相关代码时感到迷糊，可以用本文的顺序快速回顾：

1. 是哪个 Package / PlayMode？
2. 当前看的是构建期哪一步（TaskGetBuildMap / TaskCreateManifest / TaskCopyBuildinFiles / TaskCreateCatalog）？
3. 运行时是哪个 FileSystem 在处理这次加载？
4. 清单（.bytes）、Catalog、首包目录、缓存目录之间的关系是什么？

配合 `README_AssetDataFlow.md` 和 `README_AssetTag.md` 一起阅读，基本可以覆盖 YooAsset 大部分“从配置到构建到运行”的关键路径。