using System.Text.Json;
using Comm.Core;
using Comm.Core.Errors;
using Comm.Scanning;

namespace Comm.Settings;

/// <summary>工作区/项目文件的约定（规划 4.12）。</summary>
public static class WorkspaceDefaults
{
    /// <summary>工作区文件扩展名。</summary>
    public const string Extension = ".ctws";

    /// <summary>当前工作区文件格式版本（只增不改，见 AD-10）。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>默认文件名（未指定名称时使用）。</summary>
    public const string DefaultName = "未命名工作区";
}

/// <summary>导入工作区时的合并策略（规划 4.12）。</summary>
public enum WorkspaceMergeMode
{
    /// <summary>用工作区内容替换目标集合。</summary>
    Replace,

    /// <summary>按 Id/名称合并：同 Id 以工作区为准，目标独有的条目保留。</summary>
    Merge
}

/// <summary>工作区内容：可持久化对象的集合（规划 4.12 的全部对象）。</summary>
public sealed record WorkspaceContents
{
    public IReadOnlyList<SerialProfile> SerialProfiles { get; init; } = Array.Empty<SerialProfile>();
    public IReadOnlyList<TcpProfile> TcpProfiles { get; init; } = Array.Empty<TcpProfile>();
    public IReadOnlyList<DeviceTarget> Targets { get; init; } = Array.Empty<DeviceTarget>();
    public IReadOnlyList<ScanPlan> Plans { get; init; } = Array.Empty<ScanPlan>();
    public IReadOnlyList<FrameTemplate> Templates { get; init; } = Array.Empty<FrameTemplate>();
    public IReadOnlyList<RegisterPoint> WatchPoints { get; init; } = Array.Empty<RegisterPoint>();
    public IReadOnlyList<NetworkProfile> NetworkProfiles { get; init; } = Array.Empty<NetworkProfile>();

    public int TotalCount =>
        SerialProfiles.Count + TcpProfiles.Count + Targets.Count + Plans.Count +
        Templates.Count + WatchPoints.Count + NetworkProfiles.Count;

    public bool IsEmpty => TotalCount == 0;
}

/// <summary>
/// 工作区/项目文件（规划 4.12、AD-10）：把配置模板、设备目标、扫描方案、报文模板、监视点位、网卡方案
/// 组织成一个可保存/导入的工程文件。带 <c>schemaVersion</c>、原子写入、损坏文件保留备份，
/// **不保存任何凭据**（本工具的相关对象本身不含凭据）。
/// </summary>
public sealed record WorkspaceFile
{
    public int SchemaVersion { get; init; } = WorkspaceDefaults.CurrentSchemaVersion;
    public string Name { get; init; } = WorkspaceDefaults.DefaultName;
    public string? Description { get; init; }

    /// <summary>写入该文件的程序版本（便于现场排查，不做兼容判断）。</summary>
    public string? AppVersion { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public WorkspaceContents Contents { get; init; } = new();

    public int PointCount => Contents.WatchPoints.Count;
    public int PlanCount => Contents.Plans.Count;
    public int TargetCount => Contents.Targets.Count;

    /// <summary>界面显示用摘要。</summary>
    public string SummaryText =>
        $"{Name}：点位 {Contents.WatchPoints.Count}、方案 {Contents.Plans.Count}、设备 {Contents.Targets.Count}、" +
        $"报文模板 {Contents.Templates.Count}、网卡方案 {Contents.NetworkProfiles.Count}" +
        $"（共 {Contents.TotalCount} 项，格式版本 {SchemaVersion}）";
}

/// <summary>
/// 工作区文件读写（规划 15.10 的可靠性标准）：原子写入、损坏文件保留 <c>.bad-时间戳</c> 备份、
/// 高版本文件只读打开且拒绝覆盖。
/// </summary>
public sealed class WorkspaceStore
{
    public WorkspaceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    /// <summary>最近一次加载是否因为文件版本高于当前程序而按只读方式处理。</summary>
    public bool LastLoadWasReadOnly { get; private set; }

    /// <summary>根据工作区名称生成默认文件路径（名称中的非法字符会被替换）。</summary>
    public static string DefaultPath(string directory, string name)
    {
        var safe = Sanitize(name);
        return System.IO.Path.Combine(directory, safe + WorkspaceDefaults.Extension);
    }

    /// <summary>把名称规范成合法文件名。</summary>
    public static string Sanitize(string? name)
    {
        var text = string.IsNullOrWhiteSpace(name) ? WorkspaceDefaults.DefaultName : name.Trim();
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
        {
            text = text.Replace(invalid, '_');
        }

        return text.Length > 80 ? text[..80] : text;
    }

    /// <summary>
    /// 读取工作区文件。文件不存在 → 返回一个空工作区（warning = null）；
    /// 损坏 → 备份为 <c>.bad-时间戳</c> 并返回空工作区（带 warning）；
    /// 版本高于当前程序 → 只读打开（内容照常返回，带 warning，且后续 <see cref="Save"/> 会拒绝覆盖）。
    /// </summary>
    public Result<WorkspaceFile> Load(out string? warning)
    {
        warning = null;
        LastLoadWasReadOnly = false;

        if (!File.Exists(Path))
        {
            return Result<WorkspaceFile>.Ok(NewFile(FileSystemName()));
        }

        try
        {
            var json = File.ReadAllText(Path);
            var file = JsonSerializer.Deserialize<WorkspaceFile>(json, SettingsJson.Options);
            if (file is null)
            {
                warning = "工作区文件内容为空，已按空工作区打开（原文件保持不变）。";
                return Result<WorkspaceFile>.Ok(NewFile(FileSystemName()));
            }

            if (file.SchemaVersion > WorkspaceDefaults.CurrentSchemaVersion)
            {
                LastLoadWasReadOnly = true;
                warning = $"工作区文件版本（{file.SchemaVersion}）高于当前程序支持的版本" +
                          $"（{WorkspaceDefaults.CurrentSchemaVersion}），已按只读方式打开：可以查看与使用，但不会被覆盖。";
            }

            return Result<WorkspaceFile>.Ok(file);
        }
        catch (Exception ex)
        {
            var backup = $"{Path}.bad-{DateTime.Now:yyyyMMddHHmmss}";
            try
            {
                File.Copy(Path, backup, overwrite: true);
            }
            catch (Exception)
            {
                backup = "(备份失败)";
            }

            warning = $"工作区文件损坏或无法读取（{ex.Message}），已保留备份 {backup}，并按空工作区打开。";
            return Result<WorkspaceFile>.Ok(NewFile(FileSystemName()));
        }
    }

    /// <summary>原子保存（临时文件 + 替换）。返回 null 表示成功，否则为可读原因。</summary>
    public string? Save(WorkspaceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.SchemaVersion > WorkspaceDefaults.CurrentSchemaVersion)
        {
            return $"工作区文件版本（{file.SchemaVersion}）高于当前程序支持的版本" +
                   $"（{WorkspaceDefaults.CurrentSchemaVersion}），已拒绝覆盖写入（原文件保持不变）。";
        }

        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var payload = file with
            {
                SchemaVersion = WorkspaceDefaults.CurrentSchemaVersion,
                UpdatedAt = DateTimeOffset.Now,
                AppVersion = file.AppVersion
            };

            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(payload, SettingsJson.Options));
            File.Move(temporary, Path, overwrite: true);
            LastLoadWasReadOnly = false;
            return null;
        }
        catch (Exception ex)
        {
            return $"保存工作区失败：{ex.Message}";
        }
    }

    /// <summary>导出到指定路径（同样原子写入）。返回 null 表示成功。</summary>
    public string? Export(WorkspaceFile file, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        if (file.SchemaVersion > WorkspaceDefaults.CurrentSchemaVersion)
        {
            return "工作区文件版本高于当前程序支持的版本，无法导出。";
        }

        try
        {
            var full = System.IO.Path.GetFullPath(destinationPath);
            var directory = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temporary = full + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, SettingsJson.Options));
            File.Move(temporary, full, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            return $"导出工作区失败：{ex.Message}";
        }
    }

    /// <summary>从当前设置抽取工作区内容（不包含日志/监视等运行期设置）。</summary>
    public static WorkspaceFile FromSettings(string name, AppSettings settings, string? description = null, string? appVersion = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new WorkspaceFile
        {
            Name = string.IsNullOrWhiteSpace(name) ? WorkspaceDefaults.DefaultName : name.Trim(),
            Description = description,
            AppVersion = appVersion,
            Contents = new WorkspaceContents
            {
                SerialProfiles = new[] { settings.Serial },
                TcpProfiles = new[] { settings.Tcp },
                Targets = settings.Targets,
                Plans = settings.Plans,
                Templates = settings.Templates,
                NetworkProfiles = settings.NetworkProfiles,
                WatchPoints = Array.Empty<RegisterPoint>()
            }
        };
    }

    /// <summary>
    /// 把工作区内容应用到设置。**只替换工作区包含的集合**，日志/监视/扫描默认值/目录等运行期设置一律不动。
    /// 说明：监视点位（<see cref="WorkspaceContents.WatchPoints"/>）是会话作用域的（M3b 起每个会话独立），
    /// 不在 <see cref="AppSettings"/> 里，因此由界面在导入工作区后加入当前会话的监视表。
    /// </summary>
    public static AppSettings ApplyTo(WorkspaceFile file, AppSettings settings, WorkspaceMergeMode mode)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(settings);

        var contents = file.Contents;
        var serial = contents.SerialProfiles.FirstOrDefault() ?? settings.Serial;
        var tcp = contents.TcpProfiles.FirstOrDefault() ?? settings.Tcp;

        return settings with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            Serial = serial,
            Tcp = tcp,
            Targets = mode == WorkspaceMergeMode.Replace
                ? contents.Targets
                : MergeById(settings.Targets, contents.Targets, t => t.Id),
            Plans = mode == WorkspaceMergeMode.Replace
                ? contents.Plans
                : MergeById(settings.Plans, contents.Plans, p => p.Id),
            Templates = mode == WorkspaceMergeMode.Replace
                ? contents.Templates
                : MergeById(settings.Templates, contents.Templates, t => t.Id),
            NetworkProfiles = mode == WorkspaceMergeMode.Replace
                ? contents.NetworkProfiles
                : MergeById(settings.NetworkProfiles, contents.NetworkProfiles, p => p.Id)
        };
    }

    /// <summary>合并（工作区优先，同 Id 覆盖；目标独有项保留，按 Id 去重）。</summary>
    private static IReadOnlyList<T> MergeById<T>(IReadOnlyList<T> existing, IReadOnlyList<T> incoming, Func<T, string> id)
    {
        var result = new List<T>(existing);
        foreach (var item in incoming)
        {
            var key = id(item);
            result.RemoveAll(x => string.Equals(id(x), key, StringComparison.Ordinal));
            result.Add(item);
        }

        return result;
    }

    private WorkspaceFile NewFile(string name) => new()
    {
        Name = string.IsNullOrWhiteSpace(name) ? WorkspaceDefaults.DefaultName : name,
        SchemaVersion = WorkspaceDefaults.CurrentSchemaVersion
    };

    private string FileSystemName()
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(Path);
        return string.IsNullOrWhiteSpace(name) ? WorkspaceDefaults.DefaultName : name;
    }
}

/// <summary>
/// 工作区内容校验（规划 4.12）：在保存/导入前给出人类可读的问题列表（空列表 = 通过）。
/// 只做"明显错误"的检查，不阻止用户保存（现场文件允许部分不完整）。
/// </summary>
public static class WorkspaceValidator
{
    public static IReadOnlyList<string> Validate(WorkspaceFile file)
    {
        var issues = new List<string>();
        if (file is null)
        {
            issues.Add("工作区对象为空。");
            return issues;
        }

        if (string.IsNullOrWhiteSpace(file.Name)) issues.Add("工作区名称为空。");

        AddDuplicateIssues(issues, "设备目标", file.Contents.Targets.Select(t => t.Id));
        AddDuplicateIssues(issues, "扫描方案", file.Contents.Plans.Select(p => p.Id));
        AddDuplicateIssues(issues, "报文模板", file.Contents.Templates.Select(t => t.Id));
        AddDuplicateIssues(issues, "监视点位", file.Contents.WatchPoints.Select(p => p.Id));
        AddDuplicateIssues(issues, "网卡方案", file.Contents.NetworkProfiles.Select(p => p.Id));

        foreach (var target in file.Contents.Targets)
        {
            if (string.IsNullOrWhiteSpace(target.Endpoint)) issues.Add($"设备目标「{target.DisplayText}」缺少端点（COMx 或 IP:端口）。");
        }

        foreach (var profile in file.Contents.NetworkProfiles)
        {
            if (!profile.DhcpEnabled && string.IsNullOrWhiteSpace(profile.Ipv4))
            {
                issues.Add($"网卡方案「{profile.DisplayText}」是静态模式但没有填写 IPv4 地址。");
            }
        }

        foreach (var plan in file.Contents.Plans)
        {
            if (plan.Payload.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            {
                issues.Add($"扫描方案「{plan.DisplayText}」的负载不是 JSON 对象（可能已损坏）。");
            }
        }

        foreach (var point in file.Contents.WatchPoints)
        {
            if (point.Address < 0 || point.Address > 65535)
            {
                issues.Add($"监视点位「{point.Name}」地址越界：{point.Address}（应在 0~65535）。");
            }

            if (point.RegisterCount <= 0)
            {
                issues.Add($"监视点位「{point.Name}」的数据类型寄存器数量非法。");
            }
        }

        return issues;
    }

    private static void AddDuplicateIssues(List<string> issues, string kind, IEnumerable<string> ids)
    {
        var duplicates = ids.Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        foreach (var duplicate in duplicates) issues.Add($"{kind}存在重复 Id：{duplicate}。");
    }
}
