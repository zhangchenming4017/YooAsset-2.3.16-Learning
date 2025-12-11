# YooAsset Scriptable Build Pipeline 中的 IContext 体系

> 目标：帮自己快速回忆 SBP 管线里有哪些 `IContextObject`，它们在什么任务里创建、干什么、谁来用。

所有路径基于：`Assets/AOT&Framework/YooAsset/Editor/AssetBundleBuilder`。

---

## 0. 总览：一条“流水线上的上下文”

SBP 入口：

```csharp
public class ScriptableBuildPipeline : IBuildPipeline
{
    public BuildResult Run(BuildParameters buildParameters, bool enableLog)
    {
        if (buildParameters is ScriptableBuildParameters)
        {
            AssetBundleBuilder builder = new AssetBundleBuilder();
            return builder.Run(buildParameters, GetDefaultBuildPipeline(), enableLog);
        }
        throw new Exception($"Invalid build parameter type : {buildParameters.GetType().Name}");
    }
}
```

默认任务链（重要顺序）：

```csharp
private List<IBuildTask> GetDefaultBuildPipeline()
{
    return new List<IBuildTask>
    {
        new TaskPrepare_SBP(),
        new TaskGetBuildMap_SBP(),
        new TaskBuilding_SBP(),
        new TaskVerifyBuildResult_SBP(),
        new TaskEncryption_SBP(),
        new TaskUpdateBundleInfo_SBP(),
        new TaskCreateManifest_SBP(),
        new TaskCreateReport_SBP(),
        new TaskCreatePackage_SBP(),
        new TaskCopyBuildinFiles_SBP(),
        new TaskCreateCatalog_SBP(),
    };
}
```

每个 `Task*` 都只拿到一个 `BuildContext`：

```csharp
public interface IBuildTask
{
    void Run(BuildContext context);
}
```

`BuildContext` 内部是「类型 → 上下文对象」的字典：

```csharp
public class BuildContext
{
    private readonly Dictionary<Type, IContextObject> _contextObjects = new();

    public void SetContextObject(IContextObject contextObject)
    {
        var type = contextObject.GetType();
        _contextObjects.Add(type, contextObject);
    }

    public T GetContextObject<T>() where T : IContextObject
    {
        return (T)_contextObjects[typeof(T)];
    }
}
```

构建过程中实际出现的核心 `IContextObject`：

- `BuildParametersContext`
- `BuildMapContext`
- `TaskBuilding_SBP.BuildResultContext`
- `ManifestContext`
- 报表相关的 `ReportContext`（名字在 `TaskCreateReport.cs` 中）

下面按“创建顺序”来记。

---

## 1. `BuildParametersContext` – 参数 & 路径

**文件**：`BuildParametersContext.cs`

**创建时机**：

```csharp
// AssetBundleBuilder.Run
_buildContext.ClearAllContext();
var buildParametersContext = new BuildParametersContext(buildParameters);
_buildContext.SetContextObject(buildParametersContext);
```

**里面是什么**（核心）：

- `BuildParameters Parameters`：原始构建参数（管线名、PackageName、Version、加密服务等）。
- 一堆常用路径：
  - `GetPipelineOutputDirectory()` → `.../OutputCache`，SBP 中间产物目录
  - `GetPackageOutputDirectory()` → `.../{PackageVersion}`，最终发布目录
  - `GetPackageRootDirectory()` → `.../{PackageName}`，包根目录
  - `GetBuildinRootDirectory()` → `StreamingAssets/yoo/{PackageName}`，首包拷贝目标

**谁在用**：

- `TaskPrepare_SBP`：检查参数、清理目录。
- `TaskGetBuildMap_SBP`：拿到 `Parameters` 去创建 `BuildMapContext`。
- `TaskBuilding_SBP`：把 `Parameters` 强转为 `ScriptableBuildParameters` 来生成 SBP 的 `BundleBuildParameters`。
- `TaskUpdateBundleInfo_SBP`、`TaskCreateManifest_SBP`、`TaskCreatePackage_SBP`、`TaskCopyBuildinFiles_SBP`、`TaskCreateCatalog_SBP` 都会用到各种路径方法。

> 心里可以把它当成：**“本次构建的配置 & 所有关键路径的入口”**。

---

## 2. `BuildMapContext` – 资源 & Bundle 的“设计图”

**文件**：`BuildMapContext.cs`，构建逻辑在 `TaskGetBuildMap.cs`。

**创建时机**：

```csharp
// TaskGetBuildMap_SBP.Run
var buildParametersContext = context.GetContextObject<BuildParametersContext>();
var buildMapContext = CreateBuildMap(false, buildParametersContext.Parameters);
context.SetContextObject(buildMapContext);
```

**里面的关键信息**：

- `_bundleInfoDic : Dictionary<string, BuildBundleInfo>`
  - 键：bundleName
  - 值：`BuildBundleInfo`（“这个包里有哪些资源、后续写到清单的基础数据”）
- `SpriteAtlasAssetList : List<BuildAssetInfo>`
  - 所有图集资源；后面用于补图集依赖。
- `IndependAssets : List<ReportIndependAsset>`
  - 被 Depend 收集器收集到，但最终“零引用”而被剔除的资源，供报表使用。
- `AssetFileCount : int`：参与构建的资源总数。
- `Command : CollectCommand`：收集命令（是否启用 addressable、是否写 GUID 等）。
- `Collection`：`_bundleInfoDic.Values` 的只读视图，后面所有任务都用它来遍历包。

**`CreateBuildMap` 做的事（按阶段简记）**：

1. 通过收集器拿到所有 `CollectAssetInfo`（主动收集的资源）。
2. 调 `RemoveZeroReferenceAssets`：剔除“不被 Main/Static 引用”的 Depend 资产，记录到 `IndependAssets`。
3. 给所有主动收集的资源创建 `BuildAssetInfo`（含 `BundleName/Address/Tags`），放入 `allBuildAssetInfos`。
4. 把 `CollectAssetInfo.DependAssets` 中“仅作为依赖出现”的资源也补成 `BuildAssetInfo(CollectorType.None)`，并建立“被哪些 bundle 引用”的引用计数。
5. 为每个主资源填充 `AllDependAssetInfos`（强类型依赖图）。
6. 如启用 `AutoCollectShaders`，把“仅作为依赖出现的 Shader”统一指定到一个 Shader 包。
7. 如启用 `EnableSharePackRule`，对尚未分配 BundleName 的依赖资源应用共享打包规则（根据引用次数、`SingleReferencedPackAlone` 决定独立或共享分包）。
8. 记录 `AssetFileCount` 和收集命令 `Command` 到 `BuildMapContext`。
9. 删除仍然没有 BundleName 的资源（它们会按 Unity 的“隐式依赖”规则打进引用它们的包）。
10. 按 BundleName 把所有 `BuildAssetInfo` 聚成多个 `BuildBundleInfo`，调用 `BuildMapContext.PackAsset` 填进 `_bundleInfoDic`。

**后续使用场景**：

- **1）SBP 构建输入** – `TaskBuilding_SBP`：

```csharp
var buildMapContext = context.GetContextObject<BuildMapContext>();
var buildContent = new BundleBuildContent(buildMapContext.GetPipelineBuilds());
```

- **2）构建结果校验** – `TaskVerifyBuildResult_SBP`：

```csharp
var buildMapContext = context.GetContextObject<BuildMapContext>();
var planningContent = buildMapContext.Collection.Select(t => t.BundleName).ToList();
// 与 buildResults.BundleInfos.Keys 对比
```

- **3）填写最终文件信息** – `TaskUpdateBundleInfo_SBP`：

  遍历 `buildMapContext.Collection`，为每个 `BuildBundleInfo` 写入：
  - 源文件路径（中间 `.bundle` 或 `.encrypt`）
  - 目标文件路径（版本目录下最终命名后的文件）
  - `PackageFileHash/PackageFileCRC/PackageFileSize/Encrypted/...` 等

- **4）生成清单** – `TaskCreateManifest_SBP`（基类 `TaskCreateManifest`）：

  - 用 `BuildMapContext` 生成：
    - `PackageManifest.AssetList`：所有主资源（`PackageAsset`）
    - `PackageManifest.BundleList`：所有资源包（`PackageBundle`）
  - 用 `BuildAssetInfo.AllDependAssetInfos` 计算 **资产级依赖** `PackageAsset.DependBundleIDs`。
  - 用 `SpriteAtlasAssetList` 补充图集相关内置依赖。

> 记忆关键词：**「规划期视图」+「资源级依赖图」+「清单和报告的基础」**。

---

## 3. `TaskBuilding_SBP.BuildResultContext` – SBP 的“真实结果”

**文件**：`BuildTasks/TaskBuilding_SBP.cs`

**内部结构（精简）**：

```csharp
public class TaskBuilding_SBP : IBuildTask
{
    public class BuildResultContext : IContextObject
    {
        public IBundleBuildResults Results;
        public string BuiltinShadersBundleName;
        public string MonoScriptsBundleName;
    }
}
```

**创建时机**：

```csharp
var buildContent = new BundleBuildContent(buildMapContext.GetPipelineBuilds());
IBundleBuildResults buildResults;
var buildParameters = scriptableBuildParameters.GetBundleBuildParameters();
var taskList = SBPBuildTasks.Create(builtinShadersBundleName, monoScriptsBundleName);

ReturnCode exitCode = ContentPipeline.BuildAssetBundles(buildParameters, buildContent, out buildResults, taskList);
// 成功后：
var buildResultContext = new TaskBuilding_SBP.BuildResultContext
{
    Results = buildResults,
    BuiltinShadersBundleName = builtinShadersBundleName,
    MonoScriptsBundleName = monoScriptsBundleName,
};
context.SetContextObject(buildResultContext);
```

**核心内容**：

- `IBundleBuildResults Results` 里有：
  - `BundleInfos[bundleName].Hash`：Unity 计算的内容哈希
  - `BundleInfos[bundleName].Crc`：Unity 计算的 CRC
  - `BundleInfos[bundleName].Dependencies : string[]`：**bundle 级依赖**
- `BuiltinShadersBundleName` / `MonoScriptsBundleName`：
  - 是否有生成“内置 Shader 包”、“MonoScripts 包”的信息。

**后续使用场景**：

- **1）验证结果** – `TaskVerifyBuildResult_SBP`：

```csharp
var buildResultContext = context.GetContextObject<TaskBuilding_SBP.BuildResultContext>();
VerifyingBuildingResult(context, buildResultContext.Results);
```

- **2）更新 `BuildBundleInfo`** – `TaskUpdateBundleInfo_SBP`：

  从 `buildResultContext.Results.BundleInfos[bundleName]` 拿到 Unity 的 Hash/CRC/Size，写入各个 `BuildBundleInfo`。

- **3）生成清单里的「bundle 级依赖」** – `TaskCreateManifest_SBP`：

```csharp
protected override string[] GetBundleDepends(BuildContext context, string bundleName)
{
    var buildResultContext = context.GetContextObject<TaskBuilding_SBP.BuildResultContext>();
    return buildResultContext.Results.BundleInfos[bundleName].Dependencies;
}
```

> 记忆关键词：**“构建后结果” + “Unity 权威依赖” + “Hash/CRC”**。

---

## 4. `ManifestContext` – 清单对象

**文件**：`BaseTasks/TaskCreateManifest.cs`

**定义**：

```csharp
public class ManifestContext : IContextObject
{
    internal PackageManifest Manifest;
}
```

**创建时机**：

在 `TaskCreateManifest.CreateManifestFile` 的最后：

```csharp
var manifestContext = new ManifestContext();
byte[] bytesData = FileUtility.ReadAllBytes(packagePath);
manifestContext.Manifest = ManifestTools.DeserializeFromBinary(bytesData, buildParameters.ManifestRestoreServices);
context.SetContextObject(manifestContext);
```

**里面是什么**：

- 一个已经写到磁盘、又按运行时读取流程“反序列化回来”的 `PackageManifest`：
  - `AssetList`：所有主资源（路径 / 地址 / 标签 / 依赖包 ID 等）。
  - `BundleList`：所有资源包（最终文件名 / FileHash / FileCRC / Size / Encrypted / 依赖包 ID / 标签等）。

**谁会用**：

- `TaskCreateReport_SBP`：生成 `.report` 报告时，会结合 `ManifestContext.Manifest` 与 `BuildMapContext` 写出每个资源、每个包的最终信息。
- `TaskCreateCatalog_SBP`：生成首包内置 catalog 时，需要知道哪些清单 / bundle 是内置的。

> 记忆关键词：**“清单实体对象” + “供后续任务共享的最终视图”**。

---

## 5. 报表相关 `ReportContext`（概念级）

**文件**：`BaseTasks/TaskCreateReport.cs` 及 `BuildTasks/TaskCreateReport_SBP.cs`。

在报告创建阶段，一般会有一个 `ReportContext : IContextObject`，内部包含：

- `BuildReport` 或其构建期数据（见 `AssetBundleReporter` 相关代码）。

任务关系大致是：

- `TaskCreateReport_SBP.Run`：
  - 读取：`BuildParametersContext`、`BuildMapContext`、`ManifestContext` 等。
  - 生成：`BuildReport`（总览、每个包、每个资源、零引用资源列表等）。
  - 写入：`ReportContext`（供调试器 / 可视化窗口使用）。

> 这里对源码不展开，只要记住：**报表任务也是通过一个 IContext 把结果塞回 `BuildContext`**。

---

## 6. 一张“时间线”：各 IContext 何时创建 / 何时被读

从 `AssetBundleBuilder.Run` 到构建结束的顺序，可以浓缩成：

1. **AssetBundleBuilder.Run**
   - 创建 `BuildParametersContext`
2. **TaskPrepare_SBP**
   - 读 `BuildParametersContext` 做参数校验 / 清理
3. **TaskGetBuildMap_SBP**
   - 读 `BuildParametersContext.Parameters`
   - 创建 `BuildMapContext`
4. **TaskBuilding_SBP**
   - 读 `BuildParametersContext`、`BuildMapContext`
   - 调 SBP → 创建 `BuildResultContext`
5. **TaskVerifyBuildResult_SBP**
   - 读 `BuildMapContext` + `BuildResultContext` 比对“计划 vs 实际”
6. **TaskEncryption_SBP**
   - 读 `BuildParametersContext` + `BuildMapContext`，对中间 `.bundle` 文件进行可选加密，写回 `BuildBundleInfo.Encrypted` 等
7. **TaskUpdateBundleInfo_SBP**
   - 读 `BuildParametersContext` + `BuildMapContext` + `BuildResultContext`
   - 为每个 `BuildBundleInfo` 填写最终 FileHash/CRC/Size/路径等
8. **TaskCreateManifest_SBP**
   - 读 `BuildParametersContext` + `BuildMapContext` + `BuildResultContext`
   - 生成清单文件，创建 `ManifestContext`
9. **TaskCreateReport_SBP**
   - 读 `BuildParametersContext` + `BuildMapContext` + `ManifestContext`
   - 生成报告（以及自己的 `ReportContext`）
10. **TaskCreatePackage_SBP / TaskCopyBuildinFiles_SBP / TaskCreateCatalog_SBP**
    - 主要依赖：`BuildParametersContext` + `BuildMapContext` + `ManifestContext`

---

## 7. 重点对比：`BuildMapContext` vs `BuildResultContext`

可以用一句话区分这两个最容易混淆的上下文：

> - `BuildMapContext`：**我要怎么打包？**（规划 / 资源级）  
> - `BuildResultContext`：**Unity 实际打成什么样？**（结果 / bundle 级）

更具体一点：

- `BuildMapContext`
  - 来源：收集器 + 自己的共享打包规则
  - 粒度：资源粒度（`BuildAssetInfo`）+ 规划中的包（`BuildBundleInfo`）
  - 典型用途：
    - 生成 `AssetBundleBuild[]` 给 SBP
    - 计算资产级依赖 `PackageAsset.DependBundleIDs`
    - 为报告提供“本次打了哪些资源、哪些包”的基础数据

- `TaskBuilding_SBP.BuildResultContext`
  - 来源：`ContentPipeline.BuildAssetBundles` 的 `IBundleBuildResults`
  - 粒度：bundle 粒度
  - 典型用途：
    - 校验“计划包”与“实际产出包”是否一致
    - 为 `BuildBundleInfo` 补充 Unity Hash/CRC/Size
    - 为清单中的 `PackageBundle.DependBundleIDs` 提供权威的 bundle 级依赖

> 记忆技巧：**“Map = 规划图”，“Result = 实际结果”**，二者在 `TaskCreateManifest_SBP` 汇合，被写进同一份 `PackageManifest` 中。

---

## 8. 如果以后要扩展新的 Task / Context

- 新任务如果需要跨 Task 共享数据，就：
  1. 定义一个实现 `IContextObject` 的类，例如 `MyCustomContext`。
  2. 在某个 Task 中构建好之后：`context.SetContextObject(myContext)`。
  3. 在后续 Task 通过 `context.GetContextObject<MyCustomContext>()` 取出使用。
- 注意：同一种 Context 类型在一次构建过程中只能被 `Set` 一次（`BuildContext` 会检测重复类型）。

这样你就可以模仿 `BuildMapContext` / `BuildResultContext` 的模式，挂接自己的扩展逻辑。