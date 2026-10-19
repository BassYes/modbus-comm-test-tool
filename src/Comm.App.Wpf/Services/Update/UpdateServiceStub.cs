namespace Comm.App.Wpf.Services.Update;

/// <summary>
/// 更新服务占位实现：仅用于评估与编译验证，不执行真实更新。
/// </summary>
public sealed class UpdateServiceStub : IUpdateService
{
    public string CurrentVersion => "0.4.2";

    public long CurrentBuild => 402;

    public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new UpdateCheckResult(false, null, "Update service is not implemented yet."));

    public Task<string> DownloadUpdateAsync(
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Update download is not implemented yet.");

    public Task<UpdateApplyResult> ApplyUpdateAsync(
        string packagePath,
        UpdateManifest manifest,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Update apply is not implemented yet.");
}
