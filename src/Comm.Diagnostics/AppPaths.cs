namespace Comm.Diagnostics;

/// <summary>
/// 目录解析（规划 12.7、22.6.2 的 X-2 / D-13；per-user 安装见 docs/per-user-install-plan.md）。
/// <para>
/// 原则：**显式声明 &gt; 安装形态 &gt; 可写性回退**。
/// 1. <c>portable.flag</c> 存在且可写 → 程序同级 <c>data/</c>（显式便携，最高优先）；
/// 2. 安装形态（<c>install.flag</c> / HKCU 注册表标记 / Inno <c>unins000.exe</c>）→ <c>%LOCALAPPDATA%\通讯测试工具\data</c>，
///    与历史版本路径一致，升级不迁移；
/// 3. 非安装形态且程序目录可写 → 程序同级 <c>data/</c>（解压即用的便携版）；
/// 4. 程序目录不可写 → 用户目录；5. 再不行 → 系统临时目录，并记录原因。
/// </para>
/// 任何情况下都不允许因为目录不可写而崩溃或静默丢设置。
/// </summary>
public static class AppPaths
{
    public const string PortableFlagName = "portable.flag";

    /// <summary>安装标记文件（由安装包写入，见 docs/per-user-install-plan.md）。</summary>
    public const string InstallFlagName = "install.flag";

    public const string ProductFolderName = "通讯测试工具";

    /// <summary>程序所在目录（发布目录）。</summary>
    public static string BaseDirectory => AppContext.BaseDirectory;

    /// <summary>程序同级 data 目录（便携形态的目标目录）。</summary>
    public static string PortableDataDirectory => Path.Combine(BaseDirectory, "data");

    /// <summary>用户根目录（安装形态的目标：%LOCALAPPDATA%\通讯测试工具）。</summary>
    public static string UserRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName);

    /// <summary>用户数据目录（安装形态的目标目录）。</summary>
    public static string UserDataDirectory => Path.Combine(UserRootDirectory, "data");

    /// <summary>安装标记文件是否存在。</summary>
    public static bool HasInstallFlag => SafeExists(Path.Combine(BaseDirectory, InstallFlagName));

    /// <summary>
    /// 是否安装形态（D1：安装标记优先于“程序目录可写性”，避免 per-user 安装后被误判为便携）。
    /// 三重判定：install.flag → HKCU 注册表标记（比对 InstallDir）→ Inno 卸载程序 unins000.exe。
    /// </summary>
    public static bool IsInstalledForm => HasInstallFlag || HasInstallRegistryMark() || HasInnoUninstaller();

    /// <summary>实际使用的数据目录。</summary>
    public static string DataDirectory => Info.Directory;

    /// <summary>日志目录：安装形态固定在用户目录（卸载不会误删），便携形态在程序同级。</summary>
    public static string LogsDirectory => Info.Form == DataForm.Installed
        ? Path.Combine(UserRootDirectory, "logs")
        : Path.Combine(BaseDirectory, "logs");

    /// <summary>导出目录：安装形态固定在用户目录，便携形态在程序同级。</summary>
    public static string ExportsDirectory => Info.Form == DataForm.Installed
        ? Path.Combine(UserRootDirectory, "exports")
        : Path.Combine(BaseDirectory, "exports");

    public static string CapturesDirectory => Path.Combine(DataDirectory, "captures");

    /// <summary>数据目录解析结果（进程内解析一次）。</summary>
    public static DataDirectoryInfo Info { get; } = Resolve();

    /// <summary>是否处于便携形态（数据在程序目录内）。</summary>
    public static bool IsPortable => Info.IsPortable;

    /// <summary>程序目录是否存在便携标记文件。</summary>
    public static bool HasPortableFlag => SafeExists(Path.Combine(BaseDirectory, PortableFlagName));

    public static string ToDisplayText() =>
        $"{DataDirectory}（{Info.Form switch
        {
            DataForm.Portable => "便携：程序同级 data",
            DataForm.Installed => "安装：用户目录 data",
            _ => "临时目录 data"
        }}）";

    private static DataDirectoryInfo Resolve()
    {
        // ① portable.flag 是显式声明，最高优先（即便装在安装目录里，也允许强制便携）
        if (HasPortableFlag && TryEnsureWritable(PortableDataDirectory))
        {
            return new DataDirectoryInfo(PortableDataDirectory, DataForm.Portable, null);
        }

        // ② 安装形态：固定在用户目录。与历史版本（装到 Program Files）的数据路径一致，升级零迁移。
        if (IsInstalledForm && TryEnsureWritable(UserDataDirectory))
        {
            return new DataDirectoryInfo(UserDataDirectory, DataForm.Installed, null);
        }

        // ③ 便携兜底（解压即用）
        if (TryEnsureWritable(PortableDataDirectory))
        {
            return new DataDirectoryInfo(PortableDataDirectory, DataForm.Portable,
                FallbackReason: IsInstalledForm
                    ? $"安装形态但用户目录不可写（{UserDataDirectory}），已回退到程序目录；请检查 %LOCALAPPDATA% 权限。"
                    : null);
        }

        // ④ 用户目录兜底
        if (TryEnsureWritable(UserDataDirectory))
        {
            return new DataDirectoryInfo(UserDataDirectory, DataForm.Installed,
                FallbackReason: $"程序目录不可写（{PortableDataDirectory}），数据已回退到用户目录。");
        }

        // ⑤ 临时目录
        return new DataDirectoryInfo(Path.Combine(Path.GetTempPath(), ProductFolderName, "data"), DataForm.Temporary,
            FallbackReason: "程序目录与用户目录均不可写，数据已回退到系统临时目录；设置可能不会长期保留。");
    }

    /// <summary>注册表安装标记（双保险：install.flag 被误删时仍能识别）。*/
    private static bool HasInstallRegistryMark()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\通讯测试工具");
            if (key?.GetValue("InstallDir") is not string installDir || string.IsNullOrWhiteSpace(installDir))
            {
                return false;
            }

            // 必须指向当前程序目录，避免把安装目录旁边的便携副本误判为安装形态
            return string.Equals(
                Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Inno Setup 卸载程序存在 → 判定为安装形态（第三重兜底）。</summary>
    private static bool HasInnoUninstaller() => SafeExists(Path.Combine(BaseDirectory, "unins000.exe"));

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

/// <summary>数据目录形态（决定 data/logs/exports 的落点）。</summary>
public enum DataForm
{
    /// <summary>便携：数据在程序同级目录。</summary>
    Portable,

    /// <summary>安装：数据在用户目录（%LOCALAPPDATA%\通讯测试工具）。</summary>
    Installed,

    /// <summary>临时：两个位置都不可写时的兜底。</summary>
    Temporary
}

/// <summary>数据目录解析结果（供设置页显示实际目录与回退原因）。</summary>
public sealed record DataDirectoryInfo(string Directory, DataForm Form, string? FallbackReason)
{
    /// <summary>是否便携形态（数据在程序目录内）。</summary>
    public bool IsPortable => Form == DataForm.Portable;
}

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
        sb.AppendLine($"安装形态：{(AppPaths.IsInstalledForm ? "安装版（per-user）" : "便携版")}；" +
                      $"install.flag：{(AppPaths.HasInstallFlag ? "存在" : "不存在")}；" +
                      $"portable.flag：{(AppPaths.HasPortableFlag ? "存在" : "不存在")}");
        sb.AppendLine($"日志目录：{AppPaths.LogsDirectory}");
        sb.AppendLine($"导出目录：{AppPaths.ExportsDirectory}");
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
