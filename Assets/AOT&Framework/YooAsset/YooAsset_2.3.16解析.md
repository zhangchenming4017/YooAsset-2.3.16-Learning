# 1. 原版YooAsset（2.3.16）的实现流程及思路
## 1.1. Boot.cs：MonoBehaviour
> 功能概述：Boot作为整个项目启动类，完成整个框架各种系统启动以及资源更新的初始化操作
### 1.1.1. 初始化游戏管理器（GameManager）
> **GameManager.cs**

将GameManager的`Behaviour`设置为该`MonoBehaviour`，作为后续协程的发起者和监听场景切换事件。
### 1.1.2. 初始化事件系统（UniEvent）
> <u>**事件系统** （ThirdParty/UniEvent）</u>
>
> ​	**EventGroup.cs**
>
> ​	**IEventMessage.cs**
>
> ​	**UniEvent.cs**
>
> ​	**UniEventDriver.cs**

​	`UniEvent.Initalize()`创建`UniEventDriver`作为**事件系统的驱动器**，驱动器负责执行延迟广播事件（ps：原框架中并未进行任何延迟广播）。

​	`UniEvent.Dictionary<int, LinkedList<Action<IEventMessage>>> _listener`保存所有监听事件，键为通过实现`IEventMessage`接口的事件所定义的事件类的哈希值，值为以`IEventMessage`为参数的Action的双向链表组成。
​	**事件组（EventGroup）** 则是对`UniEvent`的进一步封装，字典`Dictionary<System.Type, List<Action<IEventMessage>>> _cachedListener`保存了事件组的订阅，事件组的订阅`AddListener`不仅添加到事件组的字典，还通过`UniEvent.AddListener`保存到事件系统。事件组的主要作用是提供了`RemoveAllListener`移除所有缓存的监听的功能。

​	用户可以**自定义事件类**通过`IEventMessage`接口定义自己的事件，一般有`public static void SendEventMessage(自定义参数)`对`UniEvent.SendMessage`的封装实现了事件的调用。

### 1.1.3. 初始化资源系统（YooAssets）
> <u>**资源系统（YooAsset/Runtime）**</u> 
>
> ​	**YooAssets.cs**
>
> ​	**YooAssetsDriver.cs**
>
> ​	***YooAssetsExtension.cs**（暂时没在这里用到）
>
> <u>**异步操作系统（YooAsset/Runtime/OperationSystem）**</u>
>
> ​	**AsyncOperationBase.cs**
>
> ​	**EOperationStatus.cs**
>
> ​	**GameAsyncOperation.cs**
>
> ​	**OperationSystem.cs**
>
> 

- <u>**YooAsset资源系统如何启动？**</u>

​	`YooAssets.Initialize()`通过创建`YooAssetsDriver：MonoBehaviour`作为**资源系统的驱动器**，驱动器驱动`OperationSystem.Update`。同时开启`OperationSystem._watch`（持续运行的计时器）用于计算帧耗时。

- **<u>如何实现资源系统的异步操作系统基类？</u>**

​	`AsyncOperationBase : IEnumerator, IComparable<AsyncOperationBase>`是**通用异步操作的抽象基类（状态、回调、等待机制）**。通过实现`IComparable`当`OperationSystem._newList`中出现`AsyncOperationBase.Priority` > 0的异步操作时，在当前所有暂存任务提交后会重新排序，以保证高优先级先被调度。通过实现`IEnumerator`以支持协程，让外部可以`yield return operation；`在Unity协程中等待其完成。实现的方法`MoveNext()`返回`！IsDone`，协程会持续等待直到状态变为完成。`Current`恒为空、`Reset()`空实现**符合协程使用的最小接口**。

​	`AsyncOperationBase.Task`是为了让异步操作支持`await`等待，类里额外暴露了`Task`（由 TaskCompletionSource 驱动）。如果正常的话在`AsyncOperationBase.SetFinish()`中会调用`_taskCompletionSource.TrySetResult(null);`让`await`等待结束或者在第一次获取`Task`时就因为`AsyncOperationBase.IsDone`为真而结束。（ps：await op（UniTask）来自`AsyncOperationBaseExtensions`，内部订阅 Completed，并在失败时抛异常，提供更“C# 异常流”友好体验。UniTask 是 Unity 友好的 await 替代方案，这里通过扩展把 `AsyncOperationBase` 直接变成可 `await` 的对象。过程有点复杂而且`AsyncOperationBaseExtensions`不是原版框架里面的...）。

- <u>**如何实现异步操作管理？**</u>

​	`OperationSystem`是`YooAsset`内部的**异步操作调度器**，负责：

1. 统一管理所有`AsyncOperationBase`派生的异步任务生命周期（开始、更新、结束、终止）。
2. 在每帧内分配时间片执行任务，避免长耗时操作阻塞主线程。
3. 处理新增任务的收集、按优先级排序、执行与清理。
4. 通过`OperationSystem.ClearPackageOperation`按包名批量终止任务。

​	`OperationSystem.List<AsyncOperationBase> _operations`是**当前活跃（已开始）的异步任务**，只有进入这个列表的任务才参与调度循环，完成后被移除。`OperationSystem.List<AsyncOperationBase> _newList`是**新提交的任务暂存区**，避免在遍历`_operations`时直接修改集合导致风险，同时可以一次性判断是否需要排序。`OperationSystem._frameTime`在每次Update时更新作为本帧的基本时间，用于判断是否消耗完时间片。`OperationSystem.IsBusy`用于记录当前时间片是否消耗完，消耗完则不在当前Update中更新异步操作。

​	`OperationSystem._startCallback`和`OperationSystem._finishCallback`都仅支持一个委托，会被后者覆盖。前者在有新任务，即调用`AsyncOperationBase.StartOperation()`时触发。后者在`OperationSystem.Update()`的移除阶段触发（当某操作在上一帧的`AsyncOperationBase.UpdateOperation()`中完成并设置了IsFinish）。

- **<u>异步操作是如何运行的？</u>**

​	`EOperationStatus`是**状态枚举类**，定义了异步操作的四个生命阶段：None -> Processing -> Succeed/Failed。状态的更新操作**一般**都在各个异步操作的`AsyncOperationBase.InternalUpdate()`，有些特殊情况如PatchOperation的SetFinish()在。`AsyncOperationBase.IsDone`取决于是否进入Succeed/Failed。`AsyncOperationBase.IsFinish`属于操作的内部标记。**区分**：`IsDone`是**任务本身条件达成**（状态成功或失败）`IsFinish`调度器已执行过收尾（确保`_callback`回调与`Task`已处理）。`AsyncOperationBase.UpdateOperation`中当`IsDone && IsFinish == false`为`true`时，将`IsFinish`置为`true`，同时调用回调`_callback`，设置进度`Progress = 1f;`，调用`_taskCompletionSource.TrySetResult(null);`结束`await opa.Task`。~~**补充区分**：yield return 等的是 IsDone（Succeed/Failed）；await op.Task 等的是 SetFinish()（已触发回调并收尾）。~~

​	除了`EOperationStatus`一般异步操作还会根据。实际情况定义**子枚举状态**`ESteps`。

​	`AsyncOperationBase`自定义`InternalStart`、`InternalUpdate`、`InternalAbort`方法来实现协程的生命周期：

- `YooAssets.StartOperation(GameAsyncOperation)` ⇒ `OperationSystem.StartOperation(string, AsyncOperationBase)` ⇒ `AsyncOperationBase.StartOperation()` ⇒ `AsyncOperationBase.InternalStart()`
- `OperationSystem.Update()` ⇒ `AsyncOperationBase.UpdateOperation()` ⇒ `AsyncOperationBase.InternalUpdate()`
- `OperationSystem.CleanPackageOperation(string)` ⇒ `AsyncOperationBase.AbortOperation()` ⇒ `AsyncOperationBase.InternalAbort()`

​	`AsyncOperationBase.UpdateOperation()`对异步操作更新的封装，除了执行更新任务`InternalUpdate`还进行其他操作。

​	`GameAsyncOperation：AsyncOperationBase`是针对**游戏业务侧**进一步包装的派生抽象类，是面向游戏逻辑的精简覆写入口。它把`InternalStart`、`InternalUpdate`、`InternalAbort`映射为更语义化的`OnStart`、`OnUpdate`、`OnAbort`，方便游戏逻辑在继承时只实现这三个方法，不需要接触内部调度接口命名。还提供`IsBusy()`让任务可以主动检测调度器是否时间片已满（避免重操作挤占帧）。

- **<u>问题&回答</u>**

1. 问题：为什么YooAsset的要采用OperationSystem这样的异步操作处理，不直接照搬GameManager.StartCoroutine的处理方法或者使用UniTask的异步方法？
	回答：核心原因：YooAsset 选用 OperationSystem（统一异步操作调度器），是为了解决资源系统运行期的“复杂异步流程”问题（初始化、版本请求、清单更新、下载、缓存清理、加载等），这些场景用原生 StartCoroutine 或直接换成 UniTask 都会出现扩展性、一致性和控制力不足。
2. 问题：
### 1.1.4. 加载更新页面（PatchWindow）
> PatchWindow.cs

`PatchWindow：MonoBehaviour`通过`Resources.Load`加载更新页面预制体，`GameObject.Instantiate`在Boot场景中生成该页面。`PatchWindow.Awake`获取UI组件、监听补丁事件（`PatchEventDefine`）。`MessageBox`是一个对话框封装类。`PatchWindow.List<MessageBox> _msgBoxList`可以看作一个mini的MessageBox对象池。

### 1.1.5. 开始补丁更新流程（PatchOperation）
> **PatchOperation.cs**
>
> <u>**状态机系统** （ThirdParty/UniMachine）</u>
>
> ​	**StateMachine.cs**
>
> ​	**IStateNode.cs**
>
> <u>**文件系统（YooAsset/Runtime/FileSystem/）**</u>
>
> ​	<u>**BundleResult [DIR]**</u>
>
> ​	<u>**DefaultBuildinFileSystem [DIR]**</u>
>
> ​	<u>**DefaultCacheFileSystem [DIR]**</u>
>
> ​	<u>**DefaultEditorFileSystem [DIR]**</u>
>
> ​	<u>**DefaultUnpackFileSystem [DIR]**</u>
>
> ​	<u>**DefaultWebRemoteFileSystem [DIR]**</u>
>
> ​	<u>**DefaultWebServerFileSystem [DIR]**</u>
>
> ​	<u>**Interface [DIR]**</u>
>
> ​	<u>**Operation [DIR]**</u>
>
> ​	<u>**WebGame [DIR]**</u>
>
> <u>**资源包系统（YooAsset/Runtime/ResourcePackage/）**</u>
>
> ​	<u>**Interface [DIR]**</u>
>
> ​		**IBundleQuery.cs**
>
> ​		**IPlayMode.cs**
>
> ​	**<u>Operation [DIR]</u>**
>
> ​		<u>**Internal [DIR]**</u>
>
> ​			**DeserializeManifestOperation.cs**
>
> ​		**ClearCacheFileOperation.cs**
>
> ​		**DestroyOperation.cs**
>
> ​		**DownLoaderOperation.cs**
>
> ​		**InitializationOperation.cs**
>
> ​		**PreDownLoadContentOperation.cs**
>
> ​		**RequestPackageVersionOperation.cs**
>
> ​		**UpdatePackageManifestOperation.cs**
>
> ​	**<u>PlayMode [DIR]</u>**
>
> ​		**EditorSimulateModeHelper.cs**
>
> ​		**PlayModeImpl.cs**
>
> ​	**AssetInfo.cs**
>
> ​	**BundleInfo.cs**
>
> ​	**EBuildBundleType.cs**
>
> ​	**EFileNameStyle.cs**
>
> ​	**ManifestDefine.cs**
>
> ​	**ManifestTools.cs**
>
> ​	**PackageBundle.cs**
>
> ​	**PackageDetail.cs**
>
> ​	**PackageManifest.cs**
>
> ​	**ResourcePackage.cs**
>
> <u>**AB包构建（YooAsset/Editor/AssetBundleBuilder）**</u>
>
> ​	<u>**BuildPipeLine [DIR]**</u>
>
> ​	<u>**BuildSystem [DIR]**</u>
>
> ​	<u>**VisualViewers [DIR]**</u>
>
> ​	**AssetBundleBuilder.cs**
>
> ​	**AssetBundleBuilderHelper.cs**
>
> ​	**AssetBundleBuilderSetting.cs**
>
> ​	**AssetBundleBuilderWindow.cs**
>
> ​	**AssetBundleSimulateBuilder.cs**
>
> ​	**BuildAssetInfo.cs**
>
> ​	**BuildMapContent.cs**
>
> ​	**BuildParameters.cs**
>
> ​	**BuildParametersContext.cs**
>
> ​	**DefaultEncryptionServices.cs**
>
> ​	**DefaultManifestServices.cs**
>
> ​	**EBuildinFileCopyOption.cs**
>
> ​	**EBuildPipeLine.cs**
>
> ​	**ECompressOption.cs**
>
> ​	**IBuildPipeline.cs**
>
> <u>**AB包收集器（YooAsset/Editor/AssetBundleCollector）**</u>
>
> ​	**<u>CollectRules [DIR]</u>**
>
> ​		**IActiveRule.cs**
>
> ​		**IAddressRule.cs**
>
> ​		**IFilterRule.cs**
>
> ​		**IIgnoreRule.cs**
>
> ​		**IPackRule.cs**
>
> ​	**<u>DefaultRules [DIR]</u>**
>
> ​		**DefaultActiveRule.cs**
>
> ​		**DefaultAddressRule.cs**
>
> ​		**DefaultFilterRule.cs**
>
> ​		**DefaultIgnoreRule.cs**
>
> ​		**DefaultPackRule.cs**
>
> ​	**AssetBundleCollector.cs** —— 真正干活的收集器。根据“收集路径 + 过滤规则 + 打包规则 + 寻址规则”等生成若干 CollectAssetInfo
>
> ​	**AssetBundleCollectorConfig.cs**
>
> ​	**AssetBundleCollectorGroup.cs** —— 对应 UI 里的“组”，包含若干 AssetBundleCollector
>
> ​	**AssetBundleCollectorPackage.cs** —— 对“一个包（Package）”的配置抽象，包含多个 AssetBundleCollectorGroup
>
> ​	**AssetBundleCollectorSettings.cs** ——（ScriptableObject）仅“保存配置”（有哪些 Package、每个 Package 下有哪些 Group、每个 Group 下有哪些 Collector，以及各类规则类名等），不保存“收集结果”。收集结果是在每次构建时临时计算出来的。
>
> ​	**AssetBundleCollectorSettingsData.cs**	
>
> ​	**AssetBundleCollectorWindow.cs**
>
> ​	**AssetDependencyCache.cs**
>
> ​	**AssetDependencyDatabase.cs**
>
> ​	**CollectAssetInfo.cs** ——（编辑器阶段的收集产物）
>
> ​	**CollectCommand.cs** —— 是一次“收集会话”的上下文与开关集合，这些开关被 Collector、Group、Package 的逻辑用来决定具体收集行为。
>
> ​	**CollectResult.cs**
>
> ​	**DisplayNameAttribute.cs**
>
> ​	**ECollectorType.cs**
>
> ​	**RuleDisplayName.cs**
>
> **YooAssetSettingsData.cs**

- **<u>补丁流程开始</u>**

​	`var operation = new PatchOperation("DefaultPackage", PlayMode);`创建一个补丁更新操作`PatchOperation：GameAsyncOperation`携带**资源包名**和**资源系统运行模式**保存在状态机的黑板中。注册监听用户交互事件（`UserEventDefine`），创建状态机同时设置该异步操作为持有者（保存对`PatchOperation`的引用）。

- **<u>如何实现一个状态机？</u>**

​	`Dictionary<string, IStateNode> _nodes`保存状态机中的节点。`Dictionary<string, System.Object> _blackboard`保存节点间的上下文（PackageName、PlayMode、PackageVersion、Downloader等），可以跨节点共享的临时数据（能“传值”）。`System.Object Owner`保存宿主对象引用，是行为与能力的提供者（能“做事”），面向方法调用与服务访问。（例：`PatchOperation.SetFinish()`）。

​	为确保每个状态的生命周期，采取了**预注册**的方法：`AddNode<TNode>() where TNode : IStateNode`通过反射`var stateNode = Activator.CreateInstance(nodeType) as IStateNode;`创建一个状态节点并加入到`_nodes`。因此`Run<TNode>() where TNode : IStateNode`、`ChangeState<TNode>() where TNode : IStateNode`都是通过在已注册的字典 `_nodes` 里查找实例并切换，不会创建节点。未注册就会报“找不到节点”，OnCreate 也不会被调用，节点拿不到 Owner，容易空引用。

​	节点的`Update()`都是通过外部生命周期的Update方法驱动的，补丁流程的状态机的生命周期是由异步操作控制的，最终可以溯源到`YooAssetsDriver`。

1. <u>**FsmInitializePackage : IStateNode**</u>


​	`PatchEventDefine.PatchStepsChange.SendEventMessage("初始化资源包！");`通知PatchWindow改变Tip

​	`StartCoroutine(InitPackage());`：获取黑板中的Play模式和包名，获取&创建资源包`ResourcePackage`。

- **<u>资源包的创建和获取</u>**

​	资源包不存在：`YooAsset.CreatePackage()` ⇒ `new ResourcePackage()` 。资源包存在就直接从`YooAsset._packages`里面获取。

- **<u>根据运行模式的不同对资源包进行初始化</u>**

​	1. FsmInitializePackage.cs

​	（1）**创建资源包裹类**。资源包`ResourcePackage`持有包名（2）`FsmInitializePackage`根据`InitializeParameters.cs`中定义的枚举类`EPlayMode`（运行模式）创建不同的**初始化参数类**，如**编辑器下模拟运行模式的初始化参数类（EditorSimulateModeParameters : InitializeParameters）**并对它对应的**文件系统参数类（EditorFileSystemParameters:FileSystemParameters）(实际上来自FileSystemParameters.cs的CreateDefaultEditorFileSystemParameters(string packageRoot)方法)**进行实例化。（`InitializeParameters.cs`中定义了不同运行模式的**初始化参数类**。ps：**`InitializeParameters` 是“启动配置”**，具体哪几个文件系统由它携带的 `FileSystemParameters` 决定。**`FileSystemParameters` 则是“创建某个 IFileSystem 的实参”**。）。然后传入不同运行模式的初始化参数`ResourcePackage.InitializeAsync(InitializeParameters):InitializationOperation`[^1]对资源包进行初始化

​	其中编辑器模式需要获取`packageRoot`并传给后续创建的文件系统`DefaultEditorFileSystem`保存。

​	a. EditorSimulateModeHelper.cs

​	调用`EditorSimulateModeHelper.SimulateBuild(packageName)`返回`PackageInvokeBuildResult`，其中`new PackageInvokeBuildParam(packageName);`创建一个“包构建调用描述（PackageInvokeBuildParam）”。它装的是“用反射调用哪个程序集/类/方法，以及走哪条管线”等元数据，供 `PackageInvokeBuilder` 在 Editor 下反射执行真正的构建入口。通过`return PackageInvokeBuilder.InvokeBuilder(PackageInvokeBuildParam buildParam);`调用Editro类来执行构建资源包任务，在`InvokeBuilder`中通过自定义的反射调用方法`InvokePublicStaticMethod(System.Type type, string method, params object[] parameters)`反射调用我们在`PackageInvokeBuildParam`中预先设定的方法`AssetBundleSimulateBuilder.SimulateBuild`。

​	b. AssetBundleSimulateBuilder.cs

​	（1) **组装构建参数BuildParameters**（用于“模拟管线”）,`new EditorSimulateBuildParameters();`，包括但不限于“BuildOutputRoot（清单输出根目录）”、“BuildinFileRoot（StreamingAssets 根路径）”、“BuildBundleType（使用 VirtualBundle，清单中不指向真实AB，而是指向工程内 AssetPath）”等。（2) **运行模拟构建管线**：**生成清单（.version/.hash/.bytes）到输出目录**。创建模拟构建管线`new EditorSimulateBuildPipeline();`并运行`BuildResult buildResult = pipeline.Run(buildParameters, false);`。`IBuildPipeline.Run()` 会创建`var builder = new AssetBundleBuilder();`并调用`return builder.Run(buildParameters, GetDefaultBuildPipeline(), enableLog);`其中`builder.Run`会调用`BuildRunner.Run`。真正的 AB 构建统一由 **AssetBundleBuilder** + **BuildRunner** 执行，**任务链来自 `IBuildPipeline.GetDefaultBuildPipeline()`**，上下文由`AssetBundleBuilder._buildContext` （**BuildContext**：**跨任务共享上下文容器**。）持有（如 **BuildParametersContext**（对 BuildParameters 的包装，提供统一目录解析（输出根、包根、StreamingAssets 根等））、BuildMapContext （由 TaskGetBuildMap_* 产出，承载收集器（Collector）筛选后的“要打进包”的资产图谱）等）。以下是EditorSimulateBuildPipeline.GetDefaultBuildPipeline()中的IBuildTask。通过

​	1）TaskPrepare_ESBP：检测基础构建参数是否合法。

​	2）TaskGetBuildMap_ESBP：调用`TaskGetBuildMap.CreateBuildMap(true, buildParametersContext.Parameters);`生成**资源构建上下文（BuildMapContext）**。**<u>1.</u>** **获取所有收集器收集的资源**，通过AssetDatabase.FindAssets获取到`AssetBundleCollectorSettingData.Setting`，调用`BeginCollect(packageName, simulateBuild, useAssetDependencyDB):CollectResult`方法收集指定包裹的资源文件。在检测配置合法性（通过文件名获取包裹类AssetBundleCollectorPackage后检查配置错误）、和创建资源忽略规则后，创建资源收集命令`new CollectCommand(packageName, ignoreRule);`。调用`AssetBundleCollectorPackage.GetCollectAssets(command):List<CollectAssetInfo>`获取收集的资源列表，最后用`List<CollectAssetInfo>`创建`CollectResult`。**<u>2.</u>** **剔除未被引用的依赖项资源**。**<u>3.</u>** **录入所有收集器主动收集的资源**。**<u>4.</u>** **录入所有收集资源依赖的其它资源**。**<u>5.</u>** **填充所有收集资源的依赖列表**。**<u>6.</u>** **自动收集所有依赖的着色器**。**<u>7.</u>** **计算共享资源的包名**。**<u>8.</u>** **记录关键信息**。**<u>9.</u>** **移除不参与构建的资源**。**<u>10.</u>** **构建资源列表**。（太复杂了先不搞这块了...）

​	3）TaskUpdateBundleInfo_ESBP：调用`TaskUpdateBundleInfo.UpdateBundleInfo(BuildContext)`。**<u>1.</u>** **检测文件名长度**。**<u>2.</u>** **更新构建输出的文件路径**。**<u>3.</u>** **更新文件其它信息**。**<u>4.</u>** **更新补丁包输出的文件路径**。

​	4）TaskCreateManifest_ESBP：创建补丁清单文件到输出目录。调用`TaskCreateManifest.CreateManifestFile()`

​	2. ResourcePackage.cs

​	（1）`ResourcePackage.InitializeAsync(InitializeParameters)` 调用`ResourcePackage.ResetInitializeAfterFailed`用于“失败后重试初始化”的自我修复。它在上一次初始化失败时，清掉初始化锁与错误标记，让后续操作可以再次执行。（2）**检测初始化参数合法性**，调用`ResourcePackage.CheckInitializeParameters(InitializeParameters)`在检测合法性的同时根据传入的`InitializeParameters`类型对`ResourcePackage._playMode`进行赋值。（3）**创建资源管理器**（如果存在资源管理器就删除），根据包名和运行模式创建`PlayeModeImpl:IPlayMode,IBundleQuery`（内部管理若干 IFileSystem，并根据 Bundle 归属选择用哪个文件系统干活。）后对**资源管理器初始化**`_resourceManager.Initialize(parameters, _bundleQuery);`其中IPlayMode（运行模式策略接口）围绕“包”的运行模式（Editor/Offline/Host/Web/Custom）提供文件系统的生命周期与清单/版本/下载等操作。IBundleQuery（包查询接口）把 AssetInfo 映射到对应的主包/依赖包，并给出包名。底层基于 ActiveManifest 和文件系统判断。同时**保存对`PlayeModeImpl`的引用**。（4）**初始化资源系统**，根据运行模式将`InitializeParameters`拆成对应的`FileSystemParameters`传给`PlayModeImpl`，**调用`PlayModeImpl.InitializeAsync(FileSystemParameters)`创建一个初始化异步操作**`InitializationOperation initializeOperation;`，**返回后通过`OperationSystem.StartOperation(PackageName, initializeOperation);`加入生命周期**、**添加完成时回调**并**返回到`FsmInitializePackage`中等待完成**。（ps：`InitializeParameters` 是“启动配置”，具体哪几个文件系统由它携带的 `FileSystemParameters` 决定。`FileSystemParameters` 则是“创建某个 IFileSystem 的实参”。）

​	3. PlayModeImpl.cs

​	`PlayModeImpl.InitializeAsync`将传入的文件系统参数加入`fileSystemParamList`（这里的`fileSystemParamList`只是一个**临时创建的文件系统参数列表**用于统一将文件系统参数交给`InitializationOperation`，`List<IFileSystem> FileSystems`是“注册中心”：后续需要用它来判断某个 Bundle 归属于哪个文件系统、统一销毁、按顺序决策主文件系统。文件系统的注册在`InitializationOperation`中完成）[^1]。调用`InitializeAsync(List<FileSystemParameters> )`创建一个**初始化异步操作（`InitializationOperation`）**返回给ResourcePackage。

​	4. InitializationOperation.cs

​	创建`InitializationOperation`需要**保存对`PlayModeImpl`的引用**，同时获取`PlayModeImpl.fileSystemParameterList`作为**创建事件系统的实参集**`List<FileSystemParameters> _parametersList`（将创建的异步操作**返回到ResourcePackage加入到生命周期进行启动**`OperationSystem.StartOperation(PackageName, initializeOperation);`添加完成时回调并**返回FsmInitializePackage等待操作完成**）。在`InitializationOperation.InternalUpdate()`中：1）首先会检验`_parametersList`中的文件系统参数是否合法，2）然后将文件系统参数复制到`_cloneList`并可清除`PlayModeImpl`中可能会残存的一些旧文件系统，3）接着**逐个创建 FS实例** 并创建**文件系统的初始化异步操作（FSInitializeFileSystemOperation : AsyncOperationBase）**，并将这些异步操作作为`InitializationOperation`的子操作，从而将每个文件系统的初始化操作通过父操作`InitializationOperation`加入生命周期（`FSInitializeFileSystemOperation`是所有文件系统的异步初始化操作**抽象基类**）。例如：**模拟文件系统（DefaultEditorFileSystem：IFileSystem）**的初始化异步操作是`DEFSInitializeOperation：FSInitializeFileSystemOperation`。

​	5. FileSystemParameters.cs

​	文件系统实例的创建方法`IFileSystem CreateFileSystem(string packageName)`，其中**文件系统类**的**实例**是通过**保存在文件系统参数中的FileSystemClass类型的名字的字符串**反射进行创建的。`FsmInitializePackage`中的文件系统参数`FileSystemParameters`的创建就用到了默认创建的**文件系统参数类（FileSystemParameters）**（例如默认的编辑器**文件系统参数类（EditorFileSystemParameters）**，是通过`FileSystemParameters.CreateDefaultEditorFileSystemParameters(string packageRoot)：FileSystemParameters`创建 ，通过**类型的名字的字符串保存**对应的**文件系统类**：`string fileSystemClass = typeof(DefaultEditorFileSystem).FullName;`）。最终**FsmInitializePackage的初始化异步操作initializationOperation**的状态跟多个文件系统初始化异步操作的状态有关。

[^1]: 这样设计的好处：运行模式（Editor/Offline/Host/Web）通过“组合”不同的 FS（内置/缓存/远端）形成策略，查询时由 Belong/NeedDownload/NeedUnpack/NeedImport 等判定落到哪个 FS。

2. <u>**FsmRequestPackageVersion**</u>

# 2. 雪不言的改版（以下简称“雪版”）
## 2.1. 和原版YooAssets框架的差异
### 2.1.1. 资源系统（YooAsset）
​	雪版有方法`AsyncOperationBase.SetFinish`（将`IsFinish`置为`true`，同时调用回调`_callback`，设置进度`Progress = 1f;`，调用`_taskCompletionSource.TrySetResult(null);`结束`await opa.Task`。），但原版将这些操作移到`AsyncOperationBase.UpdateOperation`中进行，原版在具体的异步操作实例（如：PatchOperation）中实现了一个`SetFinish`来`Status = EOperationStatus.Succeed;`，雪版则在`AsyncOperationBase.InternalOnUpdate`中按逻辑进行设置。**简而言之**，原版中真正把回调等收尾做完的是基类的`AsyncOperationBase.UpdateOperation`由生命周期控制。雪版将这些操作封装在`AsyncOperationBase.SetFinish`交给`OperationSystem.Update()`处理。

​	在协程生命周期`InternalStart`、`InternalUpdate`、`InternalAbort`的调用上，对于`InternalStart`，雪版将`AsyncOperationBase.StartOperation()`更名为`AsyncOperationBase.SetStart()`，并修改部分。对于`InternalUpdate`，雪版将`AsyncOperationBase.UpdateOperation()`方法进行拆分，`InternalUpdate`的调用在`OperationSystem.Update()`，其余逻辑封装在`AsyncOperationBase.SetFinish`。对于`InternalAbort`，雪版将`AsyncOperationBase.AbortOperation()`更名为`AsyncOperationBase.SetAbort()`，移除终止子任务。

# 3. HybridCLR及Unity编辑器底层原理

## 3.1. Scripting Backend

> **Scripting Backend（脚本后端）**是指Unity 用来将 C# 脚本编译并运行在目标平台上的底层运行时系统。它决定了你的 C# 代码如何被编译、执行，以及与原生平台（如 iOS、Android、Windows 等）交互的方式。

### 3.1.1. Mono
