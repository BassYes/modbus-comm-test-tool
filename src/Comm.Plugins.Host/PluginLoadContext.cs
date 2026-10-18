using System.Reflection;
using System.Runtime.Loader;

namespace Comm.Plugins.Host;

/// <summary>
/// 插件加载上下文（规划 AD-13：进程内插件用独立 <see cref="AssemblyLoadContext"/> 加载）。
/// <para>
/// 关键点：<c>Comm.Core</c> 与 <c>Comm.Plugins.Abstractions</c> 必须**回落默认上下文**，
/// 否则插件看到的 <c>ICommPlugin</c> 与宿主看到的会是两个不同的类型，注册必然失败。
/// 因此插件目录里不应存在这两个程序集（示例插件工程的引用已设 <c>Private=false</c>）。
/// </para>
/// <para>
/// <see cref="AssemblyLoadContext"/> 不是安全沙箱；只加载受信目录下的插件（规划 AD-13）。
/// </para>
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    /// <summary>由宿主提供的共享契约程序集（不随插件复制，必须统一到默认上下文）。</summary>
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "Comm.Core",
        "Comm.Plugins.Abstractions"
    };

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string entryAssemblyPath)
        : base($"plugin:{System.IO.Path.GetFileNameWithoutExtension(entryAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && SharedAssemblies.Contains(assemblyName.Name))
        {
            // 返回 null → 运行时会到默认加载上下文里查找（保证类型标识一致）。
            return null;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
