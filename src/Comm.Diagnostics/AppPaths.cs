namespace Comm.Diagnostics;

/// <summary>
/// 目录解析（规划 12.7、22.6.2 的 X-2 / D-13）。
/// <para>
/// 原则：**便携优先，不可写时回退**。
/// 1. 程序目录可写 → 程序同级 <c>data/</c>（保留原有便携行为；<c>portable.flag</c> 可作为显式声明）；
/// 2. 程序目录不可写 → <c>%LOCALAPPDATA%\通讯测试工具\data</c>（装到 Program Files 或只读 U 盘的兜底）；
/// 3. 再不行 → 系统临时目录，并记录原因。
/// 任何情况下都不允许因为目录不可写而崩溃或静默丢设置。
/// </para>
/// </summary>
public static class AppPaths
{
    public const string PortableFlagName = "portable.flag";
    public const string ProductFolderName = "通讯测试工具";

    /// <summary>程序所在目录（发布目录）。</summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>程序同级 data 目录（便携形态的目标目录）。</summary>
    public static string PortableDataDirectory => Path.Combine(BaseDirectory, "data");

    /// <summary>用户数据目录（安装形态的目标目录）。</summary>
    public static string UserDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName, "data");

    /// <summary>实际使用的数据目录。</summary>
    public static string DataDirectory => Info.Directory;

    /// <summary>实际使用的日志目录（用户可在设置中覆盖）。保持规划 4.10 的“程序同级 logs”语义，不可写时由 LogGate 回退。</summary>
    public static string LogsDirectory => Path.Combine(BaseDirectory, "logs");

    public static string ExportsDirectory => Path.Combine(BaseDirectory, "exports");

    public static string CapturesDirectory => Path.Combine(DataDirectory, "captures");

    /// <summary>数据目录解析结果（进程内解析一次）。</summary>
    public static DataDirectoryInfo Info { get; } = Resolve();

    /// <summary>是否处于便携形态（数据在程序目录内）。</summary>
    public static bool IsPortable => Info.IsPortable;

    /// <summary>程序目录是否存在便携标记文件。</summary>
    public static bool HasPortableFlag => SafeExists(Path.Combine(BaseDirectory, PortableFlagName));

    public static string ToDisplayText() =>
        $"{DataDirectory}（{(IsPortable ? "便携：程序同级 data" : "回退：程序目录不可写")}）";

    private static DataDirectoryInfo Resolve()
    {
        var portableWritable = TryEnsureWritable(PortableDataDirectory);
        if (portableWritable)
        {
            return new DataDirectoryInfo(PortableDataDirectory, IsPortable: true, FallbackReason: null);
        }

        if (TryEnsureWritable(UserDataDirectory))
        {
            return new DataDirectoryInfo(UserDataDirectory, IsPortable: false,
                FallbackReason: $"程序目录不可写（{PortableDataDirectory}），数据已回退到用户目录；" +
                                "如需便携使用，请把程序解压到可写目录（可放置 portable.flag 明确声明）。");
        }

        return new DataDirectoryInfo(Path.Combine(Path.GetTempPath(), ProductFolderName, "data"), IsPortable: false,
            FallbackReason: "程序目录与用户目录均不可写，数据已回退到系统临时目录；设置可能不会长期保留。");
    }

    /// <summary>
    /// 便携版 ↔ 安装版的数据迁移：把 <paramref name="sourceDirectory"/> 中的 settings.json
    /// 复制到 <paramref name="targetDirectory"/>。目标已存在时默认不覆盖（避免冲掉用户当前配置）。
    /// </summary>
    public static string MigrateSettings(string sourceDirectory, string targetDirectory, bool overwrite = false)
    {
        try
        {
            var source = Path.Combine(sourceDirectory, "settings.json");
            if (!File.Exists(source)) return $"源目录没有 settings.json：{sourceDirectory}";

            Directory.CreateDirectory(targetDirectory);
            var target = Path.Combine(targetDirectory, "settings.json");
            if (File.Exists(target) && !overwrite)
                return $"目标目录已存在 settings.json，未覆盖：{target}";

            File.Copy(source, target, overwrite: true);
            return string.Empty;
        }
        catch (Exception ex)
        {
            return $"迁移设置失败：{ex.Message}";
        }
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>目录可写探测：创建目录 + 写入并删除一个探针文件。</summary>
    public static bool TryEnsureWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>数据目录解析结果（供设置页显示实际目录与回退原因）。</summary>
public sealed record DataDirectoryInfo(string Directory, bool IsPortable, string? FallbackReason);

/// <summary>
/// 运行环境自检（规划 22.6.2 X-8）：系统、运行时、区域、数据目录、串口、网卡、签名与 MOTW。
/// <para>只在本机生成与显示，不上传（规划 9.5）。显示相关的 DPI / 屏幕信息由界面层补充。</para>
/// </summary>
public static class EnvironmentReport
{
    public static string Build(
        IReadOnlyList<string>? serialPorts = null,
        IReadOnlyList<string>? adapters = null,
        string? displayInfo = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("== 运行环境自检（仅本机生成，不上传） ==");
        sb.AppendLine($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"程序：{AppInfo.Summary()}");
        sb.AppendLine($"程序目录：{AppPaths.BaseDirectory}");
        sb.AppendLine($"数据目录：{AppPaths.ToDisplayText()}");
        if (!string.IsNullOrEmpty(AppPaths.Info.FallbackReason)) sb.AppendLine($"目录提示：{AppPaths.Info.FallbackReason}");
        sb.AppendLine($"exports 目录可写：{AppPaths.TryEnsureWritable(AppPaths.ExportsDirectory)}");
        sb.AppendLine();

        sb.AppendLine("-- 系统 --");
        sb.AppendLine($"操作系统：{Environment.OSVersion.VersionString}");
        sb.AppendLine($"系统架构：{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}；" +
                      $"进程：{(Environment.Is64BitProcess ? "x64" : "x86")}");
        sb.AppendLine($"处理器核数：{Environment.ProcessorCount}");
        sb.AppendLine($"当前区域：{System.Globalization.CultureInfo.CurrentCulture.Name}" +
                      $"（UI：{System.Globalization.CultureInfo.CurrentUICulture.Name}；" +
                      $"小数分隔符「{System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator}」）");
        sb.AppendLine($"临时目录：{Path.GetTempPath()}");
        sb.AppendLine();

        sb.AppendLine("-- 运行时 --");
        sb.AppendLine($"CLR：{Environment.Version}");
        sb.AppendLine($"发布形态：{(IsSelfContained() ? "自包含（目标机无需预装 .NET）" : "框架依赖（需要 .NET 8 Desktop Runtime）")}");
        sb.AppendLine();

        sb.AppendLine("-- 显示 --");
        sb.AppendLine(string.IsNullOrEmpty(displayInfo) ? "（未采集；请在界面中使用“环境检查”按钮）" : displayInfo);
        sb.AppendLine();

        sb.AppendLine("-- 串口 --");
        if (serialPorts is null)
        {
            sb.AppendLine("（未采集）");
        }
        else
        {
            sb.AppendLine($"检测到 {serialPorts.Count} 个串口：{(serialPorts.Count == 0 ? "无" : string.Join(", ", serialPorts))}");
            foreach (var port in serialPorts) sb.AppendLine($"  {port}：{DescribeSerialPort(port)}");
        }
        sb.AppendLine();

        sb.AppendLine("-- 网络 --");
        if (adapters is null) sb.AppendLine("（未采集）");
        else if (adapters.Count == 0) sb.AppendLine("未检测到网络接口");
        else foreach (var adapter in adapters) sb.AppendLine($"  {adapter}");
        sb.AppendLine();

        sb.AppendLine("-- 发布与信任 --");
        var exe = SelfExecutablePath();
        sb.AppendLine($"可执行文件：{exe}");
        sb.AppendLine($"数字签名：{DescribeSignature(exe)}");
        sb.AppendLine($"来自网络标记（MOTW）：{DescribeMotw(exe)}");
        sb.AppendLine($"便携标记 {AppPaths.PortableFlagName}：{(AppPaths.HasPortableFlag ? "存在" : "不存在")}");

        return sb.ToString();
    }

    /// <summary>串口常见问题提示（规划 22.6.2 X-6）。</summary>
    public static string DescribeSerialPort(string portName)
    {
        int index = -1;
        for (int i = 0; i < portName.Length; i++)
        {
            if (!char.IsDigit(portName[i])) continue;
            _ = int.TryParse(portName[i..], out index);
            break;
        }

        var hints = new List<string>();
        if (index >= 10) hints.Add("COM10 及以上；若某些工具只认 COM1~COM9，可在设备管理器把端口号改小");
        hints.Add("无响应时请确认 USB 转串口驱动已装（CH340 / CP210x / FTDI）且未被其他程序占用");
        return string.Join("；", hints);
    }

    /// <summary>自包含发布的判定：程序目录存在运行时本体。</summary>
    public static bool IsSelfContained() =>
        File.Exists(Path.Combine(AppPaths.BaseDirectory, "System.Private.CoreLib.dll"));

    public static string SelfExecutablePath()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) return path;
        }
        catch (Exception)
        {
        }
        return Path.Combine(AppPaths.BaseDirectory, "CommTestTool.exe");
    }

    /// <summary>用 X509Certificate 判断 Authenticode 签名状态（未签名会抛 CryptographicException）。</summary>
    public static string DescribeSignature(string path)
    {
        try
        {
            if (!File.Exists(path)) return "文件不存在";
            using var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
            return $"已签名，发布者：{cert.Subject}（颁发者 {cert.Issuer}，有效期至 {cert.GetExpirationDateString()}）";
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return "未签名（NotSigned）——“无法验证发布者”警告的直接原因，见 docs/code-signing-plan.md";
        }
        catch (Exception ex)
        {
            return $"检测失败：{ex.Message}";
        }
    }

    /// <summary>NTFS 备用数据流 Zone.Identifier 的存在性判断（.NET 在 Windows 上支持 ADS 路径）。</summary>
    public static string DescribeMotw(string path)
    {
        try
        {
            if (!File.Exists(path)) return "文件不存在";
            var stream = path + ":Zone.Identifier";
            return File.Exists(stream)
                ? "存在（文件来自 Internet 区域，未签名时会触发安全警告；签名后提示变为“已验证的发布者”）"
                : "不存在（同一台机器上直接双击不会触发安全警告）";
        }
        catch (Exception ex)
        {
            return $"检测失败：{ex.Message}";
        }
    }
}
