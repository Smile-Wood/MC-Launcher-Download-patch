using PCL.Injector.Payload;

/// <summary>
/// .NET 启动钩子入口 —— DOTNET_STARTUP_HOOKS 要求的 StartupHook 类
/// 必须在全局命名空间，方法名必须是 Initialize
/// </summary>
public static class StartupHook
{
    public static void Initialize()
    {
        Boot.Initialize();
    }
}
