using System.Reflection;
using System.Runtime.Loader;
using Xunit;
using Xunit.Sdk;

namespace Comm.TestRunner;

/// <summary>
/// 轻量 xunit 用例运行器：直接反射调用带 [Fact] / [Theory] 的方法并汇总结果。
/// 用途：在没有 Visual Studio / 受限环境下也能验证同一批测试用例（见规划 16.5 的本地门禁）。
/// 完整的 CI 环境仍建议使用 dotnet test。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var assemblies = new List<Assembly>
        {
            typeof(Comm.Protocols.Modbus.Tests.CrcTests).Assembly,
            typeof(Comm.Session.Tests.ModbusMasterTests).Assembly,
            typeof(Comm.Scanning.Tests.DataDecoderTests).Assembly,
            typeof(Comm.Network.Tests.SubnetCalculatorTests).Assembly,
            typeof(Comm.Infrastructure.Tests.ShutdownCoordinatorTests).Assembly,
            // M3a / M3b 新增的测试程序集（见 docs/m3-交付说明.md）
            typeof(Comm.Platform.Windows.Tests.NetworkConfigValidatorTests).Assembly,
            typeof(Comm.NetConfigHelper.Tests.RollbackCountdownTests).Assembly,
            typeof(Comm.Plugins.Host.Tests.PluginHostTests).Assembly,
            typeof(Comm.Architecture.Tests.ArchitectureTests).Assembly,
            // M4：安全与高风险回归（SR-1 ~ SR-7，规划 16.3.9，每次发布必跑）
            typeof(Comm.Security.Tests.SecurityRegressionTests).Assembly
        };

        // 可选：把额外测试程序集加进来（供尚未登记到本运行器的测试项目自检）：
        //    dotnet run --project tests/Comm.TestRunner -- --assembly=<测试程序集路径> [--filter=名称片段]
        var extraAssemblies = args.Where(a => a.StartsWith("--assembly=", StringComparison.OrdinalIgnoreCase))
            .Select(a => a["--assembly=".Length..])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        foreach (var path in extraAssemblies)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                Console.Error.WriteLine($"找不到测试程序集：{full}");
                return 2;
            }

            RegisterProbeDirectory(Path.GetDirectoryName(full)!);
            assemblies.Add(Assembly.LoadFrom(full));
        }

        var filter = args.FirstOrDefault(a => !a.StartsWith("--assembly=", StringComparison.OrdinalIgnoreCase));
        if (filter is not null && filter.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase))
        {
            filter = filter["--filter=".Length..];
        }

        int passed = 0, failed = 0, skipped = 0;
        var failures = new List<string>();

        foreach (var assembly in assemblies)
        {
            Console.WriteLine($"=== {assembly.GetName().Name} ===");

            foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.GetCustomAttributes().Any(a => a is FactAttribute or TheoryAttribute))
                    .OrderBy(m => m.Name);

                foreach (var method in methods)
                {
                    var cases = BuildCases(method);
                    if (cases.Count == 0)
                    {
                        skipped++;
                        Console.WriteLine($"  SKIP  {type.Name}.{method.Name}（没有测试数据）");
                        continue;
                    }

                    foreach (var (arguments, display) in cases)
                    {
                        var name = $"{type.Name}.{method.Name}{display}";
                        if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            var instance = Activator.CreateInstance(type);
                            var result = method.Invoke(instance, ConvertArguments(method, arguments));
                            if (result is Task task) await task.ConfigureAwait(false);
                            if (result is ValueTask valueTask) await valueTask.ConfigureAwait(false);
                            passed++;
                            Console.WriteLine($"  PASS  {name}");
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            var actual = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                            Console.WriteLine($"  FAIL  {name}");
                            Console.WriteLine($"        {actual.GetType().Name}: {actual.Message.Replace("\n", "\n        ")}");
                            failures.Add($"{name}\n        {actual.GetType().Name}: {actual.Message}");
                        }
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"总计：通过 {passed}，失败 {failed}，跳过 {skipped}");

        if (failures.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("失败明细：");
            foreach (var failure in failures) Console.WriteLine("  - " + failure);
            return 1;
        }

        return 0;
    }

    /// <summary>把外部测试程序集所在目录加入依赖探测路径（--assembly= 模式下必需）。</summary>
    private static readonly HashSet<string> ProbedDirectories = new(StringComparer.OrdinalIgnoreCase);

    private static void RegisterProbeDirectory(string directory)
    {
        if (!ProbedDirectories.Add(directory)) return;

        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            foreach (var probe in ProbedDirectories)
            {
                var candidate = Path.Combine(probe, name.Name + ".dll");
                if (File.Exists(candidate)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
            }

            return null;
        };
    }

    /// <summary>把 InlineData 的常量转换为形参类型（xunit 会做同样的事）。</summary>
    private static object?[] ConvertArguments(MethodInfo method, object?[] arguments)
    {
        var parameters = method.GetParameters();
        if (parameters.Length != arguments.Length) return arguments;

        var converted = new object?[arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            var value = arguments[i];
            var target = parameters[i].ParameterType;
            if (value is null || target.IsInstanceOfType(value))
            {
                converted[i] = value;
                continue;
            }

            var underlying = Nullable.GetUnderlyingType(target) ?? target;
            converted[i] = underlying.IsEnum
                ? Enum.ToObject(underlying, value)
                : Convert.ChangeType(value, underlying, System.Globalization.CultureInfo.InvariantCulture);
        }
        return converted;
    }

    private static List<(object?[] Arguments, string Display)> BuildCases(MethodInfo method)
    {
        var cases = new List<(object?[], string)>();

        // 注意：TheoryAttribute 继承自 FactAttribute，必须先判断 Theory。
        if (method.GetCustomAttribute<TheoryAttribute>() is null
            && method.GetCustomAttribute<FactAttribute>() is not null)
        {
            cases.Add((Array.Empty<object?>(), string.Empty));
            return cases;
        }

        var dataAttributes = method.GetCustomAttributes().OfType<DataAttribute>().ToArray();
        if (dataAttributes.Length == 0) return cases;

        foreach (var attribute in dataAttributes)
        {
            var rows = attribute.GetData(method);
            if (rows is null) continue;

            foreach (var row in rows)
            {
                var arguments = row?.ToArray() ?? Array.Empty<object?>();
                cases.Add((arguments, "(" + string.Join(", ", arguments.Select(a => a?.ToString() ?? "null")) + ")"));
            }
        }

        return cases;
    }
}
