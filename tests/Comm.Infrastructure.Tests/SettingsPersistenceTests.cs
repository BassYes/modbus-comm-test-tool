using System.Text.Json;
using Comm.Core.Transport;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Settings;
using Xunit;

namespace Comm.Infrastructure.Tests;

/// <summary>
/// 设置持久化与迁移（M2-3，规划 4.12、15.10）：
/// 扫描方案 / 报文模板 / 设备目标必须能往返，v1 文件必须能升级且不丢原字段，更高版本文件只能只读。
/// </summary>
public class SettingsPersistenceTests
{
    private static string TempPath() => Path.Combine(AppContext.BaseDirectory, "test-output", $"settings-{Guid.NewGuid():N}.json");

    private static string EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return path;
    }

    [Fact]
    public void Plans_templates_and_targets_round_trip()
    {
        var path = EnsureDirectory(TempPath());
        try
        {
            var store = new SettingsStore(path);
            var dataOptions = new DataScanOptions
            {
                UnitId = 7,
                Area = DataArea.InputRegisters,
                StartAddress = 100,
                EndAddress = 140,
                Type = DataType.Float32,
                Order = ByteOrder.Cdab,
                Scale = 0.1,
                IntervalMs = 5
            };

            var settings = new AppSettings
            {
                Serial = new SerialProfile { PortName = "COM7", BaudRate = 19200 },
                Plans = new[]
                {
                    ScanPlan.Create("电表 40001", ScanPlanKinds.Data, dataOptions),
                    ScanPlan.Create("整段扫描", ScanPlanKinds.Device, new DeviceScanOptions { StartAddress = 1, EndAddress = 32 })
                },
                Templates = new[]
                {
                    new FrameTemplate { Name = "读 1 个寄存器", Transport = "rtu", Hex = "01 03 00 00 00 01 84 0A" },
                    new FrameTemplate { Name = "写单个寄存器", Transport = "rtu", Hex = "01 06 00 05 12 34", IsWrite = true }
                },
                Targets = new[]
                {
                    new DeviceTarget { Name = "1 号电表", Kind = ConnectionKindHint.Serial, Endpoint = "COM7", UnitId = 7 },
                    new DeviceTarget { Name = "网关", Kind = ConnectionKindHint.Tcp, Endpoint = "192.168.1.50:502", UnitId = 1 }
                }
            };

            Assert.Null(store.Save(settings));
            var loaded = new SettingsStore(path).Load(out var warning);

            Assert.Null(warning);
            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal("COM7", loaded.Serial.PortName);
            Assert.Equal(19200, loaded.Serial.BaudRate);

            // 方案负载必须能反序列化回选项对象（缺字段走默认值）
            Assert.Equal(2, loaded.Plans.Count);
            var plan = loaded.Plans.Single(p => p.Name == "电表 40001");
            Assert.Equal(ScanPlanKinds.Data, plan.PlanKind);
            var payload = plan.ReadPayload<DataScanOptions>();
            Assert.NotNull(payload);
            Assert.Equal(dataOptions, payload);

            Assert.Equal(2, loaded.Templates.Count);
            Assert.True(loaded.Templates.Single(t => t.Name == "写单个寄存器").IsWrite);
            Assert.Contains("写操作", loaded.Templates.Single(t => t.Name == "写单个寄存器").RiskText);

            Assert.Equal(2, loaded.Targets.Count);
            Assert.Equal("192.168.1.50:502", loaded.Targets.Single(t => t.Kind == ConnectionKindHint.Tcp).Endpoint);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Plan_payload_survives_new_option_fields()
    {
        // 方案里存的是选项快照：即使以后给选项加字段，旧方案仍要能读出来（缺字段走默认值）
        var path = EnsureDirectory(TempPath());
        try
        {
            File.WriteAllText(path, """
            {
              "schemaVersion": 2,
              "plans": [
                {
                  "id": "abc12345",
                  "name": "旧方案",
                  "planKind": "data",
                  "payload": { "unitId": 3, "startAddress": 10, "endAddress": 20, "area": "HoldingRegisters" },
                  "updatedAt": "2026-01-01T00:00:00+08:00"
                }
              ]
            }
            """);

            var settings = new SettingsStore(path).Load(out var warning);
            Assert.Null(warning);

            var options = Assert.Single(settings.Plans).ReadPayload<DataScanOptions>();
            Assert.NotNull(options);
            Assert.Equal(3, options!.UnitId);
            Assert.Equal(10, options.StartAddress);
            Assert.Equal(DataType.UInt16, options.Type);   // 缺字段 → 默认值
            Assert.Equal(1000, options.ResponseTimeoutMs);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Version_1_file_is_migrated_without_losing_existing_values()
    {
        var path = EnsureDirectory(TempPath());
        try
        {
            File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "serial": { "portName": "COM9", "baudRate": 4800, "dataBits": 8 },
              "tcp": { "host": "10.0.0.9", "port": 1502, "unitId": 5 },
              "scan": { "maxLanScanAddresses": 512 }
            }
            """);

            var settings = new SettingsStore(path).Load(out var warning);

            Assert.NotNull(warning);
            Assert.Contains("v1", warning!);
            Assert.Equal(2, settings.SchemaVersion);
            Assert.Equal("COM9", settings.Serial.PortName);
            Assert.Equal(4800, settings.Serial.BaudRate);
            Assert.Equal("10.0.0.9", settings.Tcp.Host);
            Assert.Equal(1502, settings.Tcp.Port);
            Assert.Equal(512, settings.Scan.MaxLanScanAddresses);

            // v1 → v2 新增的集合为空但不能为 null
            Assert.NotNull(settings.Plans);
            Assert.NotNull(settings.Templates);
            Assert.NotNull(settings.Targets);
            Assert.Empty(settings.Plans);

            // 迁移结果保存后就是 v2，再读不再提示
            Assert.Null(new SettingsStore(path).Save(settings));
            var reloaded = new SettingsStore(path).Load(out var secondWarning);
            Assert.Null(secondWarning);
            Assert.Equal("COM9", reloaded.Serial.PortName);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Higher_version_file_is_read_only_with_defaults()
    {
        var path = EnsureDirectory(TempPath());
        try
        {
            File.WriteAllText(path, """
            { "schemaVersion": 99, "serial": { "portName": "COM1", "baudRate": 115200 } }
            """);

            var settings = new SettingsStore(path).Load(out var warning);

            Assert.NotNull(warning);
            Assert.Contains("高于当前程序支持的版本", warning!);
            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal(9600, settings.Serial.BaudRate); // 只读：使用默认值
            Assert.True(File.Exists(path));               // 原文件保留，不被覆盖
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Corrupted_file_is_backed_up_and_defaults_are_used()
    {
        var path = EnsureDirectory(TempPath());
        var backups = new List<string>();
        try
        {
            File.WriteAllText(path, "{ 这不是合法 JSON ");

            var settings = new SettingsStore(path).Load(out var warning);

            Assert.NotNull(warning);
            Assert.Contains("损坏", warning!);
            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);

            var directory = Path.GetDirectoryName(path)!;
            var name = Path.GetFileName(path);
            backups.AddRange(Directory.GetFiles(directory, $"{name}.bad-*"));
            Assert.NotEmpty(backups);
        }
        finally
        {
            foreach (var file in backups)
            {
                try { File.Delete(file); } catch (IOException) { }
            }
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Save_always_writes_the_current_schema_version()
    {
        var path = EnsureDirectory(TempPath());
        try
        {
            var store = new SettingsStore(path);
            Assert.Null(store.Save(new AppSettings { SchemaVersion = 1 }));

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(AppSettings.CurrentSchemaVersion, document.RootElement.GetProperty("SchemaVersion").GetInt32());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Missing_file_produces_defaults_without_warning()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-output", $"missing-{Guid.NewGuid():N}.json");
        var settings = new SettingsStore(path).Load(out var warning);

        Assert.Null(warning);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Empty(settings.Plans);
    }
}
