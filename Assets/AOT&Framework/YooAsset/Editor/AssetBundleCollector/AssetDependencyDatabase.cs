using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace YooAsset.Editor
{
    /// <summary>
    /// 资源依赖数据库
    /// </summary>
    public class AssetDependencyDatabase
    {
        private const string FILE_VERSION = "1.0";

        /// <summary>
        /// 表示某个“节点”的局部视图
        /// </summary>
        private class DependencyInfo
        {
            /// <summary>
            /// 此哈希函数会聚合了以下内容：源资源路径、源资源、元文件、目标平台以及导入器版本。（修正：跟源资源路径无关）
            /// 如果此哈希值发送变化，则说明导入资源可能已更改，因此应重新搜集依赖关系。
            /// 修正：该哈希关注的是“依赖图的导入结果”（源文件内容、导入设置.meta、依赖资源的导入结果、目标平台、Importer/Unity 版本等）。
            /// 
            /// 补充：该值来自 AssetDatabase.GetAssetDependencyHash(assetPath) 的哈希值。
            /// 表示该资源“依赖图”的导入产物哈希：当资源本体的导入结果、其依赖资源、任意依赖的 .meta/导入设置、导入器版本或目标平台导致的导入产物发生变化时会变化。
            /// 注意：资源仅改名/移动（GUID 不变）通常不会改变此哈希；因此本数据库用 GUID 持久化依赖边，并在查询时将 GUID 映射为当前最新的 AssetPath，以吸收路径变化。
            /// • 依赖图 = 这个资源以及它“直接/间接引用”的所有资源构成的一张有向图。
            /// • 导入产物 = Unity 把“源文件 + 导入设置（.meta）+ 目标平台”等，经过 Importer 处理后生成的“可被引擎用的结果”（比如压缩后的贴图、模型网格与动画、AudioClip 数据等）。
            /// • 资源本体的导入结果 = 只看这个资源自身被导入后生成的结果（不含它引用的其它资源）。
            /// AssetDatabase.GetAssetDependencyHash(assetPath) 计算的是“依赖图的导入结果整体”的哈希：只要“任何一个节点（本体或依赖）的导入结果”改变，这个哈希就会变；仅改名/移动（GUID 不变）通常不会变。
            /// </summary>
            public string DependHash;

            /// <summary>
            /// 直接依赖资源的GUID列表，是该节点的“邻接表”（adjacency list），里面存的是“出边”的目标节点标识（用 GUID 存储依赖的端点，抗路径变化），被成为“依赖边”
            /// 因此把 DependGUIDs 称为“依赖边”，是强调“关系（边）”而不仅是“路径列表”的语义。
            /// </summary>
            public List<string> DependGUIDs = new List<string>();
        }

        //? 为什么把[AssetDependencyDatabase.CreateDatabase]构建[依赖数据库]调用[依赖信息创建方法 CreateDependencyInfo]获取到的依赖资源从 AssetPath 转成 GUID 存，
        //? 再在AssetBundleCollector构建CollectAssetInfo通过AssetDependencyDatabase.GetDependencies查询依赖资源时把 GUID 转回 AssetPath？
        //!? 之所以把依赖从 AssetPath 转成 GUID 存，再在查询时把 GUID 转回 AssetPath，是为了让“持久化的依赖缓存”对资源移动/重命名保持稳定、不失效，并减少不必要的重新计算与缓存文件抖动。这不是无意义的两次转换，是刻意的“路径解耦”。

        //!? 1.依赖项用 GUID 存储，抗路径变化:
        //!? • 构建时 CreateDependencyInfo 里，AssetDatabase.GetDependencies(assetPath, false) 返回的是当下的路径集合，但代码立刻用 AssetDatabase.AssetPathToGUID(dependAssetPath) 转 GUID 存到 DependencyInfo.DependGUIDs。
        //!? • 原因：Unity 的引用在序列化层面本来就是 GUID 稳定（.meta 保持不变）。资源文件被重命名/移动，GUID 不变，路径变。用 GUID 持久化，缓存就不会因为“仅路径变化”而失效。
        //!? • 查询时 CollectDependencies 通过 AssetDatabase.GUIDToAssetPath(dependGUID) 得到当前最新路径，天然适配资源移动后的新位置。

        //!? 2.依赖哈希与“仅路径变化”的关系:
        //!? • DependencyInfo.DependHash 来自 AssetDatabase.GetAssetDependencyHash(assetPath)。注释写明它聚合“源资源路径、源资源、元文件、目标平台、导入器版本”。
        //!? • 当“依赖文件仅仅移动/重命名”时，主资源序列化引用的 GUID 没变，通常不会触发主资源的依赖哈希变化。此时如果缓存里存的是路径列表，就会变“脏”（仍然是旧路径），但因为哈希未变，不会重建，结果就错了。
        //!? • 用 GUID 存储就避开了这个坑：即使不重建，也能在读取端把 GUID 映射为“最新路径”。

        //!? 3.为什么键仍用 AssetPath？
        //!? • 顶层 _database 的键是“主资源的 AssetPath”，是因为外部调用方传入的就是路径（例如 AssetBundleCollector.GetAllDependencies 里调用 command.AssetDependency.GetDependencies(mainAssetPath, true)）。
        //!? • 即便键是路径，CreateDatabase 会“全量扫描 + 选择性重建”：
        //!? • • 先读旧库（可选），再“移除无效资源”：AssetPathToGUID(assetPath, OnlyExistingAssets) 为空就删掉（资源被删或 moved 后原路径不再存在）。
        //!? • • 然后“全量扫描项目”：对每个现在存在的 assetPath，要么复用旧条目（哈希未变），要么 CreateDependencyInfo 重建。这样主资源被移动后，会以新路径产生新键，旧键被移除。
        //!? • 依赖边（DependGUIDs）是 GUID，不受子资源路径变化影响，从而旧条目也能正确工作。

        //!? 4.缓存文件体积与稳定性
        //!? • GUID 是固定长度字符串（32/36），而路径可能很长。以 GUID 存依赖边，磁盘文件更小、更稳定（不同机器/大小写/目录层级的路径差异不影响缓存内容），减少缓存文件的无意义变更。

        //!? 5.丢失引用与健壮性
        //!? • 查询阶段对丢失资源做了健壮处理：GUIDToAssetPath 失败会被跳过；对不在库里的 assetPath 会再用 AssetPathToGUID 判定是否“丢失引用”，分别给出 Warning 或抛出致命异常，便于发现缓存不一致。
        //!? 依赖文件被移动/重命名时：
        //!? • 存路径：缓存里是旧路径，且主资源的 DependHash 大概率不变，不会重建，查询得到的就是过期路径。
        //!? • 存 GUID：查询时动态映射到新路径，结果始终正确，无需重建。
        //!? 综上，当前设计是为了“持久化缓存的正确性与稳定性”，不是无意义的额外开销。

        //? 为什么GUID具有不以资源路径和文件名词而改变的稳定和固定长度等诸多优点，为什么YooAsset还是用 AssetPath 而不是 GUID 驱动业务链以契合“规则实现、API 使用与可读性呢？
        //!? YooAsset把“依赖边”用 GUID 持久化以抗路径变化，把“业务流程（枚举、过滤、分包、寻址、日志、可读性）”统一用 AssetPath 来驱动。即：存边用 GUID，算规则用路径。这不是健壮性孰优孰劣的问题，而是“稳定性 + 规则/可读性/API 契合度”的权衡
        //!? 1.规则与业务强依赖“路径语义”
        //!? • 打包规则、寻址规则、过滤规则全部以路径为输入，且大量用到路径语义（父目录、扩展名、相对 CollectPath 等）：
        //!? • 如果顶层统一用 GUID，以上每一步都会先做 GUID→Path，再做路径规则计算；与其在每个热路径都转一次，不如上层就以 Path 作为“业务主键”，更契合规则实现与可读性。

        //!? 2.Unity 编辑器 API 以“路径优先”更顺手
        //!? • 枚举与判定：诸多方法依赖资源的路径而非GUID
        //!? • • AssetDatabase.GetAllAssetPaths() -> string[] assetPaths、
        //!? • • AssetDatabase.IsValidFolder(assetPath)、GetMainAssetTypeAtPath(assetPath)、
        //!? • • AssetDatabase.GetDependencies(assetPath, recursive) 返回路径集合
        //!? • 代码里大量场景直接基于路径工作:
        //!? • • AssetBundleCollector 中枚举主资源、生成 CollectAssetInfo、检查地址冲突、输出日志都以 assetPath 为主。
        //!? • • BuildAssetInfo、BuildMapContext 的键与字段广泛使用 assetPath。
        //!? • 虽然 Unity 也支持 GUID，但路径是“使用更广 + 更直观”的 API 入参。顶层以 Path 驱动，可以减少全局的 GUID→Path 转换次数。

        //!? 3.可读性与诊断
        //!? • 控制台/报告/清单里输出路径更可读：如
        //!? • • 重复地址检查直接打印 AssetPath
        //!? • • TaskGetBuildMap.RemoveZeroReferenceAssets 写入 ReportIndependAsset.AssetPath 供审计
        //!? • 若顶层统一 GUID，所有日志都需要实时转换，不利于排查。

        //!? 4.依赖边用 GUID，是为了解决“路径变化导致缓存失真”
        //!? • DependencyInfo.DependGUIDs 用 GUID 存；查询时 GUIDToAssetPath 映射回“最新路径”。这样即便依赖文件移动/重命名，缓存仍然正确，这是“持久化层的稳定性”诉求。
        //!? • 相比之下，“业务层”的主键用 Path 并不造成正确性问题，因为：
        //!? • • 在 CreateDatabase 的全量扫描期会剔除“已失效路径”（AssetPathToGUID(... OnlyExistingAssets) 空则删条目），新路径会重建。也就是顶层键（Path）在每轮收集时更新一次即可。
        //!? 如果要“全局以 GUID 为主键”，技术上可行，但涉及大改，性价比也不高，YooAsset 的取舍是：用 GUID 存依赖边以保障“持久化稳定性”；用 AssetPath 驱动业务链以契合“规则实现、API 使用与可读性”。这两者结合，既稳又顺。


        private string _databaseFilePath;
        /// <summary>
        /// 键为AssetPath、值为DependencyInfo（“节点”的局部视图）
        /// </summary>
        private readonly Dictionary<string, DependencyInfo> _database = new Dictionary<string, DependencyInfo>(100000);

        /// <summary>
        /// 创建缓存数据库，即使<paramref name="readCacheDatabaseFile"/> == false，仍然会在内存中扫描并填充依赖表（只是不读/写磁盘文件），保证本轮收集可用。
        /// 只有<paramref name="readCacheDatabaseFile"/> == true，才会读取旧文件并把最新结构罗盘（加速下次收集）。
        /// </summary>
        /// <param name="readCacheDatabaseFile"></param>
        /// <param name="databaseFilePath"></param>
        public void CreateDatabase(bool readCacheDatabaseFile, string databaseFilePath)
        {
            _databaseFilePath = databaseFilePath;   //! "Library/AssetDependencyDB"
            _database.Clear();

            FileStream stream = null;
            BinaryReader reader = null;
            try
            {
                //! 1.读旧库（可选）
                //! 如果readCacheDatabaseFile == true 且文件存在，读取文件版本与条目（路径 → 依赖信息），并移除已经不存在的资源。
                if (readCacheDatabaseFile && File.Exists(databaseFilePath))
                {
                    //! 1.1 解析缓存文件
                    stream = File.OpenRead(databaseFilePath);       // 以只读方式打开指定路径，返回一个 FileStream 对象，表示对该文件的底层字节流访问。此方法适用于读取二进制文件（如自定义数据库文件、资源文件等）。
                    reader = new BinaryReader(stream);              // 创建一个 BinaryReader 包装 FileStream，用于以二进制格式读取基本数据类型（如 int、string、bool 等）。（默认使用 UTF-8 编码）
                    string fileVersion = reader.ReadString();       // 调用 ReadString() 从文件开头位置读取一个字符串。关键点：BinaryReader.ReadString() 的格式是：先读取一个 7 位编码的整数表示字符串长度（字节数），然后读取对应数量的字节，并用 UTF-8 解码为字符串。这意味着该文件必须是由 BinaryWriter.Write(string) 写入的，否则格式不匹配会导致异常

                    if (fileVersion != FILE_VERSION)
                        throw new Exception("The database file version not match !");

                    var count = reader.ReadInt32();                 // 从二进制流中读取一个 32 位整数（int），表示接下来要读取的条目数量。这个 count 通常是在写入文件时预先写入的，用于告诉读取端“后面有多少个资源项”。
                    for (int i = 0; i < count; i++)
                    {
                        var assetPath = reader.ReadString();        // 读取当前资源的路径字符串（如 "Assets/Prefabs/Player.prefab"）。
                        var cacheInfo = new DependencyInfo
                        {
                            DependHash = reader.ReadString(),
                            DependGUIDs = ReadStringList(reader),
                        };
                        _database.Add(assetPath, cacheInfo);
                    }

                    //! 1.2 移除无效资源（一般是路径变化变或者资源从项目中被删除，才会无法通过AssetPathToGUID找到对应的guid）
                    List<string> removeList = new List<string>(10000);
                    foreach (var cacheInfoPair in _database)
                    {
                        var assetPath = cacheInfoPair.Key;
#if UNITY_2021_3_OR_NEWER
                        //! AssetPathToGUIDOptions.OnlyExistingAssets是指资源存在磁盘上：指该资源文件（如 Player.prefab）真实存在于项目 Assets 文件夹中，且有对应的 .meta 文件。如果不用此选项（或使用旧版无参数重载），某些情况下可能返回看似合法但无效的 GUID
                        var assetGUID = AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets);
#else
                        var assetGUID = AssetDatabase.AssetPathToGUID(assetPath);
#endif
                        //! 如果资源被移动/重命名时，旧库中的assetPath就无法通过AssetPathToGUID获取到guid，则被认为是无效资源，需要在全量扫描项目时查找变动资源
                        if (string.IsNullOrEmpty(assetGUID))
                        {
                            removeList.Add(assetPath);
                        }
                    }
                    foreach (var assetPath in removeList)
                    {
                        _database.Remove(assetPath);
                    }
                }
            }
            catch (Exception ex)
            {
                ClearDatabase(true);
                Debug.LogError($"Failed to load cache database : {ex.Message}");
            }
            finally
            {
                if (reader != null)
                    reader.Close();
                if (stream != null)
                    stream.Close();
            }

            //! 2.全量扫描项目，查找新增或变动资源
            var allAssetPaths = AssetDatabase.GetAllAssetPaths();
            //!  对项目中每个assetPath进行操作
            foreach (var assetPath in allAssetPaths)
            {
                //! 如果库中存在该资源路径，正常来说只有在读取旧库后_database才会存在一些资源，不然应该都是执行else中的逻辑
                if (_database.TryGetValue(assetPath, out DependencyInfo cacheInfo))
                {
                    var dependHash = AssetDatabase.GetAssetDependencyHash(assetPath);
                    //! 如果该资源的DependencyInfo.DependHash和当前项目中的该资源的dependHash不一样，说明资源信息发生改变，就重建该资源的DependencyInfo（直接依赖，No recursive）
                    if (dependHash.ToString() != cacheInfo.DependHash)
                    {
                        _database[assetPath] = CreateDependencyInfo(assetPath);
                    }
                }
                //! 不存在该资源路径，则加入该资源的DependencyInfo
                else
                {
                    var newCacheInfo = CreateDependencyInfo(assetPath);
                    _database.Add(assetPath, newCacheInfo);
                }
            }
        }

        /// <summary>
        /// 保存缓存数据库
        /// </summary>
        public void SaveDatabase()
        {
            if (File.Exists(_databaseFilePath))
                File.Delete(_databaseFilePath);

            FileStream stream = null;
            BinaryWriter writer = null;
            try
            {
                stream = File.Create(_databaseFilePath);
                writer = new BinaryWriter(stream);
                writer.Write(FILE_VERSION);
                writer.Write(_database.Count);
                foreach (var assetPair in _database)
                {
                    string assetPath = assetPair.Key;
                    var assetInfo = assetPair.Value;
                    writer.Write(assetPath);
                    writer.Write(assetInfo.DependHash);
                    WriteStringList(writer, assetInfo.DependGUIDs);
                }
                writer.Flush();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to save cache database : {ex.Message}");
            }
            finally
            {
                if (writer != null)
                    writer.Close();
                if (stream != null)
                    stream.Close();
            }
        }

        /// <summary>
        /// 清理缓存数据库
        /// </summary>
        public void ClearDatabase(bool deleteDatabaseFile)
        {
            if (deleteDatabaseFile)
            {
                if (File.Exists(_databaseFilePath))
                    File.Delete(_databaseFilePath);
            }

            _database.Clear();
        }

        /// <summary>
        ///  获取资源的依赖列表
        /// </summary>
        public string[] GetDependencies(string assetPath, bool recursive)
        {
            // 注意：机制上不允许存在未收录的资源
            if (_database.ContainsKey(assetPath) == false)
            {
                throw new Exception($"Fatal : can not found cache info : {assetPath}");
            }

            var result = new HashSet<string>();

            // 注意：递归收集依赖时，依赖列表中包含主资源
            if (recursive)
                result.Add(assetPath);

            // 收集依赖
            CollectDependencies(assetPath, assetPath, result, recursive);

            return result.ToArray();
        }
        private void CollectDependencies(string parent, string assetPath, HashSet<string> result, bool recursive)
        {
            if (_database.TryGetValue(assetPath, out var cacheInfo) == false)
            {
                // 说明：检测是否为丢失引用的资产
#if UNITY_2021_3_OR_NEWER
                var assetGUID = AssetDatabase.AssetPathToGUID(assetPath, AssetPathToGUIDOptions.OnlyExistingAssets);
#else
                var assetGUID = AssetDatabase.AssetPathToGUID(assetPath);
#endif
                //! 若 AssetPathToGUID(assetPath) 为空，说明本身是“丢失引用”，仅警告并跳过。
                if (string.IsNullOrEmpty(assetGUID))
                {
                    Debug.LogWarning($"{parent} found missing asset : {assetPath}");
                    return;
                }
                //! 否则抛出异常（缓存不一致，属于致命错误场景）。
                else
                {
                    throw new Exception($"Fatal : can not found cache info : {assetPath}");
                }
            }

            foreach (var dependGUID in cacheInfo.DependGUIDs)
            {
                string dependAssetPath = AssetDatabase.GUIDToAssetPath(dependGUID);
                if (string.IsNullOrEmpty(dependAssetPath))
                    continue;

                // 如果是文件夹资源
                if (AssetDatabase.IsValidFolder(dependAssetPath))
                    continue;

                // 如果已经收集过
                if (result.Contains(dependAssetPath))
                    continue;

                result.Add(dependAssetPath);

                // 递归收集依赖
                if (recursive)
                    CollectDependencies(assetPath, dependAssetPath, result, recursive);
            }
        }

        /// <summary>
        /// 从<paramref name="reader"/>流中读取一个字符串列表。
        /// </summary>
        /// <param name="reader"></param>
        /// <returns>字符串列表</returns>
        private List<string> ReadStringList(BinaryReader reader)
        {
            var count = reader.ReadInt32();             // 先读取一个 32 位整数（int），表示接下来有多少个字符串。
            var values = new List<string>(count);       // 循环 count 次，每次调用 reader.ReadString() 读取一个字符串，并添加到列表中。
            for (int i = 0; i < count; i++)
            {
                values.Add(reader.ReadString());
            }
            return values;
        }
        private void WriteStringList(BinaryWriter writer, List<string> values)
        {
            writer.Write(values.Count);
            foreach (var value in values)
            {
                writer.Write(value);
            }
        }

        /// <summary>
        /// 构建资源<paramref name="assetPath"/>的直接依赖的DependencyInfo
        /// </summary>
        /// <param name="assetPath"></param>
        /// <returns></returns>
        private DependencyInfo CreateDependencyInfo(string assetPath)
        {
            var dependHash = AssetDatabase.GetAssetDependencyHash(assetPath);
            // 注意：AssetDatabase.GetDependencies()方法返回结果里会踢出丢失文件！
            var dependAssetPaths = AssetDatabase.GetDependencies(assetPath, false);// 只返回“磁盘上真实存在的 Asset 文件路径”，而 GameObject 是运行时概念，没有对应文件，所以不算依赖。
            var dependGUIDs = new List<string>();
            foreach (var dependAssetPath in dependAssetPaths)
            {
                string guid = AssetDatabase.AssetPathToGUID(dependAssetPath);
                if (string.IsNullOrEmpty(guid) == false)
                {
                    dependGUIDs.Add(guid);
                }
            }

            var cacheInfo = new DependencyInfo();
            cacheInfo.DependHash = dependHash.ToString();
            cacheInfo.DependGUIDs = dependGUIDs;
            return cacheInfo;
        }
    }
}