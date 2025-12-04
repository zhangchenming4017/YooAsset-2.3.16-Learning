
using System;

namespace YooAsset.Editor
{
    /// <summary>
    /// *ESBP（EditorSimulateBuildPipline）检验
    /// </summary>
    public class TaskPrepare_ESBP : IBuildTask
    {
        void IBuildTask.Run(BuildContext context)
        {
            var buildParametersContext = context.GetContextObject<BuildParametersContext>();
            var buildParameters = buildParametersContext.Parameters;

            // 检测基础构建参数
            buildParametersContext.CheckBuildParameters();
        }
    }
}