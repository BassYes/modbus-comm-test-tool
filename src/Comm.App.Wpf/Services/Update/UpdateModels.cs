namespace Comm.App.Wpf.Services.Update;

/// <summary>客户端更新清单（与 docs/samples/update-manifest.example.json 对应）。</summary>
public sealed record UpdateManifest(
    int Schema,
    string Product,
    string Platform,
    string Version,
    long Build,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string FileName,
    string DownloadUrl,
    long Size,
    string Sha256,
    string? ExecutableSha256,
    string? Notes,
    string SigningKeyId,
    string? Signature);

/// <summary>更新检查结果。</summary>
public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    UpdateManifest? Manifest,
    string Message);

/// <summary>更新下载进度。</summary>
public sealed record UpdateDownloadProgress(
    long ReceivedBytes,
    long TotalBytes);

/// <summary>更新应用结果。</summary>
public sealed record UpdateApplyResult(
    bool Success,
    string Message);
