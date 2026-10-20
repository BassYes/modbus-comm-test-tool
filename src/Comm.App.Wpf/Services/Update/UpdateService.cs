using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Comm.Diagnostics;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Comm.App.Wpf.Services.Update;

/// <summary>
/// 更新服务实现（M1：检查、下载、校验；M2 再接入 helper 替换）。
/// </summary>
public sealed class UpdateService : IUpdateService
{
    public const string DefaultPublicKey = "5Vp7fPpSrbX0KEJMlnRh2YYzIclyim6UQtXYfvll3Dc=";

    /// <summary>
    /// 更新清单地址：指向本仓库最新（Latest）Release 的清单附件，
    /// 该附件由 .github/workflows/release.yml 在推送 v* 标签时生成并上传。
    /// </summary>
    public const string DefaultManifestUrl = "https://github.com/BassYes/modbus-comm-test-tool/releases/latest/download/update-manifest.signed.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly string[] AllowedHosts =
    {
        "github.com",
        "objects.githubusercontent.com",
        "githubusercontent.com",
        "raw.githubusercontent.com"
    };

    private readonly HttpClient _http;
    private readonly string _manifestUrl;
    private readonly string _publicKeyBase64;
    private readonly string _product;
    private readonly string _platform;

    public UpdateService(
        string manifestUrl,
        string publicKeyBase64,
        string currentVersion,
        long currentBuild,
        HttpClient? httpClient = null)
    {
        _manifestUrl = manifestUrl;
        _publicKeyBase64 = publicKeyBase64;
        _product = "CommTestTool";
        _platform = "windows-x64";
        CurrentVersion = currentVersion;
        CurrentBuild = currentBuild;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string CurrentVersion { get; }

    public long CurrentBuild { get; }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        Uri manifestUri;
        try
        {
            manifestUri = new Uri(_manifestUrl);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, null, $"更新清单地址无效：{ex.Message}");
        }

        if (!IsAllowedHost(manifestUri.Host))
        {
            return new UpdateCheckResult(false, null, "更新清单域名不在白名单内。");
        }

        if (manifestUri.Scheme != Uri.UriSchemeHttps)
        {
            return new UpdateCheckResult(false, null, "更新清单必须使用 HTTPS。");
        }

        string json;
        try
        {
            using var response = await _http.GetAsync(manifestUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(false, null, $"获取更新清单失败：HTTP {(int)response.StatusCode}。");
            }
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, null, $"网络错误：{ex.Message}");
        }

        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, null, $"更新清单解析失败：{ex.Message}");
        }

        if (manifest is null)
        {
            return new UpdateCheckResult(false, null, "更新清单为空。");
        }

        var validation = ValidateManifest(manifest);
        if (!validation.Success)
        {
            return new UpdateCheckResult(false, null, validation.Message);
        }

        if (!IsNewer(manifest))
        {
            return new UpdateCheckResult(false, manifest, "当前已是最新版本。");
        }

        return new UpdateCheckResult(true, manifest, $"发现新版本 {manifest.Version}（build {manifest.Build}）。");
    }

    public async Task<string> DownloadUpdateAsync(
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateManifest(manifest);
        if (!validation.Success)
        {
            throw new InvalidOperationException(validation.Message);
        }

        if (!IsNewer(manifest))
        {
            throw new InvalidOperationException("目标版本不高于当前版本。");
        }

        Uri downloadUri;
        try
        {
            downloadUri = new Uri(manifest.DownloadUrl);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"下载地址无效：{ex.Message}", ex);
        }

        if (downloadUri.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(downloadUri.Host))
        {
            throw new InvalidOperationException("下载地址不在允许的 HTTPS 域名白名单内。");
        }

        var updateDir = Path.Combine(AppPaths.DataDirectory, "updates");
        Directory.CreateDirectory(updateDir);
        var packagePath = Path.Combine(updateDir, SanitizeFileName(manifest.FileName));
        var partPath = packagePath + ".part";

        // 上次已经下好的包（比如“应用更新”失败后重试）直接复用，避免把 100 MB+ 再下一遍。
        if (await TryReuseExistingPackageAsync(packagePath, manifest, progress, cancellationToken).ConfigureAwait(false))
        {
            return packagePath;
        }

        // 先清掉上次失败留下的半成品。删不掉也不影响本次：内容先写到 .part，最后再替换正式文件。
        await TryDeleteFileAsync(partPath, cancellationToken).ConfigureAwait(false);
        await TryDeleteFileAsync(packagePath, cancellationToken).ConfigureAwait(false);

        using var response = await _http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? manifest.Size;
        if (totalBytes > 0 && totalBytes != manifest.Size)
        {
            // Allow unknown length, but if server reports length it must match manifest.
            if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != manifest.Size)
            {
                throw new InvalidOperationException("下载文件大小与清单不一致。");
            }
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var received = 0L;
        var buffer = new byte[81920];
        await using (var destination = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                if (received > manifest.Size)
                {
                    throw new InvalidOperationException("下载文件超过清单声明大小。");
                }
                progress?.Report(new UpdateDownloadProgress(received, manifest.Size));
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        if (received != manifest.Size)
        {
            await TryDeleteFileAsync(partPath, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"下载大小不匹配：期望 {manifest.Size}，实际 {received}。");
        }

        var actualHash = await ComputeSha256Async(partPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await TryDeleteFileAsync(partPath, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("下载文件 SHA-256 校验失败。");
        }

        // 校验通过才把 .part 换成正式文件名。杀毒软件可能在文件刚写完时短暂独占它，替换时重试几次。
        await ReplaceFileAsync(partPath, packagePath, cancellationToken).ConfigureAwait(false);
        return packagePath;
    }

    public Task<UpdateApplyResult> ApplyUpdateAsync(
        string packagePath,
        UpdateManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var helperPath = FindHelperExecutable();
        if (helperPath is null)
        {
            return Task.FromResult(new UpdateApplyResult(false, "未找到 Comm.UpdateHelper.exe，无法应用更新。"));
        }

        var targetExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(targetExe))
        {
            return Task.FromResult(new UpdateApplyResult(false, "无法确定当前程序路径。"));
        }

        var targetDir = Path.GetDirectoryName(targetExe)!;

        // 更新助手可能是“exe + 依赖 dll”的多文件发布：把整个 helper 目录一起搬到临时目录再启动，
        // 避免只复制 exe 导致缺 deps.json / runtimeconfig.json 而起不来。
        var helperDirectory = Path.GetDirectoryName(Path.GetFullPath(helperPath))!;
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "CommTestTool-update-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);

        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(Path.GetFullPath(helperDirectory).TrimEnd(Path.DirectorySeparatorChar), baseDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            // 兼容旧的“程序根目录放单文件助手”布局：只复制 exe。
            File.Copy(helperPath, Path.Combine(stagingDirectory, Path.GetFileName(helperPath)), overwrite: true);
        }
        else
        {
            foreach (var file in Directory.EnumerateFiles(helperDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                File.Copy(file, Path.Combine(stagingDirectory, Path.GetFileName(file)), overwrite: true);
            }
        }

        var stagingHelper = Path.Combine(stagingDirectory, Path.GetFileName(helperPath));

        var currentPid = Environment.ProcessId;
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = stagingHelper,
            Arguments = $"--worker --package \"{packagePath}\" --target-dir \"{targetDir}\" --wait-pid {currentPid} --launch \"{targetExe}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = System.Diagnostics.Process.Start(startInfo);
        if (process is null)
        {
            return Task.FromResult(new UpdateApplyResult(false, "无法启动更新助手。"));
        }

        return Task.FromResult(new UpdateApplyResult(true, "更新助手已启动，程序即将退出以完成更新。"));
    }

    private static string? FindHelperExecutable()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            // 发布包里的标准位置（Publish-App.ps1 输出到 helper\update\）
            Path.Combine(baseDir, "helper", "update", "Comm.UpdateHelper.exe"),
            Path.Combine(baseDir, "helper", "Comm.UpdateHelper.exe"),
            Path.Combine(baseDir, "Comm.UpdateHelper.exe"),
            Path.Combine(baseDir, "..", "Comm.UpdateHelper", "bin", "Debug", "net8.0-windows", "Comm.UpdateHelper.exe"),
            Path.Combine(baseDir, "..", "..", "..", "tools", "Comm.UpdateHelper", "bin", "Debug", "net8.0-windows", "Comm.UpdateHelper.exe")
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    private (bool Success, string Message) ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.Schema != 1)
        {
            return (false, $"不支持的清单 schema：{manifest.Schema}。");
        }

        if (!string.Equals(manifest.Product, _product, StringComparison.Ordinal))
        {
            return (false, "产品标识不匹配。");
        }

        if (!string.Equals(manifest.Platform, _platform, StringComparison.Ordinal))
        {
            return (false, "平台标识不匹配。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version) || !Version.TryParse(manifest.Version, out _))
        {
            return (false, "版本号无效。");
        }

        if (manifest.Build <= 0)
        {
            return (false, "构建号无效。");
        }

        var now = DateTimeOffset.UtcNow;
        if (manifest.IssuedAt > now.AddMinutes(5))
        {
            return (false, "清单签发时间无效。");
        }

        if (manifest.ExpiresAt <= now)
        {
            return (false, "更新清单已过期。");
        }

        if (manifest.ExpiresAt - manifest.IssuedAt > TimeSpan.FromDays(180))
        {
            return (false, "更新清单有效期过长。");
        }

        if (string.IsNullOrWhiteSpace(manifest.FileName) || manifest.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return (false, "文件名无效。");
        }

        Uri downloadUri;
        try
        {
            downloadUri = new Uri(manifest.DownloadUrl);
        }
        catch
        {
            return (false, "下载地址无效。");
        }

        if (downloadUri.Scheme != Uri.UriSchemeHttps || !IsAllowedHost(downloadUri.Host))
        {
            return (false, "下载地址不在允许的 HTTPS 域名白名单内。");
        }

        if (manifest.Size <= 0 || manifest.Size > 512L * 1024 * 1024)
        {
            return (false, "文件大小无效或超过限制。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Sha256) || manifest.Sha256.Length != 64)
        {
            return (false, "SHA-256 无效。");
        }

        if (string.IsNullOrWhiteSpace(manifest.SigningKeyId))
        {
            return (false, "签名密钥标识缺失。");
        }

        if (string.IsNullOrWhiteSpace(manifest.Signature))
        {
            return (false, "清单缺少签名。");
        }

        if (!VerifySignature(manifest))
        {
            return (false, "Ed25519 签名验证失败。");
        }

        return (true, "OK");
    }

    private bool VerifySignature(UpdateManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(_publicKeyBase64))
        {
            return false;
        }

        try
        {
            var sigBytes = Convert.FromBase64String(manifest.Signature!);
            var pubBytes = Convert.FromBase64String(_publicKeyBase64);
            if (pubBytes.Length != 32 || sigBytes.Length == 0)
            {
                return false;
            }

            var canonical = CanonicalManifestBytes(manifest);
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(pubBytes, 0));
            verifier.BlockUpdate(canonical, 0, canonical.Length);
            return verifier.VerifySignature(sigBytes);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断清单是否比当前程序更新：<b>只比版本号</b>。
    /// <para>
    /// 不能用 <see cref="UpdateManifest.Build"/> 比较：清单里的 build 是发布流水线“生成清单”的时刻，
    /// 而程序里的 build 是“编译”时刻；同一版本的清单 build 天生比程序 build 晚，
    /// 会把“已是最新”误判成“发现新版本 0.4.2”（点下载后又会因版本不高于当前版本而失败）。
    /// 发版约定见 Directory.Build.props / .github/workflows/release.yml：每次发布递增版本号。
    /// </para>
    /// </summary>
    private bool IsNewer(UpdateManifest manifest)
    {
        if (!Version.TryParse(CurrentVersion, out var current))
        {
            return true;
        }
        if (!Version.TryParse(manifest.Version, out var latest))
        {
            return false;
        }

        return latest > current;
    }

    private static bool IsAllowedHost(string host)
    {
        foreach (var allowed in AllowedHosts)
        {
            if (string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("文件名无效。");
        }
        return name;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        // 杀毒软件/搜索索引可能在文件刚写完时短暂独占它：共享读 + 有限重试，避免“文件被占用”直接判下载失败。
        var lastError = default(Exception);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                await Task.Delay(200 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            "更新包校验失败：文件正被其他程序占用（常见于杀毒软件扫描），请稍后重试。", lastError);
    }

    /// <summary>已有完整且 SHA 匹配的包时直接复用（避免重复下载 100 MB+）。</summary>
    private static async Task<bool> TryReuseExistingPackageAsync(
        string packagePath,
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(packagePath);
            if (!file.Exists || file.Length != manifest.Size)
            {
                return false;
            }

            var hash = await ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            progress?.Report(new UpdateDownloadProgress(manifest.Size, manifest.Size));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>删除文件；删不掉（被占用 / 无权限）时只重试几次，不抛异常。</summary>
    private static async Task TryDeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>把下载好的 .part 替换成正式包名；被杀毒软件短暂占用时重试。</summary>
    private static async Task ReplaceFileAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        var lastError = default(Exception);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Move(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                await Task.Delay(200 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            "更新包保存失败：目标文件正被其他程序占用（常见于杀毒软件扫描），请稍后重试。", lastError);
    }

    private static byte[] CanonicalManifestBytes(UpdateManifest manifest)
    {
        var clone = manifest with { Signature = null };
        var json = JsonSerializer.Serialize(clone, CanonicalOptions);
        const string prefix = "CommTestTool update manifest Ed25519 v1\0";
        return Encoding.UTF8.GetBytes(prefix + json);
    }
}