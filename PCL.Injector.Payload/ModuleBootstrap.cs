using System;

// 重要: ModuleInitializerAttribute 必须定义在 System.Runtime.CompilerServices 命名空间
// Roslyn 编译器只认这个命名空间下的 ModuleInitializer
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class ModuleInitializerAttribute : Attribute
    {
    }
}

namespace PCL.Injector.Payload
{
    using System.Runtime.CompilerServices;

    internal static class ModuleBootstrap
    {
        [ModuleInitializer]
        internal static void AutoInitialize()
        {
            Boot.Initialize();
        }
    }
}
