using Comm.Core.Errors;
using Comm.Core.Transport;
using Comm.Scanning;
using Comm.Session;
using Xunit;

namespace Comm.Scanning.Tests;

/// <summary>
/// 参数扫描的"恢复原串口参数"结果必须能贯穿取消/异常终态（审计 F-06）。
/// 这里用一个只在内存里记账的假重配器，不碰真实串口。
/// </summary>
public sealed class ParameterScanRestoreTests
{
    [Fact]
    public async Task Cancelled_scan_exposes_restore_failure_through_LastRestore()
    {
        var reconfigurator = new FakeReconfigurator(restoreSucceeds: false);
        var service = new ParameterScanService(() => null, reconfigurator);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ScanAsync(SmallOptions(), null, null, null, cts.Token));

        Assert.True(reconfigurator.RestoreCallCount > 0, "取消路径也必须执行恢复");
        Assert.NotNull(service.LastRestore);
        Assert.False(service.LastRestore!.Success);
        Assert.Contains("模拟恢复失败", service.LastRestore.Error);
    }

    [Fact]
    public async Task Completed_scan_exposes_restore_success()
    {
        var reconfigurator = new FakeReconfigurator(restoreSucceeds: true);
        var service = new ParameterScanService(() => null, reconfigurator);

        // 只有 1 个组合 + 1 个地址：没有会话时探测全部离线，但流程会正常走完并恢复参数
        var result = await service.ScanAsync(SmallOptions(), null, null, null, CancellationToken.None);

        Assert.True(result.Restored);
        Assert.Null(result.RestoreError);
        Assert.NotNull(service.LastRestore);
        Assert.True(service.LastRestore!.Success);
    }

    [Fact]
    public async Task LastRestore_is_reset_at_the_start_of_a_new_scan()
    {
        var reconfigurator = new FakeReconfigurator(restoreSucceeds: true);
        var service = new ParameterScanService(() => null, reconfigurator);

        await service.ScanAsync(SmallOptions(), null, null, null, CancellationToken.None);
        Assert.NotNull(service.LastRestore);

        // 第二次扫描在"恢复步骤之前"就因校验失败抛出：不能沿用上一轮的成功结果
        var invalid = SmallOptions() with { EndAddress = 0, StartAddress = 1 };
        await Assert.ThrowsAsync<CommException>(
            () => service.ScanAsync(invalid, null, null, null, CancellationToken.None));

        Assert.Null(service.LastRestore);
    }

    private static ParameterScanOptions SmallOptions() => new()
    {
        StartAddress = 1,
        EndAddress = 1,
        ResponseTimeoutMs = 50,
        IntervalMs = 0,
        BaudRateSource = SerialParameterSource.Unknown,
        CandidateBaudRates = new[] { 9600 },
        ParitySource = SerialParameterSource.UseConnection,
        StopBits = StopBitsStrategy.Auto,
        TryReadDeviceIdentification = false,
        ContinueAfterHit = false
    };

    private sealed class FakeReconfigurator : ISerialReconfigurator
    {
        private readonly bool _restoreSucceeds;

        public FakeReconfigurator(bool restoreSucceeds) => _restoreSucceeds = restoreSucceeds;

        public int RestoreCallCount { get; private set; }

        public bool CanReconfigure => true;

        public SerialSettings? OriginalSettings { get; } = new() { PortName = "COM_TEST", BaudRate = 9600 };

        public SerialSettings? ActiveSettings => OriginalSettings;

        public Task<ModbusSession?> ApplyAsync(SerialSettings settings, int settleMs, CancellationToken cancellationToken) =>
            Task.FromResult<ModbusSession?>(null);

        public Task<SerialRestoreResult> RestoreAsync(CancellationToken cancellationToken)
        {
            RestoreCallCount++;
            return Task.FromResult(_restoreSucceeds
                ? SerialRestoreResult.Ok
                : new SerialRestoreResult(false, "模拟恢复失败：端口被占用"));
        }
    }
}
