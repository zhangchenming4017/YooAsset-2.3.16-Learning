
using System;

namespace YooAsset.Editor
{
    /// <summary>
    /// ! ESBP（EditorSimulateBuildPipline）构建生成资源构建上下文
    /// </summary>
    public class TaskGetBuildMap_ESBP : TaskGetBuildMap, IBuildTask
    {
        void IBuildTask.Run(BuildContext context)
        {
            var buildParametersContext = context.GetContextObject<BuildParametersContext>();
            var buildMapContext = CreateBuildMap(true, buildParametersContext.Parameters);
            context.SetContextObject(buildMapContext);
        }
    }
}