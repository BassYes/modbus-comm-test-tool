using System.Text.Json;
using Comm.Scanning;
using Comm.Settings;
using Xunit;

namespace Comm.Infrastructure.Tests;

/// <summary>
/// 工作区/项目文件（规划 4.12、AD-10、15.10）：往返、原子写、损坏与高版本处理、合并语义、校验。
/// </summary>
public class WorkspaceFileTests
{
    private static string CreateDirectory()
    {
        // 与 FrameFileWriterTests 一致：放在测试输出目录下（本机受限环境下系统 TEMP 对子进程可能不可写）。
        var directory = Path.Combine(AppContext.BaseDirectory, "workspace-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static WorkspaceFile SampleFile(string name = "现场 A 站") => new()
    {
        Name = name,
        Description = "M3b 工作区测试",
        AppVersion = "0.3.0",
        Contents = new WorkspaceContents
        {
            SerialProfiles = new[] { new SerialProfile { Name = "串口模板", PortName = "COM5", BaudRate = 19200 } },
            TcpProfiles = new[] { new TcpProfile { Name = "TCP 模板", Host = "192.168.1.10" } },
            Targets = new[]
            {
                new DeviceTarget { Id = "t1", Name = "1 号表", Endpoint = "COM5", UnitId = 5 },
                new DeviceTarget { Id = "t2", Name = "网关", Endpoint = "192.168.1.10:502", UnitId = 1 }
            },
            Plans = new[] { ScanPlan.Create("设备扫描方案", ScanPlanKinds.Device, new { Start = 1 }) },
            Templates = new[] { new FrameTemplate { Id = "f1", Name = "读保持寄存器", Hex = "010300000001" } },
            WatchPoints = new[]
            {
                new RegisterPoint { Id = "p1", Name = "A 相电压", Address = 100, UnitId = 5 },
                new RegisterPoint { Id = "p2", Name = "频率", Address = 102, UnitId = 5 }
            },
            NetworkProfiles = new[] { new NetworkProfile { Id = "n1", Name = "调试网段", AdapterMatcher = "eth0", Ipv4 = "192.168.9.10", PrefixLength = 24 } }
        }
    };

    [Fact]
    public void Save_and_load_round_trips_every_collection()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new WorkspaceStore(WorkspaceStore.DefaultPath(directory, "现场 A 站"));
            var original = SampleFile();

            Assert.Null(store.Save(original));

            var loaded = store.Load(out var warning);
            Assert.True(loaded.IsSuccess);
            Assert.Null(warning);

            var file = loaded.Value!;
            Assert.Equal("现场 A 站", file.Name);
            Assert.Equal(2, file.Contents.WatchPoints.Count);
            Assert.Equal(2, file.Contents.Targets.Count);
            Assert.Single(file.Contents.Plans);
            Assert.Equal(19200, file.Contents.SerialProfiles[0].BaudRate);
            Assert.Equal("192.168.9.10", file.Contents.NetworkProfiles[0].Ipv4);
            Assert.Equal(WorkspaceDefaults.CurrentSchemaVersion, file.SchemaVersion);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Default_path_uses_the_ctws_extension()
    {
        var directory = CreateDirectory();
        try
        {
            var path = WorkspaceStore.DefaultPath(directory, "某站/调试");
            Assert.EndsWith(WorkspaceDefaults.Extension, path);
            Assert.DoesNotContain("/", Path.GetFileName(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Atomic_write_leaves_no_temporary_file()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "atomic" + WorkspaceDefaults.Extension);
            var store = new WorkspaceStore(path);

            // 连续保存两次（覆盖路径也要原子）。
            Assert.Null(store.Save(SampleFile("第一次")));
            Assert.Null(store.Save(SampleFile("第二次")));

            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal("第二次", store.Load(out _).Value!.Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_yields_an_empty_workspace_without_warning()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new WorkspaceStore(Path.Combine(directory, "new" + WorkspaceDefaults.Extension));
            var result = store.Load(out var warning);

            Assert.True(result.IsSuccess);
            Assert.Null(warning);
            Assert.True(result.Value!.Contents.IsEmpty);
            Assert.Equal("new", result.Value.Name);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_reported()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "broken" + WorkspaceDefaults.Extension);
            File.WriteAllText(path, "{ this is not valid json");

            var store = new WorkspaceStore(path);
            var result = store.Load(out var warning);

            Assert.True(result.IsSuccess);
            Assert.NotNull(warning);
            Assert.Contains("损坏", warning);
            Assert.True(result.Value!.Contents.IsEmpty);

            var backups = Directory.GetFiles(directory, "broken*.bad-*");
            Assert.Single(backups);
            Assert.Equal("{ this is not valid json", File.ReadAllText(backups[0]));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Higher_schema_version_opens_read_only_and_is_never_overwritten()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "future" + WorkspaceDefaults.Extension);
            var future = SampleFile("未来版本") with { SchemaVersion = WorkspaceDefaults.CurrentSchemaVersion + 1 };
            File.WriteAllText(path, JsonSerializer.Serialize(future, SettingsJson.Options));
            var originalContent = File.ReadAllText(path);

            var store = new WorkspaceStore(path);
            var result = store.Load(out var warning);

            Assert.True(result.IsSuccess);
            Assert.NotNull(warning);
            Assert.Contains("只读", warning);
            Assert.True(store.LastLoadWasReadOnly);

            // 内容照常可见（不丢用户数据），但保存必须被拒绝，原文件不得被改写。
            Assert.Equal("未来版本", result.Value!.Name);
            var saveError = store.Save(result.Value!);
            Assert.NotNull(saveError);
            Assert.Equal(originalContent, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Export_writes_a_loadable_copy()
    {
        var directory = CreateDirectory();
        try
        {
            var store = new WorkspaceStore(Path.Combine(directory, "source" + WorkspaceDefaults.Extension));
            var destination = Path.Combine(directory, "导出目录 带空格", "现场 A 站" + WorkspaceDefaults.Extension);

            Assert.Null(store.Export(SampleFile(), destination));
            Assert.True(File.Exists(destination));
            Assert.False(File.Exists(destination + ".tmp"));

            var copy = new WorkspaceStore(destination).Load(out var warning).Value!;
            Assert.Null(warning);
            Assert.Equal(2, copy.Contents.WatchPoints.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void From_settings_captures_the_persistable_objects()
    {
        var settings = new AppSettings
        {
            Serial = new SerialProfile { PortName = "COM7", BaudRate = 4800 },
            Targets = new[] { new DeviceTarget { Id = "t9", Endpoint = "COM7" } },
            Templates = new[] { new FrameTemplate { Id = "f9", Hex = "010300000001" } }
        };

        var file = WorkspaceStore.FromSettings("从设置生成", settings, "说明", "0.3.0");

        Assert.Equal("从设置生成", file.Name);
        Assert.Equal("COM7", file.Contents.SerialProfiles[0].PortName);
        Assert.Single(file.Contents.Targets);
        Assert.Single(file.Contents.Templates);
    }

    [Fact]
    public void Apply_to_replace_swaps_collections_but_keeps_runtime_settings()
    {
        var file = SampleFile();
        var settings = new AppSettings
        {
            Log = new Comm.Diagnostics.LogOptions { Enabled = true, CrashReportsToKeep = 3 },
            Monitor = new MonitorSettings { UiCapacity = 1234 },
            Scan = new ScanDefaults { LanScanConcurrency = 32 },
            LastExportDirectory = "E:/导出",
            Targets = new[] { new DeviceTarget { Id = "old", Endpoint = "COM1" } }
        };

        var applied = WorkspaceStore.ApplyTo(file, settings, WorkspaceMergeMode.Replace);

        Assert.Equal(2, applied.Targets.Count);
        Assert.DoesNotContain(applied.Targets, t => t.Id == "old");
        Assert.Equal(19200, applied.Serial.BaudRate);

        // 监视点位是会话作用域的（M3b 起每个会话有自己的监视表），因此不在 AppSettings 里，
        // 而是保留在工作区内容中，由界面在导入后加入监视表。
        Assert.Equal(2, file.Contents.WatchPoints.Count);

        // 运行期设置必须原样保留（规划 4.12：工作区只管可持久化对象）。
        Assert.True(applied.Log.Enabled);
        Assert.Equal(3, applied.Log.CrashReportsToKeep);
        Assert.Equal(1234, applied.Monitor.UiCapacity);
        Assert.Equal(32, applied.Scan.LanScanConcurrency);
        Assert.Equal("E:/导出", applied.LastExportDirectory);
        Assert.Equal(AppSettings.CurrentSchemaVersion, applied.SchemaVersion);
    }

    [Fact]
    public void Apply_to_merge_keeps_target_only_items_and_overrides_same_ids()
    {
        var incoming = SampleFile() with
        {
            Contents = SampleFile().Contents with
            {
                Targets = new[]
                {
                    new DeviceTarget { Id = "t1", Name = "被工作区覆盖", Endpoint = "COM9", UnitId = 9 },
                    new DeviceTarget { Id = "t3", Name = "工作区新增", Endpoint = "COM3" }
                }
            }
        };

        var settings = new AppSettings
        {
            Targets = new[]
            {
                new DeviceTarget { Id = "t1", Name = "原有 1", Endpoint = "COM1" },
                new DeviceTarget { Id = "t2", Name = "原有 2", Endpoint = "COM2" }
            }
        };

        var applied = WorkspaceStore.ApplyTo(incoming, settings, WorkspaceMergeMode.Merge);

        Assert.Equal(3, applied.Targets.Count);
        Assert.Equal("被工作区覆盖", applied.Targets.Single(t => t.Id == "t1").Name);
        Assert.Contains(applied.Targets, t => t.Id == "t2");
        Assert.Contains(applied.Targets, t => t.Id == "t3");
        Assert.Equal(applied.Targets.Select(t => t.Id).Distinct().Count(), applied.Targets.Count);
    }

    [Fact]
    public void From_settings_then_apply_to_round_trips()
    {
        var original = new AppSettings
        {
            Serial = new SerialProfile { PortName = "COM3", BaudRate = 9600 },
            Targets = new[] { new DeviceTarget { Id = "a", Endpoint = "COM3" } },
            Plans = new[] { ScanPlan.Create("方案", ScanPlanKinds.Data, new { Start = 0 }) },
            NetworkProfiles = new[] { new NetworkProfile { Id = "n", Name = "网段", AdapterMatcher = "x", Ipv4 = "10.0.0.5" } }
        };

        var file = WorkspaceStore.FromSettings("往返", original);
        var restored = WorkspaceStore.ApplyTo(file, new AppSettings(), WorkspaceMergeMode.Replace);

        Assert.Equal("COM3", restored.Serial.PortName);
        Assert.Single(restored.Targets);
        Assert.Single(restored.Plans);
        Assert.Single(restored.NetworkProfiles);
    }

    [Fact]
    public void Validator_accepts_a_sound_workspace()
    {
        Assert.Empty(WorkspaceValidator.Validate(SampleFile()));
    }

    [Fact]
    public void Validator_reports_duplicate_ids_and_empty_name()
    {
        var file = SampleFile("") with
        {
            Contents = SampleFile().Contents with
            {
                Targets = new[]
                {
                    new DeviceTarget { Id = "dup", Endpoint = "COM1" },
                    new DeviceTarget { Id = "dup", Endpoint = "COM2" }
                }
            }
        };

        var issues = WorkspaceValidator.Validate(file);

        Assert.Contains(issues, i => i.Contains("名称为空"));
        Assert.Contains(issues, i => i.Contains("重复 Id"));
    }

    [Fact]
    public void Validator_reports_missing_endpoint_and_static_profile_without_address()
    {
        var file = SampleFile() with
        {
            Contents = SampleFile().Contents with
            {
                Targets = new[] { new DeviceTarget { Id = "t", Endpoint = "   " } },
                NetworkProfiles = new[] { new NetworkProfile { Id = "n", Name = "静态无地址", AdapterMatcher = "x" } }
            }
        };

        var issues = WorkspaceValidator.Validate(file);

        Assert.Contains(issues, i => i.Contains("缺少端点"));
        Assert.Contains(issues, i => i.Contains("没有填写 IPv4"));
    }

    [Fact]
    public void Validator_reports_bad_payload_and_out_of_range_point()
    {
        var file = SampleFile() with
        {
            Contents = SampleFile().Contents with
            {
                Plans = new[] { new ScanPlan { Id = "p", Name = "坏方案", Payload = JsonSerializer.SerializeToElement(new[] { 1, 2 }) } },
                WatchPoints = new[] { new RegisterPoint { Id = "w", Name = "越界", Address = 70000 } }
            }
        };

        var issues = WorkspaceValidator.Validate(file);

        Assert.Contains(issues, i => i.Contains("JSON 对象"));
        Assert.Contains(issues, i => i.Contains("地址越界"));
    }

    [Fact]
    public void Sanitize_replaces_invalid_file_name_characters()
    {
        var sanitized = WorkspaceStore.Sanitize("A/B:C*D?E");

        Assert.DoesNotContain('/', sanitized);
        Assert.DoesNotContain(':', sanitized);
        Assert.DoesNotContain('*', sanitized);
        Assert.DoesNotContain('?', sanitized);
    }

    [Fact]
    public void Summary_text_mentions_the_collections()
    {
        var text = SampleFile().SummaryText;

        Assert.Contains("点位 2", text);
        Assert.Contains("方案 1", text);
        Assert.Contains("设备 2", text);
    }
}
