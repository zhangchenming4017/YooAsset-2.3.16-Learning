# 补丁更新前的准备阶段（Boot → UniEvent → YooAssets/OperationSystem → PatchOperation）

> 适用版本：本项目 YooAsset 2.3.16
>
> 目标：梳理**开始补丁更新流程之前**的所有关键步骤和参与者，回答：
> - Boot 里每一行初始化代码到底干了什么？
> - 事件系统 / 资源系统 / 异步系统 / 状态机之间怎么串起来？
> - AsyncOperationBase / OperationSystem 在补丁流程里扮演什么角色？
> - 为什么这些系统都要依赖一个 MonoBehaviour 驱动器（GameObject + Update）？
>
> 本文只讲“准备阶段”和“骨架”：
> - 不展开具体 FSM 状态节点（如 `FsmInitializePackage` 的内部细节），那部分放在单独文档里讲；
> - 这里只要搞清楚：**是谁驱动谁、数据存在哪、生命周期如何衔接**，以及这些设计背后的“用意”。
>
> 建议配合同目录下的：
> - `Editor/AssetBundleBuilder/README_PackagesAndRuntime.md`
> - `Editor/AssetBundleBuilder/README_AssetDataFlow.md`
> - `Editor/AssetBundleCollector/README_AssetTag.md`

相关文件：

- `Assets/AOT&Framework/Boot.cs`
- `Assets/AOT&Framework/GameManager.cs`
- `Assets/AOT&Framework/ThirdParty/UniFramework/UniEvent/Runtime/UniEvent.cs`
- `Assets/AOT&Framework/ThirdParty/UniFramework/UniEvent/Runtime/UniEventDriver.cs`
- `Assets/AOT&Framework/ThirdParty/UniFramework/UniEvent/Runtime/EventGroup.cs`
- `Assets/AOT&Framework/YooAsset/Runtime/YooAssets.cs`
- `Assets/AOT&Framework/YooAsset/Runtime/YooAssetsDriver.cs`
- `Assets/AOT&Framework/YooAsset/Runtime/OperationSystem/AsyncOperationBase.cs`
- `Assets/AOT&Framework/YooAsset/Runtime/OperationSystem/GameAsyncOperation.cs`
- `Assets/AOT&Framework/YooAsset/Runtime/OperationSystem/OperationSystem.cs`
- `Assets/AOT&Framework/ThirdParty/UniFramework/UniMachine/Runtime/StateMachine.cs`
- `Assets/AOT&Framework/PatchLogic/PatchOperation.cs`

---

## 1. Boot.Start 的整体路径

Boot 是游戏启动入口，`Start()` 协程的关键逻辑（删去热更 DLL 细节）如下：

```csharp
IEnumerator Start()
{
    // 1) 游戏管理器
    GameManager.Instance.Behaviour = this;

    // 2) 初始化事件系统
    UniEvent.Initalize();

    // 3) 初始化资源系统
    YooAssets.Initialize();

    // 4) 加载更新页面 UI
    var go = Resources.Load<GameObject>("PatchWindow");
    GameObject.Instantiate(go);

    // 5) 启动补丁流程（状态机包裹在一个 GameAsyncOperation 里）
    var operation = new PatchOperation("DefaultPackage", PlayMode);
    YooAssets.StartOperation(operation);
    yield return operation; // 等待补丁流程结束

    // 6) 设置默认包
    gamePackage = YooAssets.GetPackage("DefaultPackage");
    YooAssets.SetDefaultPackage(gamePackage);

    // 7) （示例）加载热更 DLL，最后切换场景
    yield return LoadDlls();
    SceneEventDefine.ChangeToHomeScene.SendEventMessage();
}
```

可以粗略分为三段：

1. **准备环境**：GameManager / 事件系统 / 资源系统；
2. **跑补丁状态机**：`PatchOperation`（一个 `GameAsyncOperation`，内部挂着 `StateMachine`）；
3. **补丁完成后开始正式游戏**：设置默认包、加载 DLL、切场景。

下面从 **GameManager → UniEvent → YooAssets/OperationSystem → PatchOperation/StateMachine** 依次说明，并顺带解释这些设计背后的原因。

---

## 2. GameManager：为其他系统提供协程/行为宿主

`GameManager` 在本项目中是一个全局单例，暴露 `Behaviour` 属性：

```csharp
GameManager.Instance.Behaviour = this; // this = Boot
```

用途：

- 让非 MonoBehaviour 代码（例如一些工具/框架）能统一从 `GameManager.Instance.Behaviour` 发起协程；
- 允许集中监听 Unity 生命周期事件（如场景切换），并转发给其他系统。

### 2.1 发起协程的例子

很多框架代码并不是 MonoBehaviour，例如补丁状态机中的某些节点，只能通过 GameManager 来发起协程：

```csharp
// 某个 FSM 节点中
GameManager.Instance.StartCoroutine(InitPackage());
```

这样做的好处：

- 框架核心（补丁逻辑、YooAsset 等）可以完全独立于具体场景，不需要直接依赖某个场景上的脚本；
- 只要 GameManager 的 Behaviour 绑定在一个“常驻物体”（Boot）上，就能跨场景持续调度协程。

### 2.2 监听场景事件的例子

类似地，GameManager 也可以作为**场景事件的集中监听点**，例如：

```csharp
public class GameManager
{
    public MonoBehaviour Behaviour { get; set; }

    public void HookSceneLoaded()
    {
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // 统一转发给其他系统，例如发送一个“场景切换完毕”事件
        SceneEventDefine.SceneLoaded.SendEventMessage(scene.name);
    }
}
```

Boot 初始化时只要调用一次 `GameManager.Instance.HookSceneLoaded()`，后续任何场景加载都能在 GameManager 中被感知并转发。

> 这两类需求（协程 + 场景事件）都是“需要 MonoBehaviour 的生命周期”，通过 GameManager 集中托管，就避免了到处 new/挂脚本、业务代码直接依赖某个场景物体的耦合问题。

---

## 3. UniEvent：事件系统的数据结构、驱动器与广播过程

入口：

```csharp
UniEvent.Initalize();
```

实现文件：`UniEvent.cs` + `UniEventDriver.cs`。

### 3.1 Initalize & UniEventDriver：为什么需要一个驱动器？

```csharp
public static void Initalize()
{
    if (_isInitialize)
        throw new Exception($"{nameof(UniEvent)} is initialized !");

    if (_isInitialize == false)
    {
        _isInitialize = true;
        _driver = new GameObject("[UniEvent]");
        _driver.AddComponent<UniEventDriver>();
        Object.DontDestroyOnLoad(_driver);
        UniLogger.Log("UniEvent initalize !");
    }
}
```

`UniEventDriver` 的核心代码类似：

```csharp
public class UniEventDriver : MonoBehaviour
{
    private void Update()
    {
        UniEvent.Update();
    }
}
```

也就是说：

- 事件系统本身是一个静态类，内部只有数据结构 `_listeners/_postingList`；
- 要想实现“延迟在下一帧派发”的功能，就必须有一个**每帧被调用的入口** → 只能靠 MonoBehaviour.Update；
- `UniEvent.Initialize` 动态创建了驱动器 `[UniEvent]`，并标记 `DontDestroyOnLoad`，从而在任何场景下都能持续驱动事件系统。

> 一个经验点：
> - 所有“静态管理器”一旦需要依赖 Update/协程/场景回调，都会需要这样一个“驱动器 MonoBehaviour”;
> - 本项目中：事件系统有 `UniEventDriver`，资源/异步系统有 `YooAssetsDriver`，两者设计是一致的。

### 3.2 事件是如何保存的？（_listeners）

`UniEvent` 内部关键字段：

```csharp
// 监听表：键 = 事件ID，值 = 监听该事件的回调列表
private static readonly Dictionary<int, LinkedList<Action<IEventMessage>>> _listeners
    = new Dictionary<int, LinkedList<Action<IEventMessage>>>(1000);

// 延迟发送队列
private static readonly List<PostWrapper> _postingList = new List<PostWrapper>(1000);
```

- 事件“类型”本质上是一个 `Type`：

  ```csharp
  public static void AddListener<TEvent>(Action<IEventMessage> listener)
      where TEvent : IEventMessage
  {
      Type eventType = typeof(TEvent);
      int eventId = eventType.GetHashCode();
      AddListener(eventId, listener);
  }
  ```

- `eventId` = `事件类型.GetHashCode()`，作为事件的唯一标识；
- `_listeners[eventId]` 是一个 `LinkedList<Action<IEventMessage>>`：
  - 允许多个回调订阅同一事件；
  - 使用链表，方便在遍历时插入/删除节点。

添加/移除监听的代码前文已经列出，不再重复。

### 3.3 事件组 EventGroup 是如何保存的？（_cachedListener）

`EventGroup` 是对 `UniEvent` 的一层封装，方便打包和清理一组监听：

```csharp
internal readonly Dictionary<Type, List<Action<IEventMessage>>> _cachedListener
    = new Dictionary<Type, List<Action<IEventMessage>>>();
```

- `EventGroup.AddListener<TEvent>(listener)`：
  1. 先调用 `UniEvent.AddListener<TEvent>(listener)`，把回调注册到全局事件系统；
  2. 再把 `listener` 存进 `_cachedListener[typeof(TEvent)]`；
- `EventGroup.RemoveAllListener()`：
  - 遍历 `_cachedListener` 中的所有 `(Type, List<listener>)`；
  - 调用 `UniEvent.RemoveListener` 一一从全局事件系统注销；
  - 最后清空 `_cachedListener`。

在补丁流程里：

```csharp
private readonly EventGroup _eventGroup = new EventGroup();

public void SetFinish()
{
    _steps = ESteps.Done;
    _eventGroup.RemoveAllListener(); // 一次性移除所有补丁相关事件监听
    Status = EOperationStatus.Succeed;
}
```

> 小结：
> - `_listeners` 是**全局事件总线**的“事件ID → 回调链表”字典；
> - `EventGroup._cachedListener` 是“**当前逻辑块**订阅了哪些事件”的本地缓存，用来实现“一键清理”;
> - `UniEventDriver` 则是整个事件系统能运转起来的**时间轮（tick）**，负责每帧调用 `UniEvent.Update()`。

### 3.4 事件是如何广播调用的？

即时广播（`SendMessage`）与延迟广播（`PostMessage`）逻辑前文已给出，这里只强调**设计动机**：

- `SendMessage`：适合“立刻反应”的场景（例如某个 FSM 节点内部发送一个进度变更事件给 UI）；
- `PostMessage`：适合“先完成当前逻辑，再在下一帧对外广播”的场景：
  - 比如某个复杂逻辑需要在一帧内多次修改内部状态，等稳定后再通知 UI；
  - 或者希望避免“事件回调中又修改当前集合”这类嵌套修改问题。

### 3.5 事件类型是如何定义的？

事件类型只需要实现 `IEventMessage` 接口，例如：

```csharp
public class UserTryInitialize : IEventMessage
{
    public static void SendEventMessage()
    {
        UniEvent.SendMessage(new UserTryInitialize());
    }
}
```

- 订阅方：

  ```csharp
  _eventGroup.AddListener<UserEventDefine.UserTryInitialize>(OnHandleEventMessage);
  ```

- 触发方（通常是 UI 脚本）：

  ```csharp
  UserEventDefine.UserTryInitialize.SendEventMessage();
  ```

> 这样，**补丁流程 UI 和后台状态机之间的通信**就通过 UniEvent 完成：
> - UI Button 点击 → 发送 `UserTryInitialize`；
> - `PatchOperation.OnHandleEventMessage` 收到后 → 根据事件类型切换状态机节点。

---

## 4. YooAssets 与 OperationSystem：统一的异步执行环境

入口：

```csharp
YooAssets.Initialize();
```

实现文件：`YooAssets.cs` + `YooAssetsDriver.cs` + `OperationSystem` 系列。

### 4.1 AsyncOperationBase：所有异步操作的共同基类

文件：`Runtime/OperationSystem/AsyncOperationBase.cs`。

职责：为 YooAsset 内部和游戏业务侧提供一个**统一的异步操作抽象**，具备：

- 可被 `yield return operation` 等待（实现 `IEnumerator`）;
- 有统一的状态机（`EOperationStatus` + 自定义 `ESteps`，通常在子类中定义 `_steps`）;
- 有统一的回调与 `Task` 支持（可用于 `await` 场景——这部分可由扩展方法封装）。

关键字段与属性（简化）：

```csharp
public abstract class AsyncOperationBase : IEnumerator, IComparable<AsyncOperationBase>
{
    public EOperationStatus Status { protected set; get; } // None / Processing / Succeed / Failed
    public float Progress { protected set; get; }
    public string Error  { protected set; get; }

    // 调度器内部使用
    internal bool IsFinish { get; private set; }

    // 外部协程使用
    public bool IsDone => Status == EOperationStatus.Succeed || Status == EOperationStatus.Failed;

    public bool MoveNext() => !IsDone;
    public void Reset() { }
    public object Current => null;

    internal void StartOperation()  => InternalOnStart();
    internal void UpdateOperation() => InternalOnUpdate();
    internal void AbortOperation()  => InternalOnAbort();

    protected abstract void InternalOnStart();
    protected abstract void InternalOnUpdate();
    protected abstract void InternalOnAbort();
}
```

几个概念：

- `Status`：
  - `None`：尚未开始；
  - `Processing`：正在执行；
  - `Succeed/Failed`：逻辑上已经结束；
- `IsDone`：对协程/业务来说，“这个任务算完成了吗？”;
- `IsFinish`：对调度器来说，“是否已经做完收尾操作？（回调、Task 通知等）”。

> 这层抽象的意义：
> - 让 **所有类型的异步任务**（补丁、清单加载、下载、资源加载等）
>   - 在接口层是统一的；
>   - 都可以被 `yield return` 等待；
>   - 都可以被同一个 `OperationSystem` 统一管理。

### 4.2 OperationSystem：统一调度所有 AsyncOperationBase

文件：`Runtime/OperationSystem/OperationSystem.cs`。

职责：

- 保存并调度所有异步操作；
- 控制每帧执行的时间片，避免某个任务一帧内执行过久卡顿；
- 支持按包名清理任务（YooAsset 内部部分操作是按包分类的）。

关键字段（简化）：

```csharp
private static readonly List<AsyncOperationBase> _operations = new List<AsyncOperationBase>(1000);
private static readonly List<AsyncOperationBase> _newList    = new List<AsyncOperationBase>(1000);

internal static long MaxTimeSlice = 30; // 每帧最多消耗 30ms

private static Stopwatch _watch;
private static long _frameTime;
```

启动一个操作：

```csharp
public static void StartOperation(string packageName, AsyncOperationBase operation)
{
    _newList.Add(operation); // 记录新任务，真正开始在下一帧 Update 中
}
```

每帧更新（概要）：

```csharp
internal static void Update()
{
    _frameTime = _watch.ElapsedMilliseconds;

    // 1) 迁移 _newList 到 _operations，并调用 StartOperation()
    // 2) 遍历 _operations 调用 UpdateOperation()，直到时间片耗尽或任务遍历完；
    // 3) 对 IsDone 且尚未 Finish 的任务执行收尾（回调/Task 通知/从列表移除）。
}
```

> 设计动机：
> - Unity 自带协程只是“语法糖 + 调度队列”，缺少**统一的优先级/时间片/归类管理**；
> - YooAsset 自己维护一个 OperationSystem：
>   - 可以让所有资源相关异步处于可控状态；
>   - 比如某帧下载任务很多，也可以通过 MaxTimeSlice 保护主线程；
>   - 需要清理某个包时，可以通过包名一次性终止相关任务。

### 4.3 GameAsyncOperation：给游戏侧用的异步基类

文件：`Runtime/OperationSystem/GameAsyncOperation.cs`。

它继承自 `AsyncOperationBase`，把内部的三个抽象方法包装成更直观的 `OnStart/OnUpdate/OnAbort` 接口，供游戏逻辑覆写：

```csharp
protected sealed override void InternalOnStart()  => OnStart();
protected sealed override void InternalOnUpdate() => OnUpdate();
protected sealed override void InternalOnAbort()  => OnAbort();

protected abstract void OnStart();
protected abstract void OnUpdate();
protected abstract void OnAbort();
```

- `PatchOperation` 就是一个典型的 `GameAsyncOperation`：
  - `OnStart` 启动补丁状态机；
  - `OnUpdate` 驱动状态机；
  - `OnAbort` 可用于中断补丁（当前实现中为空实现）。

### 4.4 YooAssets.Initialize & YooAssetsDriver：资源/异步系统的驱动器

关键代码：

```csharp
public static void Initialize(ILogger logger = null)
{
    if (_isInitialize)
    {
        Debug.LogWarning("YooAssets is initialized !");
        return;
    }

    YooLogger.Logger = logger;

    _isInitialize = true;
    _driver = new GameObject("[YooAssets]");
    _driver.AddComponent<YooAssetsDriver>();
    Object.DontDestroyOnLoad(_driver);

    OperationSystem.Initialize();
}
```

`YooAssetsDriver`：

```csharp
public class YooAssetsDriver : MonoBehaviour
{
    private void Update()
    {
        YooAssets.Update();
    }
}

internal static void Update()
{
    if (_isInitialize)
        OperationSystem.Update();
}
```

- `YooAssetsDriver` 和 `UniEventDriver` 类似，都是一个“静态系统的时间驱动器”;
- 资源系统本身只是一堆静态类 + 普通对象，要想让所有异步操作每帧推进，只能借助一个 MonoBehaviour.Update;
- 把驱动器挂在 `[YooAssets]` 物体上并标记 `DontDestroyOnLoad`，避免场景切换带来中断。

> 设计上的统一性：
> - 事件系统有 `[UniEvent] + UniEventDriver.Update()`；
> - 资源/异步系统有 `[YooAssets] + YooAssetsDriver.Update()`；
> - GameManager 自身也可以挂一些与业务相关的 Update/场景回调；
> - 所有“纯 C# 管理器”都通过一个统一的“宿主 GameObject”与 Unity 生命周期衔接。

### 4.5 YooAssets.StartOperation：把补丁流程挂到 OperationSystem

在 Boot 中：

```csharp
var operation = new PatchOperation("DefaultPackage", PlayMode);
YooAssets.StartOperation(operation);
```

`YooAssets.StartOperation`：

```csharp
public static void StartOperation(GameAsyncOperation operation)
{
    // 游戏业务逻辑不区分包名，传空字符串
    OperationSystem.StartOperation(string.Empty, operation);
}
```

时序：

- 当前帧：`PatchOperation` 进入 `_newList`，尚未开始；
- 下一帧的 `OperationSystem.Update()`：
  - 把它迁移到 `_operations`；
  - 调用 `PatchOperation.InternalOnStart()` → 即 `OnStart()`；
  - 后续每帧调用 `PatchOperation.InternalOnUpdate()` → 即 `OnUpdate()`；
- Boot 的 `yield return operation` 会持续等待到 `operation.IsDone` 为真（状态变为 `Succeed/Failed`）。

> 这样，“补丁流程”就变成了一个标准的 YooAsset 异步操作，和其它资源相关操作处在一个统一的执行环境中。

---

## 5. PatchOperation 与 UniMachine.StateMachine：一层异步包装 + 一棵状态树

实现文件：`PatchOperation.cs` + `StateMachine.cs`。

### 5.1 StateMachine：状态机骨架与“预注册”设计

文件：`ThirdParty/UniFramework/UniMachine/Runtime/StateMachine.cs`。

关键成员：

```csharp
private readonly Dictionary<string, object> _blackboard = new Dictionary<string, object>(100);
private readonly Dictionary<string, IStateNode> _nodes   = new Dictionary<string, IStateNode>(100);
private IStateNode _curNode;
private IStateNode _preNode;

public object Owner { private set; get; }
```

**预注册机制：**

```csharp
public void AddNode<TNode>() where TNode : IStateNode
{
    var nodeType = typeof(TNode);
    var stateNode = Activator.CreateInstance(nodeType) as IStateNode;
    AddNode(stateNode);
}

public void AddNode(IStateNode stateNode)
{
    var nodeType = stateNode.GetType();
    var nodeName = nodeType.FullName;

    if (_nodes.ContainsKey(nodeName) == false)
    {
        stateNode.OnCreate(this);  // 这里节点拿到 StateMachine（进而拿到 Owner 和 Blackboard）
        _nodes.Add(nodeName, stateNode);
    }
}
```

- 所有状态节点在构造状态机时一次性创建好并调用 `OnCreate`：
  - 节点可以在 `OnCreate` 中缓存引用（Owner / StateMachine / Blackboard）并做初始化；
  - 避免在状态切换过程中频繁 new 对象，降低 GC 和不确定性；
- 之后 `Run/ChangeState` 只是从 `_nodes` 字典中拿已有实例，并调用 `OnEnter/OnExit/OnUpdate`：

  ```csharp
  public void Run<TNode>() where TNode : IStateNode
  {
      _curNode = TryGetNode(typeof(TNode).FullName);
      _preNode = _curNode;
      _curNode.OnEnter();
  }

  public void ChangeState<TNode>() where TNode : IStateNode
  {
      var node = TryGetNode(typeof(TNode).FullName);
      _preNode = _curNode;
      _curNode.OnExit();
      _curNode = node;
      _curNode.OnEnter();
  }

  public void Update()
  {
      _curNode?.OnUpdate();
  }
  ```

> 预注册的意义：
> - 所有状态节点的生命周期是“整个状态机存在期”，不会在中途动态创建/销毁；
> - 节点之间可以通过 Blackboard 安全共享上下文对象（不用担心一个节点刚 new 出来还没初始化完就被使用）;
> - 更容易调试：无论当前在哪个状态，所有节点实例都存在于 `_nodes` 中，可以随时检查其内部字段。

### 5.2 PatchOperation：把状态机装进一个 GameAsyncOperation

关键部分回顾：

```csharp
public class PatchOperation : GameAsyncOperation
{
    private enum ESteps { None, Update, Done }
    private readonly EventGroup   _eventGroup = new EventGroup();
    private readonly StateMachine _machine;
    private readonly string       _packageName;
    private ESteps _steps = ESteps.None;

    public PatchOperation(string packageName, EPlayMode playMode)
    {
        _packageName = packageName;

        // 注册 UI / 用户交互事件
        _eventGroup.AddListener<UserEventDefine.UserTryInitialize>(OnHandleEventMessage);
        // ... 省略若干事件

        // 创建状态机，Owner = this
        _machine = new StateMachine(this);
        _machine.AddNode<FsmInitializePackage>();
        _machine.AddNode<FsmRequestPackageVersion>();
        // ... 其他 FsmXXX 节点预注册

        // 写入黑板（跨状态共享上下文）
        _machine.SetBlackboardValue("PackageName", packageName);
        _machine.SetBlackboardValue("PlayMode",  playMode);
    }

    protected override void OnStart()
    {
        _steps = ESteps.Update;
        _machine.Run<FsmInitializePackage>(); // 进入第一个状态
    }

    protected override void OnUpdate()
    {
        if (_steps == ESteps.Update)
            _machine.Update();
    }

    public void SetFinish()
    {
        _steps = ESteps.Done;
        _eventGroup.RemoveAllListener();
        Status = EOperationStatus.Succeed; // 通知外部：补丁异步操作已完成
        Debug.Log($"Package {_packageName} patch done !");
    }

    private void OnHandleEventMessage(IEventMessage message)
    {
        // 根据 UserEvent 切换不同的 FsmXXX 状态
        // 例如：UserTryUpdatePackageManifest → 切到 FsmUpdatePackageManifest
    }
}
```

结合前面几节，可以得到这样一条链路：

- Boot 创建 `PatchOperation` 并通过 `YooAssets.StartOperation` 提交给 `OperationSystem`；
- 下一帧 `OperationSystem.Update()` 调用 `PatchOperation.OnStart()`：
  - 状态机进入 `FsmInitializePackage`；
- 后续每帧 `OperationSystem.Update()` 调用 `PatchOperation.OnUpdate()`：
  - 状态机根据当前节点逻辑决定是否切换到下一个状态（版本请求 / 清单更新 / 创建下载器等）;
- UI 通过 `UniEvent` 发送 `UserEventDefine.*` 事件：
  - 被 `PatchOperation.OnHandleEventMessage` 收到后驱动状态机切换；
- 当补丁流程整体结束时（通常在某个 `FsmXXX` 节点内部）：
  - 调用 `PatchOperation.SetFinish()`：
    - 设置 `Status = Succeed`；
    - Boot 那边的 `yield return operation` 结束；
    - 进入“设置默认包 + 正式游戏”的阶段。

---

## 6. 小结：从 Boot 到补丁状态机的整体视角

可以用下面这条“谁驱动谁”的链路来快速回忆：

1. **Boot.Start**
   - 设置 `GameManager.Instance.Behaviour` → 给其他系统一个协程宿主与场景事件入口；
   - 调用 `UniEvent.Initalize` → 创建 `[UniEvent] + UniEventDriver`，负责事件延迟派发；
   - 调用 `YooAssets.Initialize` → 创建 `[YooAssets] + YooAssetsDriver`，负责 `OperationSystem.Update`；
   - 创建并启动 `PatchOperation`，通过 `YooAssets.StartOperation` 纳入统一异步调度；
   - `yield return PatchOperation` 等待补丁完成；
   - 设置默认包、加载 DLL 和主场景。

2. **事件系统（UniEvent）**
   - `_listeners : Dictionary<int, LinkedList<Action<IEventMessage>>>` 保存“事件ID → 回调列表”;
   - `EventGroup._cachedListener` 保存“当前逻辑块订阅了哪些事件”，方便一键清理；
   - `UniEventDriver.Update` 每帧调用 `UniEvent.Update`，驱动延迟事件派发；
   - UI 调用 `UserEventDefine.*.SendEventMessage()` → 触发事件；
   - `PatchOperation.OnHandleEventMessage` 收到后 → 控制状态机变更。

3. **资源/异步系统（YooAssets + OperationSystem + AsyncOperationBase）**
   - `AsyncOperationBase` 定义统一的异步任务基类；
   - `OperationSystem` 管理所有任务的开始/更新/结束，控制每帧时间片；
   - `YooAssetsDriver.Update` 每帧调用 `OperationSystem.Update()`；
   - `PatchOperation : GameAsyncOperation` 只是所有任务中的一种，只不过内部挂着一个补丁状态机。

4. **状态机（UniMachine.StateMachine）**
   - `PatchOperation` 构造时创建 `StateMachine` 并预注册所有 `FsmXXX` 节点（一次性构造 + OnCreate）;
   - `OnStart` 进入起始状态；
   - `OnUpdate` 每帧转调 `_machine.Update()`；
   - 通过黑板共享上下文，通过 Owner 调用如 `SetFinish()` 等方法。

理解了这四块拼图之间的关系，再去看任何一个具体的补丁状态节点（如清单更新、创建下载器、清理缓存），都可以套到这条主线上去：

> “它只是挂在 PatchOperation 状态机上的一个节点，
> 由 OperationSystem 驱动更新，
> 通过 UniEvent 和 UI/用户交互，
> 最终在某个时刻调用 SetFinish 通知 Boot：补丁流程已经跑完。”
