using Comm.Core.Errors;
using Comm.Core.Frames;
using Comm.Plugins.Abstractions;
using Comm.Plugins.Host;
using Xunit;

namespace Comm.Plugins.Host.Tests;

/// <summary>
/// 插件宿主：发现、加载、故障隔离、启停（规划 8、14.7、15.11；SR-7 只加载受信目录）。
/// 所有用例都在临时目录里做，不触碰真实 plugins 目录。
/// </summary>
public class PluginHostTests
{
    private static readonly Version HostVersion = new(0, 3, 0);

    private static PluginHost CreateHost(string directory, IPluginStateStore? store = null, int timeoutMs = 5000) =>
        new(directory, store ?? new InMemoryPluginStateStore(), HostVersion, timeoutMs);

    [Fact]
    public void Missing_directory_is_not_an_error()
    {
        using var host = CreateHost(Path.Combine(AppContext.BaseDirectory, "plugin-tests", "does-not-exist-" + Guid.NewGuid().ToString("N")[..6]));

        host.LoadAll();

        Assert.Empty(host.Plugins);
        Assert.True(host.Catalog.IsEmpty);
    }

    [Fact]
    public void Empty_directory_yields_no_plugins()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            Directory.CreateDirectory(root);
            using var host = CreateHost(root);

            host.LoadAll();

            Assert.Empty(host.Plugins);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Loads_the_sample_plugin_and_registers_extensions()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.InstallSamplePlugin(root);
            using var host = CreateHost(root);

            host.LoadAll();

            var info = Assert.Single(host.Plugins);
            Assert.Equal("sample-plugin", info.Id);
            Assert.Equal(PluginState.Loaded, info.State);
            Assert.Null(info.Error);
            Assert.False(info.MayWrite);

            Assert.Single(host.Catalog.FrameParsers);
            Assert.Single(host.Catalog.Exporters);
            Assert.All(host.Catalog.Registrations, r => Assert.Equal("sample-plugin", r.PluginId));
            Assert.Equal(2, host.Catalog.OfPlugin("sample-plugin").Count);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Plugin_directory_may_omit_shared_contract_assemblies()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            var directory = PluginTestFixtures.InstallSamplePlugin(root);

            // 共享契约不能出现在插件目录（否则会破坏类型标识，见 PluginLoadContext）。
            Assert.False(File.Exists(Path.Combine(directory, "Comm.Plugins.Abstractions.dll")));
            Assert.False(File.Exists(Path.Combine(directory, "Comm.Core.dll")));

            using var host = CreateHost(root);
            host.LoadAll();

            // 能加载成功即证明共享契约回落默认上下文生效（ICommPlugin 类型一致）。
            Assert.Equal(PluginState.Loaded, Assert.Single(host.Plugins).State);
            Assert.IsAssignableFrom<IFrameParser>(host.Catalog.FrameParsers[0]);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Register_failure_marks_plugin_failed_and_counts_failure()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.InstallSamplePlugin(root, throwOnRegister: true);
            var store = new InMemoryPluginStateStore();
            using var host = CreateHost(root, store);

            host.LoadAll();

            var info = Assert.Single(host.Plugins);
            Assert.Equal(PluginState.Failed, info.State);
            Assert.Equal(1, info.FailureCount);
            Assert.False(info.AutoDisabled);
            Assert.Contains(ErrorCodes.PluginCallFailed, info.Error);
            Assert.Equal(1, store.GetFailureCount("sample-plugin"));
            Assert.True(host.Catalog.IsEmpty);
            Assert.False(store.IsDisabled("sample-plugin"));
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Third_consecutive_failure_auto_disables_the_plugin()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.InstallSamplePlugin(root, throwOnRegister: true);
            var store = new InMemoryPluginStateStore();

            // 三次加载尝试（模拟"主程序反复启动"），失败计数持久化在状态存储里。
            for (int i = 0; i < 3; i++)
            {
                using var host = CreateHost(root, store);
                host.LoadAll();

                var info = Assert.Single(host.Plugins);
                Assert.Equal(i + 1, store.GetFailureCount("sample-plugin"));
                if (i < 2) Assert.Equal(PluginState.Failed, info.State);
            }

            Assert.True(store.IsDisabled("sample-plugin"));

            using var finalHost = CreateHost(root, store);
            finalHost.LoadAll();
            var disabled = Assert.Single(finalHost.Plugins);
            Assert.Equal(PluginState.Disabled, disabled.State);
            Assert.True(disabled.AutoDisabled);
            Assert.Contains(ErrorCodes.PluginAutoDisabled, disabled.Error);
            Assert.True(finalHost.Catalog.IsEmpty);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Disabled_plugin_is_not_loaded_from_the_start()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.InstallSamplePlugin(root);
            var store = new InMemoryPluginStateStore();
            store.SetDisabled("sample-plugin", true);

            using var host = CreateHost(root, store);
            host.LoadAll();

            Assert.Equal(PluginState.Disabled, Assert.Single(host.Plugins).State);
            Assert.True(host.Catalog.IsEmpty);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Reload_re_enables_a_disabled_plugin_and_resets_failures()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.InstallSamplePlugin(root);
            var store = new InMemoryPluginStateStore();
            store.SetDisabled("sample-plugin", true);
            store.SetFailureCount("sample-plugin", 3);

            using var host = CreateHost(root, store);
            host.LoadAll();
            Assert.True(host.Catalog.IsEmpty);

            var reloaded = host.Reload("sample-plugin");

            Assert.NotNull(reloaded);
            Assert.Equal(PluginState.Loaded, reloaded!.State);
            Assert.False(store.IsDisabled("sample-plugin"));
            Assert.Equal(0, store.GetFailureCount("sample-plugin"));
            Assert.Single(host.Catalog.FrameParsers);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Disable_unloads_extensions_and_persists_state()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.InstallSamplePlugin(root);
            var store = new InMemoryPluginStateStore();
            using var host = CreateHost(root, store);

            host.LoadAll();
            Assert.Equal(2, host.Catalog.Registrations.Count);

            host.Disable("sample-plugin");

            Assert.True(store.IsDisabled("sample-plugin"));
            Assert.True(host.Catalog.IsEmpty);
            Assert.Equal(PluginState.Disabled, Assert.Single(host.Plugins).State);
            Assert.False(Assert.Single(host.Plugins).AutoDisabled);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Entry_type_from_manifest_is_honoured_and_missing_type_is_invalid()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            var directory = PluginTestFixtures.WriteManifest(root, "MissingType",
                """{"id":"missing-type","name":"入口类型不存在","version":"1.0.0","hostVersionRange":"*","entry":"Comm.Plugin.Sample.dll","type":"Comm.Plugin.Sample.Nope"}""");
            PluginTestFixtures.CopySampleAssembly(directory);

            using var host = CreateHost(root);
            host.LoadAll();

            var info = Assert.Single(host.Plugins);
            Assert.Equal(PluginState.Invalid, info.State);
            Assert.Contains(ErrorCodes.PluginManifestInvalid, info.Error);
            Assert.Contains("Nope", info.Error);
            Assert.True(host.Catalog.IsEmpty);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Manifest_id_must_match_assembly_declaration()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            var directory = PluginTestFixtures.WriteManifest(root, "IdMismatch",
                """{"id":"other-id","name":"Id 不一致","version":"1.0.0","hostVersionRange":"*","entry":"Comm.Plugin.Sample.dll","type":"Comm.Plugin.Sample.SamplePlugin"}""");
            PluginTestFixtures.CopySampleAssembly(directory);

            using var host = CreateHost(root);
            host.LoadAll();

            var info = Assert.Single(host.Plugins);
            Assert.Equal(PluginState.Invalid, info.State);
            Assert.Contains("id", info.Error);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Multiple_assemblies_without_entry_is_invalid()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            var directory = PluginTestFixtures.WriteManifest(root, "Ambiguous",
                """{"id":"ambiguous","name":"入口不明确","version":"1.0.0","hostVersionRange":"*"}""");
            PluginTestFixtures.CopySampleAssembly(directory);
            File.Copy(Path.Combine(PluginTestFixtures.SamplePluginOutput, "Comm.Plugin.Sample.dll"),
                Path.Combine(directory, "Second.dll"), overwrite: true);

            using var host = CreateHost(root);
            host.LoadAll();

            var info = Assert.Single(host.Plugins);
            Assert.Equal(PluginState.Invalid, info.State);
            Assert.Contains("entry", info.Error);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Entry_assembly_with_parent_directory_escape_is_rejected()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            Directory.CreateDirectory(root);

            // 插件目录之外放一个真实程序集：清单用 ..\ 指向它就属于目录逃逸（审计 F-05）
            File.Copy(Path.Combine(PluginTestFixtures.SamplePluginOutput, "Comm.Plugin.Sample.dll"),
                Path.Combine(root, "outside.dll"), overwrite: true);

            var directory = PluginTestFixtures.WriteManifest(root, "Escape",
                """{"id":"escape","name":"逃逸插件","version":"1.0.0","hostVersionRange":"*","entry":"..\\outside.dll"}""");
            Assert.True(Directory.Exists(directory));

            using var host = CreateHost(root);
            host.LoadAll();

            var info = Assert.Single(host.Plugins);
            Assert.Equal(PluginState.Invalid, info.State);
            Assert.Contains(ErrorCodes.PluginManifestInvalid, info.Error);
            Assert.Contains("上级目录", info.Error);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Entry_assembly_with_absolute_path_is_rejected()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            Directory.CreateDirectory(root);
            var outside = Path.Combine(root, "outside-abs.dll");
            File.Copy(Path.Combine(PluginTestFixtures.SamplePluginOutput, "Comm.Plugin.Sample.dll"),
                outside, overwrite: true);

            var escaped = outside.Replace("\\", "\\\\");
            var directory = PluginTestFixtures.WriteManifest(root, "Absolute",
                $$"""{"id":"absolute","name":"绝对路径插件","version":"1.0.0","hostVersionRange":"*","entry":"{{escaped}}"}""");
            Assert.True(Directory.Exists(directory));

            using var host = CreateHost(root);
            host.LoadAll();

            var info = Assert.Single(host.Plugins);
            Assert.Equal(PluginState.Invalid, info.State);
            Assert.Contains(ErrorCodes.PluginManifestInvalid, info.Error);
            Assert.Contains("绝对路径", info.Error);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Directory_without_manifest_is_reported_as_invalid()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Stray"));
            using var host = CreateHost(root);

            host.LoadAll();

            var info = Assert.Single(host.Plugins);
            Assert.Equal(PluginState.Invalid, info.State);
            Assert.Contains(ErrorCodes.PluginManifestInvalid, info.Error);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Incompatible_plugin_does_not_block_other_plugins()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.WriteManifest(root, "Future",
                """{"id":"future-plugin","name":"未来插件","version":"1.0.0","hostVersionRange":">=9.0.0"}""");
            PluginTestFixtures.InstallSamplePlugin(root);

            using var host = CreateHost(root);
            host.LoadAll();

            Assert.Equal(2, host.Plugins.Count);
            Assert.Contains(host.Plugins, p => p is { Id: "future-plugin", State: PluginState.Incompatible });
            Assert.Contains(host.Plugins, p => p is { Id: "sample-plugin", State: PluginState.Loaded });
            Assert.Single(host.Catalog.FrameParsers);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Broken_plugin_does_not_block_other_plugins()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.WriteManifest(root, "Broken", "{ this is not json }");
            PluginTestFixtures.InstallSamplePlugin(root);

            using var host = CreateHost(root);
            host.LoadAll();

            Assert.Equal(2, host.Plugins.Count);
            Assert.Equal(PluginState.Loaded, host.Plugins.Single(p => p.Id == "sample-plugin").State);
            Assert.Equal(PluginState.Invalid, host.Plugins.Single(p => p.Id == "Broken").State);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Register_timeout_is_isolated_and_counted()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.InstallSamplePlugin(root, slowRegister: true);
            var store = new InMemoryPluginStateStore();
            using var host = CreateHost(root, store, timeoutMs: 250);

            host.LoadAll();

            var info = Assert.Single(host.Plugins);
            Assert.Equal(PluginState.Failed, info.State);
            Assert.Contains("超时", info.Error);
            Assert.Equal(1, store.GetFailureCount("sample-plugin"));
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Validate_all_does_not_load_anything()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.InstallSamplePlugin(root);
            using var host = CreateHost(root);

            var validated = host.ValidateAll();

            var info = Assert.Single(validated);
            Assert.Equal(PluginState.NotLoaded, info.State);
            Assert.True(host.Catalog.IsEmpty);
            Assert.Empty(host.Plugins);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Load_all_is_idempotent()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.InstallSamplePlugin(root);
            using var host = CreateHost(root);

            host.LoadAll();
            host.LoadAll();

            Assert.Single(host.Plugins);
            Assert.Single(host.Catalog.FrameParsers);
            Assert.Equal(2, host.Catalog.Registrations.Count);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Catalog_can_find_instance_by_name()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.InstallSamplePlugin(root);
            using var host = CreateHost(root);
            host.LoadAll();

            Assert.NotNull(host.Catalog.FindInstance<IFrameParser>("sample-frame-summary"));
            Assert.Null(host.Catalog.FindInstance<IFrameParser>("nope"));
            Assert.Single(host.Catalog.Exporters);
            Assert.Equal("sample-tsv", host.Catalog.Exporters[0].Format);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Sample_parser_and_exporter_produce_text()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.InstallSamplePlugin(root);
            using var host = CreateHost(root);
            host.LoadAll();

            var parser = host.Catalog.FrameParsers[0];
            var record = PluginTestFixtures.Frame(new byte[] { 0x01, 0x03, 0x02, 0x00, 0x0A });
            Assert.True(parser.CanParse(record));

            var parsed = parser.Parse(record);
            Assert.NotNull(parsed);
            Assert.Contains("示例插件", parsed);

            var exporter = host.Catalog.Exporters[0].Exporter;
            var text = exporter(new[] { record }, "测试导出");
            Assert.Contains("测试导出", text);
            Assert.Contains("010302000A", text.Replace(" ", string.Empty));
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void Dispose_unloads_loaded_plugins()
    {
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            PluginTestFixtures.InstallSamplePlugin(root);
            var host = CreateHost(root);
            host.LoadAll();
            Assert.Equal(1, host.LoadedCount);

            host.Dispose();

            Assert.True(host.Catalog.IsEmpty);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }

    [Fact]
    public void System_plugin_directory_is_never_touched_by_tests()
    {
        // SR-7 的回归护栏：宿主的受信目录完全由构造参数决定，测试只使用临时目录。
        var root = PluginTestFixtures.CreatePluginRoot();
        try
        {
            Directory.CreateDirectory(root);
            using var host = CreateHost(root);

            Assert.Equal(Path.GetFullPath(root), host.PluginDirectory);
        }
        finally
        {
            PluginTestFixtures.Delete(root);
        }
    }
}
