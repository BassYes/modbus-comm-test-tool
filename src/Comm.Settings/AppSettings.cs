using System.Text.Json;
using System.Text.Json.Serialization;
using Comm.Core.Transport;
using Comm.Diagnostics;
using Comm.Protocols.Modbus;
using Comm.Scanning;

namespace Comm.Settings;

/// <summary>串口连接模板（见规划 7.1）。</summary>
public sealed record SerialProfile
{
    public string Name { get; init; } = "默认串口";
    public string PortName { get; init; } = "COM1";
    public int BaudRate { get; init; } = 9600;
    public SerialParity Parity { get; init; } = SerialParity.None;
    public int DataBits { get; init; } = 8;
    public SerialStopBits StopBits { get; init; } = SerialStopBits.One;
    public int ResponseTimeoutMs { get; init; } = 1000;
    public int RetryCount { get; init; } = 2;
    public int TurnaroundDelayMs { get; init; } = 20;
    public bool SuppressEcho { get; init; } = true;

    public SerialSettings ToSerialSettings() => new()
    {
        PortName = PortName,
        BaudRate = BaudRate,
        DataBits = DataBits,
        Parity = Parity,
        StopBits = StopBits
    };
}

public sealed record TcpProfile
{
    public string Name { get; init; } = "默认 TCP";
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 502;
    public byte UnitId { get; init; } = 1;
    public int ConnectTimeoutMs { get; init; } = 3000;
    public int ResponseTimeoutMs { get; init; } = 1000;
    public int RetryCount { get; init; } = 1;

    /// <summary>TCP 请求之间的最小间隔（毫秒）；默认 0（TCP 不需要 RTU 的轮转延迟）。</summary>
    public int TurnaroundDelayMs { get; init; }

    public TcpSettings ToTcpSettings() => new()
    {
        Host = Host,
        Port = Port,
        ConnectTimeoutMs = ConnectTimeoutMs
    };
}

/// <summary>TcpProfile 的辅助扩展（保持模型不可变）。</summary>
public static class TcpProfileExtensions
{
    /// <summary>把可能为负的间隔规范为 0（TCP 默认不需要轮转延迟）。</summary>
    public static int TurnaroundDelayMsOrZero(this TcpProfile profile) => profile is null ? 0 : Math.Max(0, profile.TurnaroundDelayMs);
}

public sealed record MonitorSettings
{
    /// <summary>落盘与总线显示缓冲上限。</summary>
    public int DisplayCapacity { get; init; } = 200_000;

    /// <summary>界面侧保留行数（M1 欠账 D-3：原为硬编码 20 000）。</summary>
    public int UiCapacity { get; init; } = 20_000;

    public bool ParseEnabled { get; init; } = true;
    public bool AutoScroll { get; init; } = true;

    /// <summary>TX/RX 方向着色（M1 欠账 D-2：原定义未接线）。</summary>
    public bool ColorizeDirections { get; init; } = true;
}

public sealed record ScanDefaults
{
    public byte ScanStartAddress { get; init; } = 1;
    public byte ScanEndAddress { get; init; } = 247;
    public int ScanResponseTimeoutMs { get; init; } = 500;
    public int ScanIntervalMs { get; init; } = 20;
    public DataArea DefaultArea { get; init; } = DataArea.HoldingRegisters;
    public DataType DefaultDataType { get; init; } = DataType.UInt16;
    public ByteOrder DefaultByteOrder { get; init; } = ByteOrder.Abcd;
    public AddressStyle DefaultAddressStyle { get; init; } = AddressStyle.ProtocolZeroBased;

    /// <summary>局域网扫描地址上限（超过需用户确认，见规划 4.7.1）。</summary>
    public int MaxLanScanAddresses { get; init; } = 4096;

    /// <summary>局域网扫描并发度（16 ~ 256）。</summary>
    public int LanScanConcurrency { get; init; } = 64;

    /// <summary>局域网扫描单主机超时。</summary>
    public int LanScanPerHostTimeoutMs { get; init; } = 800;

    /// <summary>监视表刷新周期（毫秒，不低于 200）。</summary>
    public int WatchTableIntervalMs { get; init; } = 1000;
}

/// <summary>设备目标类型（见规划 7.2）。</summary>
public enum ConnectionKindHint
{
    Serial,
    Tcp
}

/// <summary>
/// 网卡配置方案（M3a，规划 4.8.4 / 7.6）。
/// 只保存用户意图，不保存“当前生效值”；实际生效值以提权助手回读结果为准。
/// </summary>
public sealed record NetworkProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = string.Empty;

    /// <summary>目标网卡匹配方式：适配器 Id（GUID）优先，其次名称包含匹配。</summary>
    public string AdapterMatcher { get; init; } = string.Empty;

    public bool DhcpEnabled { get; init; }
    public string? Ipv4 { get; init; }
    public int PrefixLength { get; init; } = 24;
    public string? Gateway { get; init; }
    public string? Dns1 { get; init; }
    public string? Dns2 { get; init; }
    public bool ApplyDns { get; init; }
    public string? Notes { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public string DisplayText => string.IsNullOrWhiteSpace(Name)
        ? (DhcpEnabled ? $"DHCP（{AdapterMatcher}）" : $"{Ipv4}/{PrefixLength}（{AdapterMatcher}）")
        : Name;
}

/// <summary>网卡配置行为设置（M3a，规划 4.8.3、15.8）。</summary>
public sealed record NetConfigSettings
{
    /// <summary>“安全恢复倒计时”默认关闭；勾选后默认 60 秒（D-6）。</summary>
    public bool SafetyRollbackEnabled { get; init; }

    public int SafetyRollbackSeconds { get; init; } = 60;

    /// <summary>应用前是否检查目标 IP 是否已被占用（可选 ARP 探测，默认关）。</summary>
    public bool ProbeAddressConflict { get; init; }

    /// <summary>提权助手空闲退出时间（分钟，规划 12.5 默认 5）。</summary>
    public int HelperIdleExitMinutes { get; init; } = 5;
}

/// <summary>插件宿主设置（M3b，规划 15.11）：被自动禁用的插件在用户手动启用前保持禁用。</summary>
public sealed record PluginSettings
{
    /// <summary>被禁用（或自动禁用）的插件 Id。</summary>
    public IReadOnlyList<string> DisabledPlugins { get; init; } = Array.Empty<string>();

    /// <summary>每个插件的连续失败次数（规划 15.11：达到 3 次自动禁用，且跨次启动累计）。</summary>
    public IReadOnlyDictionary<string, int> FailureCounts { get; init; } = new Dictionary<string, int>();

    public bool LoadOnStartup { get; init; } = true;
}

/// <summary>可拆分窗口的布局（M3b，规划 21.2.2）：每个会话独立保存位置与尺寸。</summary>
public sealed record SessionWindowLayout
{
    public string SessionId { get; init; } = string.Empty;
    public double Left { get; init; } = double.NaN;
    public double Top { get; init; } = double.NaN;
    public double Width { get; init; } = 1180;
    public double Height { get; init; } = 780;
    public bool Maximized { get; init; }
    public string? MonitorDeviceName { get; init; }
}

/// <summary>会话与工作区设置（M3b）。</summary>
public sealed record SessionSettings
{
    /// <summary>并行会话上限（规划 4.1.3，默认 8）。</summary>
    public int MaxSessions { get; init; } = 8;

    /// <summary>上次打开的工作区/工程文件（规划 4.12）。</summary>
    public string? LastWorkspaceFile { get; init; }

    /// <summary>启动时自动重新打开上次的工作区文件。</summary>
    public bool ReopenLastWorkspace { get; init; }

    /// <summary>已拆分为独立窗口的会话及其窗口布局。</summary>
    public IReadOnlyList<SessionWindowLayout> WindowLayouts { get; init; } = Array.Empty<SessionWindowLayout>();
}

/// <summary>常用设备目标：把“某一台设备”的连接信息与 Unit ID 存下来复用（规划 7.2、4.12）。</summary>
public sealed record DeviceTarget
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = string.Empty;
    public ConnectionKindHint Kind { get; init; } = ConnectionKindHint.Serial;

    /// <summary><c>COM3</c> 或 <c>192.168.1.10:502</c>。</summary>
    public string Endpoint { get; init; } = string.Empty;

    public byte UnitId { get; init; } = 1;
    public string? Notes { get; init; }
    public string[] Tags { get; init; } = Array.Empty<string>();
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public string DisplayText => string.IsNullOrWhiteSpace(Name) ? $"{Endpoint} (Unit {UnitId})" : $"{Name}（{Endpoint}）";
}

/// <summary>扫描方案类型（<see cref="ScanPlan.PlanKind"/> 的取值）。</summary>
public static class ScanPlanKinds
{
    public const string Device = "device";
    public const string Data = "data";
    public const string Locate = "locate";
    public const string Lan = "lan";
    /// <summary>TCP Unit ID 扫描方案（R-12）。</summary>
    public const string UnitId = "unitid";

    public static string ToChinese(string kind) => kind switch
    {
        Device => "设备扫描",
        Data => "数据扫描",
        Locate => "寄存器定位",
        Lan => "局域网扫描",
        UnitId => "Unit ID 扫描",
        _ => kind
    };
}

/// <summary>
/// 扫描方案（规划 4.12、7.3）：只保存“选项快照”，不保存结果。
/// 用 <see cref="JsonElement"/> 保存，这样以后给选项加字段也不会让旧方案读不出来（缺字段走默认值）。
/// </summary>
public sealed record ScanPlan
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = string.Empty;
    public string? DeviceTargetId { get; init; }
    public string PlanKind { get; init; } = ScanPlanKinds.Device;
    public JsonElement Payload { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public string KindText => ScanPlanKinds.ToChinese(PlanKind);

    public string DisplayText => string.IsNullOrWhiteSpace(Name) ? KindText : Name;

    /// <summary>把选项对象写入方案的负载。</summary>
    public static ScanPlan Create(string name, string kind, object options, string? deviceTargetId = null) => new()
    {
        Name = name,
        PlanKind = kind,
        DeviceTargetId = deviceTargetId,
        Payload = JsonSerializer.SerializeToElement(options, SettingsJson.Options),
        UpdatedAt = DateTimeOffset.Now
    };

    /// <summary>读取方案负载；字段缺失或方案损坏时返回 null（界面提示后按默认值处理）。</summary>
    public T? ReadPayload<T>() where T : class
    {
        if (Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;

        try
        {
            return Payload.Deserialize<T>(SettingsJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>常用报文模板（规划 4.5.4、6.5）。写模板必须保留风险标签，重发时要再次确认。</summary>
public sealed record FrameTemplate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = string.Empty;

    /// <summary><c>rtu</c> 或 <c>tcp</c>。</summary>
    public string Transport { get; init; } = "rtu";

    public string Hex { get; init; } = string.Empty;

    /// <summary>写功能码模板；载入与重发时必须再次确认（规划 6.5）。</summary>
    public bool IsWrite { get; init; }

    public string? Notes { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public string TransportText => Transport.Equals("tcp", StringComparison.OrdinalIgnoreCase) ? "Modbus TCP" : "Modbus RTU";

    public string RiskText => IsWrite ? "写操作（重发会再次修改设备数据）" : "读操作";

    public string DisplayText => string.IsNullOrWhiteSpace(Name)
        ? $"{TransportText} {Hex}"
        : $"{Name}（{TransportText}{(IsWrite ? " · 写" : string.Empty)}）";
}

/// <summary>设置 JSON 序列化选项。方案负载与设置文件共用，保证枚举以名称保存、便于人工阅读。</summary>
public static class SettingsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>应用设置（带 schemaVersion，见 AD-10）。</summary>
public sealed record AppSettings
{
    /// <summary>当前程序支持的设置文件版本。M2 起为 2（新增方案 / 模板 / 设备目标）。</summary>
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public LogOptions Log { get; init; } = new();
    public SerialProfile Serial { get; init; } = new();
    public TcpProfile Tcp { get; init; } = new();
    public MonitorSettings Monitor { get; init; } = new();
    public ScanDefaults Scan { get; init; } = new();
    public string? LastExportDirectory { get; init; }
    public string? LastCaptureDirectory { get; init; }

    /// <summary>常用设备目标（规划 7.2）。</summary>
    public IReadOnlyList<DeviceTarget> Targets { get; init; } = Array.Empty<DeviceTarget>();

    /// <summary>扫描方案（规划 4.12）。</summary>
    public IReadOnlyList<ScanPlan> Plans { get; init; } = Array.Empty<ScanPlan>();

    /// <summary>报文模板（规划 4.5.4）。</summary>
    public IReadOnlyList<FrameTemplate> Templates { get; init; } = Array.Empty<FrameTemplate>();

    /// <summary>网卡配置方案（M3a，规划 4.8.4）。</summary>
    public IReadOnlyList<NetworkProfile> NetworkProfiles { get; init; } = Array.Empty<NetworkProfile>();

    /// <summary>网卡配置行为设置（M3a）。</summary>
    public NetConfigSettings NetConfig { get; init; } = new();

    /// <summary>插件宿主设置（M3b，规划 15.11）。</summary>
    public PluginSettings Plugins { get; init; } = new();

    /// <summary>会话与工作区设置（M3b，规划 21.2）。</summary>
    public SessionSettings Sessions { get; init; } = new();
}

/// <summary>
/// 设置存储：原子写入、损坏文件保留、版本迁移（见规划 15.10）。
/// ⚠️ 迁移规则：只接受 <see cref="AppSettings.CurrentSchemaVersion"/> 及更低的版本，
/// 更低版本走 <see cref="Migrate"/>，更高版本按只读方式使用默认值并提示。
/// </summary>
public sealed class SettingsStore
{
    public SettingsStore(string? filePath = null)
    {
        // R-21 X-2：默认落在 AppPaths 解析出的数据目录（便携优先，不可写时回退 %LOCALAPPDATA%）。
        Path = filePath ?? System.IO.Path.Combine(AppPaths.DataDirectory, "settings.json");
    }

    public string Path { get; }

    public AppSettings Load(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(Path)) return new AppSettings();

            var json = File.ReadAllText(Path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, SettingsJson.Options);
            if (settings is null)
            {
                warning = "设置文件内容为空，已使用默认设置";
                return new AppSettings();
            }

            if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                warning = $"设置文件版本（{settings.SchemaVersion}）高于当前程序支持的版本" +
                          $"（{AppSettings.CurrentSchemaVersion}），已按只读方式使用默认值。";
                return new AppSettings();
            }

            var migrated = Migrate(settings, out warning);
            if (migrated.SchemaVersion != settings.SchemaVersion)
            {
                // 迁移结果立即落盘：下次启动不再重复提示（M2 验收：老设置文件升级后无告警）。
                Save(migrated);
            }

            return migrated;
        }
        catch (Exception ex)
        {
            warning = $"设置文件损坏或无法读取（{ex.Message}），已使用默认设置；原文件已保留为 .bad 备份。";
            try
            {
                File.Copy(Path, $"{Path}.bad-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            }
            catch (Exception)
            {
                // 备份失败不影响启动
            }
            return new AppSettings();
        }
    }

    /// <summary>把旧版本设置升级到当前版本：新增字段一律取默认值，原有字段不丢。</summary>
    public static AppSettings Migrate(AppSettings settings, out string? warning)
    {
        warning = null;
        var migrated = settings;

        if (migrated.SchemaVersion < 2)
        {
            // v1 → v2：新增 Targets / Plans / Templates 集合，其余字段原样保留。
            migrated = migrated with { SchemaVersion = 2 };
            warning = "设置文件已从 v1 升级到 v2（新增扫描方案、报文模板与设备目标；原有参数保持不变）。";
        }

        return migrated;
    }

    public string? Save(AppSettings settings)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var payload = settings with { SchemaVersion = AppSettings.CurrentSchemaVersion };
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(payload, SettingsJson.Options));
            File.Move(temp, Path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return $"保存设置失败：{ex.Message}";
        }
    }
}
