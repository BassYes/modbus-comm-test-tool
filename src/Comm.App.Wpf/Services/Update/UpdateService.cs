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

        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }

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
        await using var destination = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
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

        if (received != manifest.Size)
        {
            throw new InvalidOperationException($"下载大小不匹配：期望 {manifest.Size}，实际 {received}。");
        }

        var actualHash = await ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(packagePath); } catch { /* ignore */ }
            throw new InvalidOperationException("下载文件 SHA-256 校验失败。");
        }

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
        var stagingHelper = Path.Combine(Path.GetTempPath(), "CommTestTool-update-helper-" + Guid.NewGuid().ToString("N"), "Comm.UpdateHelper.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(stagingHelper)!);
        File.Copy(helperPath, stagingHelper, overwrite: true);

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
            Path.Combine(baseDir, "Comm.UpdateHelper.exe"),
            Path.Combine(baseDir, "helper", "Comm.UpdateHelper.exe"),
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

        var cmp = latest.CompareTo(current);
        if (cmp > 0) return true;
        if (cmp < 0) return false;
        return manifest.Build > CurrentBuild;
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
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] CanonicalManifestBytes(UpdateManifest manifest)
    {
        var clone = manifest with { Signature = null };
        var json = JsonSerializer.Serialize(clone, CanonicalOptions);
        const string prefix = "CommTestTool update manifest Ed25519 v1\0";
        return Encoding.UTF8.GetBytes(prefix + json);
    }
}