namespace Comm.App.Wpf.Services.Update;

/// <summary>
/// 软件更新服务契约（评估阶段：仅定义接口，不注册实现）。
/// </summary>
public interface IUpdateService
{
    /// <summary>当前产品版本。</summary>
    string CurrentVersion { get; }

    /// <summary>当前内部构建号。</summary>
    long CurrentBuild { get; }

    /// <summary>检查更新。</summary>
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>下载更新包。</summary>
    Task<string> DownloadUpdateAsync(
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>校验并应用更新（通常需要重启）。</summary>
    Task<UpdateApplyResult> ApplyUpdateAsync(
        string packagePath,
        UpdateManifest manifest,
        CancellationToken cancellationToken = default);
}
