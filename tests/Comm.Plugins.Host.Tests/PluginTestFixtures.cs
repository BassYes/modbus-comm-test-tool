using Comm.Core.Frames;
using Comm.Plugins.Abstractions;
using Comm.Plugins.Host;

namespace Comm.Plugins.Host.Tests;

/// <summary>测试脚手架：准备临时插件目录（复制示例插件的构建产物或写自定义清单）。</summary>
internal static class PluginTestFixtures
{
    /// <summary>仓库根目录（从测试输出目录向上找到含 CommTestTool.sln 的目录）。</summary>
    public static string RepoRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CommTestTool.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("找不到仓库根目录（CommTestTool.sln）");
        }
    }

    /// <summary>示例插件的构建输出目录。</summary>
    public static string SamplePluginOutput
    {
        get
        {
            var configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
                ? "Release"
                : "Debug";
            return Path.Combine(RepoRoot, "samples", "Comm.Plugin.Sample", "bin", configuration, "net8.0");
        }
    }

    /// <summary>
    /// 创建一个临时插件根目录（调用方负责清理）。
    /// 注意：不放在系统临时目录下——本机受限环境下系统 TEMP 对子进程可能不可写（Win32 权限），
    /// 统一放在测试程序集输出目录里（与 FrameFileWriterTests 的做法一致）。
    /// </summary>
    public static string CreatePluginRoot() =>
        Path.Combine(AppContext.BaseDirectory, "plugin-tests", Guid.NewGuid().ToString("N")[..8]);

    /// <summary>把示例插件安装到 &lt;root&gt;/Sample/ 下；可选择添加自测标记文件。</summary>
    public static string InstallSamplePlugin(string root, string directoryName = "Sample", bool throwOnRegister = false, bool slowRegister = false)
    {
        var target = Path.Combine(root, directoryName);
        Directory.CreateDirectory(target);

        File.Copy(Path.Combine(SamplePluginOutput, "Comm.Plugin.Sample.dll"), Path.Combine(target, "Comm.Plugin.Sample.dll"), overwrite: true);
        File.Copy(Path.Combine(RepoRoot, "samples", "Comm.Plugin.Sample", "plugin.json"), Path.Combine(target, "plugin.json"), overwrite: true);

        if (throwOnRegister) File.WriteAllText(Path.Combine(target, "throw-on-register.flag"), "1");
        if (slowRegister) File.WriteAllText(Path.Combine(target, "slow-register.flag"), "1");

        return target;
    }

    /// <summary>写一个自定义 plugin.json（用于清单校验用例）。</summary>
    public static string WriteManifest(string root, string directoryName, string json)
    {
        var target = Path.Combine(root, directoryName);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "plugin.json"), json);
        return target;
    }

    /// <summary>把示例插件的程序集复制到指定目录（用于自定义清单指向真实程序集）。</summary>
    public static void CopySampleAssembly(string directory)
    {
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(SamplePluginOutput, "Comm.Plugin.Sample.dll"),
            Path.Combine(directory, "Comm.Plugin.Sample.dll"), overwrite: true);
    }

    public static void Delete(string root)
    {
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    /// <summary>构造一个帧记录（用于解析器/导出器行为用例）。</summary>
    public static FrameRecord Frame(byte[] bytes, byte unitId = 1, byte? function = 0x03, FrameDirection direction = FrameDirection.Rx) => new()
    {
        Seq = 1,
        SessionId = "test",
        Direction = direction,
        Timestamp = DateTimeOffset.Now,
        RawBytes = bytes,
        UnitId = unitId,
        FunctionCode = function
    };
}
