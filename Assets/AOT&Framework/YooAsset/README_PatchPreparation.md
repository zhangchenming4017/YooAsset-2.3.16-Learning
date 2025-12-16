# 补丁更新前的准备阶段（Boot → 事件系统 → 资源系统）

> 适用版本：本项目 YooAsset 2.3.16
>
> 目标：快速回顾“开始补丁流程之前”需要做的最少步骤与涉及的核心组件。
>
> 相关文件：
> - `Assets/AOT&Framework/Boot.cs`
> - `Assets/AOT&Framework/ThirdParty/UniFramework/UniEvent/Runtime/UniEvent.cs`
> - `Assets/AOT&Framework/YooAsset/Runtime/YooAssets.cs`
> - `Assets/AOT&Framework/PatchLogic/PatchOperation.cs`

---

## 1. 一张图看全流程（准备阶段）

Boot.Start（协程）按顺序执行：

1) 创建游戏管理器宿主
- `GameManager.Instance.Behaviour = this;`
- 作用：把 `Boot` 这个 `MonoBehaviour` 作为“协程/定时/场景切换监听”的统一宿主。

2) 初始化事件系统（UniEvent）
- `UniEvent.Initalize();`
- 做了什么：
  - 创建驱动器 `GameObject [UniEvent]` 并挂载 `UniEventDriver`；
  - `UniEvent.Update()` 每帧驱动延迟广播队列（`PostMessage`）。

3) 初始化资源系统（YooAssets）
- `YooAssets.Initialize();`
- 做了什么：
  - 创建驱动器 `GameObject [YooAssets]` 并挂载 `YooAssetsDriver`；
  - 初始化 `OperationSystem`（YooAsset 的统一异步任务调度器）；
  - 之后 `YooAssets.Update()` 每帧驱动 `OperationSystem.Update()`。

4)（可选）加载补丁 UI
- 示例：`Resources.Load("PatchWindow")` + `Instantiate`
- 仅用于展示进度/交互，逻辑上与准备阶段解耦。

5) 启动补丁流程（进入状态机）
- `var op = new PatchOperation("DefaultPackage", PlayMode);`
- `YooAssets.StartOperation(op);`
- `yield return op;`
- 说明：
  - `PatchOperation : GameAsyncOperation`，内部持有 `StateMachine`；
  - 黑板初始化键：`PackageName`、`PlayMode`；
  - 首个状态：`FsmInitializePackage`（按 PlayMode 创建并初始化包）。

6) 设置默认包（补丁流程完成后）
- `var pkg = YooAssets.GetPackage("DefaultPackage");`
- `YooAssets.SetDefaultPackage(pkg);`
- 作用：后续 `LoadAssetAsync` 等 API 可不显式传包名，走“默认包”。

---

## 2. 关键参与者与职责边界

- `Boot : MonoBehaviour`
  - 运行入口；
  - 在 `Inspector` 里配置 `PlayMode`；
  - 串起：事件系统初始化 → 资源系统初始化 → 启动补丁。

- `GameManager`
  - 游戏侧单例；
  - `Behaviour` 指向一个 `MonoBehaviour` 宿主（此处为 `Boot`）。

- `UniEvent`
  - 轻量事件系统；
  - `Initalize()` 时生成 `UniEventDriver`；
  - `PostMessage` 的延迟派发由 `UniEvent.Update()` 逐帧处理。

- `YooAssets`
  - 资源系统门面；
  - `Initialize()` 时生成 `YooAssetsDriver` 并初始化 `OperationSystem`；
  - `StartOperation(GameAsyncOperation)` 将“游戏侧操作”纳入 YooAsset 调度（包名为空）。

- `OperationSystem`
  - YooAsset 内部异步任务调度器；
  - 管控 `AsyncOperationBase` 系列任务的生命周期/优先级/时间片。

- `PatchOperation : GameAsyncOperation`
  - 补丁流程入口；
  - 内部 `StateMachine` 预注册状态：
    - `FsmInitializePackage`、`FsmRequestPackageVersion`、`FsmUpdatePackageManifest`、
      `FsmCreateDownloader`、`FsmDownloadPackageFiles`、`FsmDownloadPackageOver`、
      `FsmClearCacheBundle`、`FsmStartGame`；
  - 该文档只关注进入它之前的准备工作与入口调用。

---

## 3. PlayMode 选择（在 Boot 上配置）

- 字段：`Boot.PlayMode : EPlayMode`
- 影响：`FsmInitializePackage` 会据此创建不同的 `InitializeParameters` + `FileSystemParameters` 组合：
  - `EditorSimulateMode`：编辑器模拟，走 `AssetDatabase`，无需真实 AB；
  - `OfflinePlayMode`：仅 `DefaultBuildinFileSystem`（首包离线，不下载）；
  - `HostPlayMode`：`Buildin + Cache(Remote)`（首包 + 远程下载 + 本地缓存）；
  - `WebPlayMode`：Web/小游戏专用文件系统实现。

---

## 4. 最小可用示例（Boot.Start 关键片段）

```csharp
// 1) 游戏管理器
GameManager.Instance.Behaviour = this;

// 2) 事件系统
UniEvent.Initalize();

// 3) 资源系统
YooAssets.Initialize();

// 4) 启动补丁流程
var operation = new PatchOperation("DefaultPackage", PlayMode);
YooAssets.StartOperation(operation);
yield return operation; // 等待补丁完成

// 5) 设置默认包
var gamePackage = YooAssets.GetPackage("DefaultPackage");
YooAssets.SetDefaultPackage(gamePackage);
```

---

## 5. 常见易错点

- 忘记调用 `UniEvent.Initalize()`：导致事件派发器未创建，补丁 UI 的交互事件不生效。
- 忘记调用 `YooAssets.Initialize()`：资源系统与调度器未启动，`PatchOperation` 不会更新。
- `StartOperation` 后未 `yield return`：补丁尚未跑完就去加载资源，易失败。
- 补丁完成后未 `SetDefaultPackage`：后续加载若未指定包名会找不到包。

---

## 6. 进一步阅读

- 构建与运行期总览：`Editor/AssetBundleBuilder/README_PackagesAndRuntime.md`
- 补丁状态机入口代码：`Assets/AOT&Framework/PatchLogic/PatchOperation.cs`
- 事件系统实现：`Assets/AOT&Framework/ThirdParty/UniFramework/UniEvent/Runtime/UniEvent.cs`
- 资源系统入口：`Assets/AOT&Framework/YooAsset/Runtime/YooAssets.cs`
