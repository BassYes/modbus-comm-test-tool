using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Comm.Architecture.Tests;

/// <summary>
/// 架构测试（规划 12.3、12.4 AD-02、14.1 分层规则、16.3.1）。
/// <para>
/// 只读仓库里的 <c>.csproj</c> / <c>.sln</c> / 源码文本做**静态**断言，不依赖编译顺序，
/// 也不引用任何产品程序集（行为层断言用 <see cref="Assembly.LoadFrom"/> 尽力而为）。
/// </para>
/// </summary>
public class ArchitectureTests
{
    // -------------------------------------------------------------------------------------
    // 仓库定位与基础数据
    // -------------------------------------------------------------------------------------

    private static string RepoRoot
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

    private static string SrcDirectory => Path.Combine(RepoRoot, "src");

    private sealed record ProjectInfo(string Name, string Path, string? OutputType, string? TargetFramework,
        bool UseWpf, bool HasVersionProperty, IReadOnlyList<string> References);

    private static IReadOnlyList<ProjectInfo> ReadProjects(string directory) =>
        Directory.GetFiles(directory, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(ReadProject)
            .ToArray();

    private static ProjectInfo ReadProject(string path)
    {
        var document = XDocument.Load(path);
        var properties = document.Descendants()
            .Where(e => e.Parent?.Name.LocalName == "PropertyGroup")
            .GroupBy(e => e.Name.LocalName)
            .ToDictionary(g => g.Key, g => g.Last().Value);

        var references = document.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")?.Value ?? string.Empty))
            .Where(name => !string.IsNullOrEmpty(name))
            .ToArray();

        return new ProjectInfo(
            Name: Path.GetFileNameWithoutExtension(path),
            Path: path,
            OutputType: properties.GetValueOrDefault("OutputType"),
            TargetFramework: properties.GetValueOrDefault("TargetFramework"),
            UseWpf: string.Equals(properties.GetValueOrDefault("UseWPF"), "true", StringComparison.OrdinalIgnoreCase),
            HasVersionProperty: properties.ContainsKey("Version"),
            References: references);
    }

    private static IReadOnlyList<ProjectInfo> SrcProjects() => ReadProjects(SrcDirectory);

    private static IReadOnlyList<ProjectInfo> TestProjects() => ReadProjects(Path.Combine(RepoRoot, "tests"));

    private static ProjectInfo Project(string name) =>
        SrcProjects().SingleOrDefault(p => p.Name == name)
        ?? throw new InvalidOperationException($"找不到 src 下的项目 {name}");

    // -------------------------------------------------------------------------------------
    // 1. 依赖方向（规划 12.3 / 14.1：上层只依赖下层，底层不得反向引用上层）
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// 允许的 src → src 引用边白名单（现状即约束）。新增引用必须先在这里登记并说明理由，
    /// 否则架构测试会失败——这是"底层不得反向引用上层"的守护。
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedSrcReferences = new(StringComparer.Ordinal)
    {
        ["Comm.Core"] = Array.Empty<string>(),
        ["Comm.Protocols.Modbus"] = new[] { "Comm.Core" },
        ["Comm.Transport"] = new[] { "Comm.Core" },
        ["Comm.Session"] = new[] { "Comm.Core", "Comm.Protocols.Modbus" },
        ["Comm.Scanning"] = new[] { "Comm.Core", "Comm.Protocols.Modbus", "Comm.Session" },
        ["Comm.Capture"] = new[] { "Comm.Core" },
        ["Comm.Network"] = new[] { "Comm.Core" },
        ["Comm.Diagnostics"] = new[] { "Comm.Core" },
        ["Comm.Settings"] = new[] { "Comm.Core", "Comm.Diagnostics", "Comm.Protocols.Modbus", "Comm.Scanning" },
        ["Comm.Plugins.Abstractions"] = new[] { "Comm.Core" },
        ["Comm.Plugins.Host"] = new[] { "Comm.Core", "Comm.Plugins.Abstractions" },
        ["Comm.Platform.Windows"] = new[] { "Comm.Core", "Comm.Network" },
        ["Comm.NetConfigHelper"] = new[] { "Comm.Core", "Comm.Diagnostics", "Comm.Platform.Windows" },
        ["Comm.App.Wpf"] = new[]
        {
            "Comm.Core", "Comm.Capture", "Comm.Diagnostics", "Comm.Network", "Comm.Plugins.Abstractions",
            "Comm.Plugins.Host", "Comm.Platform.Windows", "Comm.Protocols.Modbus", "Comm.Scanning",
            "Comm.Session", "Comm.Settings", "Comm.Transport"
        }
    };

    [Fact]
    public void Core_has_no_project_references()
    {
        var core = Project("Comm.Core");
        Assert.True(core.References.Count == 0,
            $"Comm.Core 必须是无外部依赖的领域层（规划 AD-02），但它引用了：{string.Join(", ", core.References)}");
    }

    [Fact]
    public void Source_projects_only_reference_allowed_projects()
    {
        var failures = new List<string>();

        foreach (var project in SrcProjects())
        {
            if (!AllowedSrcReferences.TryGetValue(project.Name, out var allowed))
            {
                failures.Add($"项目 {project.Name} 未登记在白名单中——请先在 ArchitectureTests.AllowedSrcReferences 里说明它的依赖方向。");
                continue;
            }

            foreach (var reference in project.References.Distinct(StringComparer.Ordinal))
            {
                if (!allowed.Contains(reference, StringComparer.Ordinal))
                {
                    failures.Add($"{project.Name} → {reference}：不在允许的依赖边内（允许：{string.Join(", ", allowed)}）" +
                                 "。如果这是有意的新依赖，请更新白名单并检查分层是否被破坏。");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void No_product_project_depends_on_a_test_project()
    {
        var testNames = TestProjects().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var failures = SrcProjects()
            .SelectMany(p => p.References.Where(testNames.Contains).Select(r => $"{p.Name} → {r}"))
            .ToArray();

        Assert.True(failures.Length == 0,
            "产品代码不得依赖测试项目：" + string.Join(", ", failures));
    }

    [Fact]
    public void Nothing_depends_on_the_ui_project()
    {
        var failures = SrcProjects()
            .Where(p => p.Name != "Comm.App.Wpf")
            .SelectMany(p => p.References.Where(r => r == "Comm.App.Wpf").Select(r => $"{p.Name} → {r}"))
            .ToArray();

        Assert.True(failures.Length == 0,
            "UI 依赖必须单向（规划 AD-02：除 App.Wpf 外不得引用 WPF 与界面程序集）：" + string.Join(", ", failures));
    }

    [Fact]
    public void Scanning_and_Network_do_not_reference_wpf()
    {
        foreach (var name in new[] { "Comm.Scanning", "Comm.Network", "Comm.Platform.Windows", "Comm.Settings" })
        {
            var project = Project(name);
            Assert.False(project.UseWpf, $"{name} 不得使用 WPF（规划 AD-02）");
        }
    }

    [Fact]
    public void Platform_windows_does_not_reference_scanning_or_session()
    {
        var project = Project("Comm.Platform.Windows");
        Assert.DoesNotContain("Comm.Scanning", project.References);
        Assert.DoesNotContain("Comm.Session", project.References);
    }

    [Fact]
    public void Plugins_host_does_not_reference_the_ui()
    {
        var project = Project("Comm.Plugins.Host");
        Assert.DoesNotContain("Comm.App.Wpf", project.References);
    }

    // -------------------------------------------------------------------------------------
    // 2. WPF / WinForms 只在允许的项目中出现（AD-02）
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Only_the_ui_project_uses_wpf()
    {
        var offenders = SrcProjects().Where(p => p.UseWpf && p.Name != "Comm.App.Wpf").Select(p => p.Name).ToArray();
        Assert.True(offenders.Length == 0, "以下项目不应使用 WPF：" + string.Join(", ", offenders));
    }

    /// <summary>允许出现 <c>using Microsoft.Win32</c> 的项目（注册表/文件对话框等 Windows 互操作）。</summary>
    private static readonly HashSet<string> Win32AllowedProjects = new(StringComparer.Ordinal)
    {
        "Comm.App.Wpf",        // Microsoft.Win32.OpenFileDialog / SaveFileDialog
        "Comm.NetConfigHelper" // 助手在管理员上下文里读注册表做诊断
    };

    /// <summary>允许出现 <c>using System.Windows</c> 的项目。</summary>
    private static readonly HashSet<string> WpfNamespaceAllowedProjects = new(StringComparer.Ordinal) { "Comm.App.Wpf" };

    [Fact]
    public void Source_text_does_not_leak_wpf_outside_the_ui_project()
    {
        var failures = new List<string>();

        foreach (var project in SrcProjects())
        {
            foreach (var file in SourceFiles(Path.GetDirectoryName(project.Path)!))
            {
                var text = File.ReadAllText(file);

                if (Regex.IsMatch(text, @"using\s+System\.Windows\.Forms") ||
                    Regex.IsMatch(text, @"<UseWindowsForms>\s*true"))
                {
                    failures.Add($"{project.Name}/{Path.GetFileName(file)}：使用了 WinForms（规划只使用 WPF）。");
                }

                if (!WpfNamespaceAllowedProjects.Contains(project.Name) &&
                    Regex.IsMatch(text, @"using\s+System\.Windows\s*;"))
                {
                    failures.Add($"{project.Name}/{Path.GetFileName(file)}：使用了 System.Windows，但该层应保持与 UI 无关（规划 AD-02）。");
                }

                if (!Win32AllowedProjects.Contains(project.Name) &&
                    Regex.IsMatch(text, @"using\s+Microsoft\.Win32\s*;"))
                {
                    failures.Add($"{project.Name}/{Path.GetFileName(file)}：使用了 Microsoft.Win32，未在允许清单内。");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static IEnumerable<string> SourceFiles(string directory) =>
        Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    // -------------------------------------------------------------------------------------
    // 3. 解决方案登记与版本号单一来源（规划 9.4、22.1.2 F-7/F-9）
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Every_src_project_is_listed_in_the_solution()
    {
        var solution = File.ReadAllText(Path.Combine(RepoRoot, "CommTestTool.sln"));
        var missing = SrcProjects()
            .Where(p => !solution.Contains(Path.GetFileName(p.Path), StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)
            .ToArray();

        Assert.True(missing.Length == 0,
            "以下 src 项目未登记到 CommTestTool.sln（会导致 CI/构建漏编）：" + string.Join(", ", missing));
    }

    [Fact]
    public void Every_project_listed_in_the_solution_exists_on_disk()
    {
        var solution = File.ReadAllText(Path.Combine(RepoRoot, "CommTestTool.sln"));
        var declared = Regex.Matches(solution, @"""([^""]+\.csproj)""")
            .Select(m => m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var missing = declared.Where(relative => !File.Exists(Path.Combine(RepoRoot, relative))).ToArray();

        Assert.True(missing.Length == 0,
            "以下在解决方案中登记的项目文件不存在：" + string.Join(", ", missing));
    }

    [Fact]
    public void Every_project_targets_a_supported_framework()
    {
        var failures = new List<string>();

        foreach (var project in SrcProjects().Concat(TestProjects()))
        {
            // 未显式声明时继承 Directory.Build.props 的 net8.0。
            var framework = project.TargetFramework ?? "net8.0";
            if (framework is not ("net8.0" or "net8.0-windows"))
            {
                failures.Add($"{project.Name} 的目标框架为 {framework}（规划 AD-03：仅支持 net8.0 / net8.0-windows）。");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Directory_build_props_is_the_single_version_source()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot, "Directory.Build.props"));
        var version = props.Descendants().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
        Assert.False(string.IsNullOrWhiteSpace(version), "Directory.Build.props 必须定义 <Version>（版本号唯一来源）。");

        // 规划 12.8：Comm.Plugins.Abstractions 单独版本号，是唯一的例外。
        var offenders = SrcProjects()
            .Where(p => p.HasVersionProperty && p.Name != "Comm.Plugins.Abstractions")
            .Select(p => p.Name)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "以下项目自带 <Version>，会与 Directory.Build.props 冲突：" + string.Join(", ", offenders));
    }

    // -------------------------------------------------------------------------------------
    // 4. 已构建程序集的行为层守护（尽力而为：dll 不存在时跳过）
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Core_assembly_references_only_bcl()
    {
        var assembly = TryLoadProductAssembly("Comm.Core");
        if (assembly is null) return;

        var offenders = assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith("Comm.", StringComparison.Ordinal) ||
                           name is "PresentationCore" or "PresentationFramework" or "WindowsBase" or "System.Windows.Forms")
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Comm.Core 只能依赖 BCL（规划 AD-02），但它引用了：" + string.Join(", ", offenders));
    }

    [Fact]
    public void Non_ui_assemblies_do_not_reference_wpf()
    {
        foreach (var name in new[] { "Comm.Scanning", "Comm.Network", "Comm.Session", "Comm.Settings", "Comm.Platform.Windows", "Comm.Plugins.Host" })
        {
            var assembly = TryLoadProductAssembly(name);
            if (assembly is null) continue;

            var offenders = assembly.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .Where(referenced => referenced is "PresentationCore" or "PresentationFramework" or "WindowsBase")
                .ToArray();

            Assert.True(offenders.Length == 0, $"{name} 不得引用 WPF：" + string.Join(", ", offenders));
        }
    }

    /// <summary>按仓库内的构建输出尽力加载产品程序集；找不到（未构建/其他 TFM）时返回 null 由调用方跳过。</summary>
    private static Assembly? TryLoadProductAssembly(string name)
    {
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            foreach (var framework in new[] { "net8.0", "net8.0-windows" })
            {
                var candidate = Path.Combine(RepoRoot, "src", name, "bin", configuration, framework, name + ".dll");
                if (!File.Exists(candidate)) continue;

                try
                {
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        return null;
    }
}
