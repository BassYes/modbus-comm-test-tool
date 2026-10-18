using System.Text.Json;
using Comm.Core.Errors;
using Comm.NetConfigHelper.Tests.Fakes;
using Comm.Platform.Windows;
using Xunit;

namespace Comm.NetConfigHelper.Tests;

/// <summary>
/// 助手服务端集成测试：进程内真实命名管道（<see cref="HelperProtocol.CreatePipeName"/>）+ 内存假后端。
/// <para>
/// 不弹 UAC、不提权、不触碰本机网卡（规划 16.3.7 / SR-4）。
/// </para>
/// </summary>
public sealed class HelperServerTests
{
    [Fact]
    public void Snapshot_ReturnsFakeBackendAdapters()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out var sample);
        backend.Adapters.Add(FakeNetworkAdapterBackend.SampleSnapshot("adapter-2", "第二块网卡", "10.0.0.5"));

        using var harness = new Harness(backend);
        var response = HelperPipeClient.Send(harness.PipeName,
            new HelperRequest { Command = HelperCommands.Snapshot });

        Assert.True(response.IsSuccess, response.ToUserText());
        var adapters = JsonSerializer.Deserialize<List<AdapterSnapshot>>(response.Payload!, HelperJson.Options)!;
        Assert.Equal(2, adapters.Count);
        Assert.Equal(sample.Id, adapters[0].Id);
        Assert.Equal("第二块网卡", adapters[1].Name);
        Assert.False(response.RollbackArmed);
        Assert.Equal(0, backend.WriteCallCount);
    }

    [Fact]
    public void Apply_PassesRequestFieldsToBackend()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out var sample);
        backend.ApplyResult = NetConfigResult.Ok(sample, "已应用");

        var request = new NetworkConfigRequest
        {
            DhcpEnabled = false,
            Address = "192.168.1.20",
            PrefixLength = 25,
            ReplaceExistingAddresses = false,
            Gateway = "192.168.1.1",
            ApplyDns = true,
            Dns1 = "114.114.114.114",
            Dns2 = "223.5.5.5",
            AutoRollbackSeconds = 0
        };

        using var harness = new Harness(backend);
        var response = HelperPipeClient.Send(harness.PipeName, new HelperRequest
        {
            Command = HelperCommands.Apply,
            AdapterId = sample.Id,
            Payload = JsonSerializer.Serialize(request, HelperJson.Options),
            Confirmed = true
        });

        Assert.True(response.IsSuccess, response.ToUserText());
        var call = Assert.Single(backend.ApplyCalls);
        Assert.Equal(sample.Id, call.AdapterId);
        Assert.Equal(request, call.Request);  // record 值相等：字段必须原样交给后端
        Assert.False(response.RollbackArmed);
    }

    [Fact]
    public void Apply_WithAutoRollback_ArmsCountdown_AndConfirmDisarms()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out var sample);

        using var harness = new Harness(backend);
        var response = HelperPipeClient.Send(harness.PipeName, new HelperRequest
        {
            Command = HelperCommands.Apply,
            AdapterId = sample.Id,
            Payload = JsonSerializer.Serialize(
                new NetworkConfigRequest { Address = "192.168.1.30", PrefixLength = 24, AutoRollbackSeconds = 60 },
                HelperJson.Options)
        });

        Assert.True(response.IsSuccess, response.ToUserText());
        Assert.True(response.RollbackArmed);
        Assert.True(response.RollbackSecondsRemaining > 55);
        Assert.True(harness.Server.Countdown.IsArmed);

        var confirm = HelperPipeClient.Send(harness.PipeName, new HelperRequest { Command = HelperCommands.Confirm });
        Assert.True(confirm.IsSuccess, confirm.ToUserText());
        Assert.False(confirm.RollbackArmed);
        Assert.False(harness.Server.Countdown.IsArmed);
        Assert.Equal(0, harness.Server.Countdown.RemainingSeconds);

        var status = HelperPipeClient.Send(harness.PipeName, new HelperRequest { Command = HelperCommands.Status });
        Assert.True(status.IsSuccess);
        Assert.False(status.RollbackArmed);
        Assert.Contains("无未确认", status.Message!);
    }

    [Fact]
    public void Apply_WithAutoRollback_WhenCountdownExpires_RestoresPreviousSnapshot()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out var sample);
        var clock = new ManualClock();

        using var harness = new Harness(backend, new HelperServerOptions
        {
            PipeName = HelperProtocol.CreatePipeName(),
            Clock = clock
        });

        var response = HelperPipeClient.Send(harness.PipeName, new HelperRequest
        {
            Command = HelperCommands.Apply,
            AdapterId = sample.Id,
            Payload = JsonSerializer.Serialize(
                new NetworkConfigRequest { Address = "192.168.1.30", PrefixLength = 24, AutoRollbackSeconds = 30 },
                HelperJson.Options)
        });
        Assert.True(response.IsSuccess, response.ToUserText());
        Assert.True(response.RollbackArmed);

        // 助手在改动前读取了"修改前快照"，到期用它对后端回滚（规划 12.5、15.8）
        Assert.Equal(1, backend.ReadAdapterCount);

        clock.AdvanceSeconds(30);
        Assert.True(harness.Server.Countdown.Tick());

        var restore = Assert.Single(backend.RestoreCalls);
        Assert.Equal(sample.Id, restore.AdapterId);
        Assert.Equal(sample, restore.Snapshot);
        Assert.Null(harness.Server.Countdown.LastError);
        Assert.True(harness.Server.Countdown.RollbackPerformed);
    }

    [Fact]
    public void AddAuxiliary_CallsBackendAddAuxiliaryAddress()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out var sample);

        using var harness = new Harness(backend);
        var response = HelperPipeClient.Send(harness.PipeName, new HelperRequest
        {
            Command = HelperCommands.AddAuxiliary,
            AdapterId = sample.Id,
            Payload = "{\"address\":\"10.20.30.40\",\"prefixLength\":24}"
        });

        Assert.True(response.IsSuccess, response.ToUserText());
        var call = Assert.Single(backend.AddAuxCalls);
        Assert.Equal(sample.Id, call.AdapterId);
        Assert.Equal("10.20.30.40", call.Address);
        Assert.Equal(24, call.PrefixLength);
        Assert.Empty(backend.RemoveAuxCalls);
    }

    [Fact]
    public void RemoveAuxiliary_CallsBackendRemoveAuxiliaryAddress()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out var sample);

        using var harness = new Harness(backend);
        var response = HelperPipeClient.Send(harness.PipeName, new HelperRequest
        {
            Command = HelperCommands.RemoveAuxiliary,
            AdapterId = sample.Id,
            Payload = "{\"address\":\"10.20.30.40\",\"prefixLength\":24}"
        });

        Assert.True(response.IsSuccess, response.ToUserText());
        var call = Assert.Single(backend.RemoveAuxCalls);
        Assert.Equal(sample.Id, call.AdapterId);
        Assert.Equal("10.20.30.40", call.Address);
        Assert.Empty(backend.AddAuxCalls);
    }

    [Theory]
    [InlineData("cmd.exe /c calc")]
    [InlineData("apply2")]
    [InlineData("")]
    public void UnknownCommand_IsRejected_AndBackendUntouched(string command)
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out var sample);

        using var harness = new Harness(backend);
        var response = HelperPipeClient.Send(harness.PipeName, new HelperRequest
        {
            Command = command,
            AdapterId = sample.Id,
            Payload = "{\"address\":\"10.20.30.40\",\"prefixLength\":24}"
        });

        Assert.False(response.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformHelperProtocol, response.ErrorCode);
        Assert.Contains(command, harness.Server.RejectedCommands);
        Assert.Equal(0, backend.TotalCallCount);  // 绝不执行任何写操作（更不执行 shell / 子进程）
    }

    [Fact]
    public void MalformedJson_ReturnsPlt5008()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out _);

        using var harness = new Harness(backend);
        var response = HelperPipeClient.SendRaw(harness.PipeName, "{ this is not json");

        Assert.False(response.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformHelperProtocol, response.ErrorCode);
        Assert.Equal(0, backend.TotalCallCount);
        Assert.Empty(harness.Server.RejectedCommands);
    }

    [Fact]
    public void BackendThrows_ReturnsFailure_AndServerKeepsServing()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out var sample);
        backend.ApplyException = new InvalidOperationException("IP Helper 拒绝访问（模拟）");

        using var harness = new Harness(backend);
        var failed = HelperPipeClient.Send(harness.PipeName, new HelperRequest
        {
            Command = HelperCommands.Apply,
            AdapterId = sample.Id,
            Payload = JsonSerializer.Serialize(
                new NetworkConfigRequest { Address = "192.168.1.31", PrefixLength = 24 },
                HelperJson.Options)
        });

        Assert.False(failed.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformNetConfigApplyFailed, failed.ErrorCode);
        Assert.NotNull(failed.Message);
        Assert.DoesNotContain("   at ", failed.Message!);        // 堆栈只写日志，不外传
        Assert.DoesNotContain("StackTrace", failed.Message!);

        // 服务端没有崩：后续请求照常处理
        var status = HelperPipeClient.Send(harness.PipeName, new HelperRequest { Command = HelperCommands.Status });
        Assert.True(status.IsSuccess, status.ToUserText());
        Assert.Equal(2, harness.Server.RequestCount);
    }

    [Fact]
    public async Task SingleRequestMode_ExitsAfterOneRequest()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out _);

        using var harness = new Harness(backend, new HelperServerOptions
        {
            PipeName = HelperProtocol.CreatePipeName(),
            SingleRequest = true
        }, start: false);

        var run = harness.RunTask;
        var response = HelperPipeClient.Send(harness.PipeName, new HelperRequest { Command = HelperCommands.Status });
        Assert.True(response.IsSuccess, response.ToUserText());

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, harness.Server.RequestCount);
    }

    [Fact]
    public async Task IdleExit_WhenNotArmed_StopsServer()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out _);

        using var harness = new Harness(backend, new HelperServerOptions
        {
            PipeName = HelperProtocol.CreatePipeName(),
            IdleExitTimeoutOverride = TimeSpan.FromMilliseconds(300)
        }, start: false);

        await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, harness.Server.RequestCount);  // 一条请求都没收到就因空闲退出
    }

    [Fact]
    public async Task IdleExit_WhileArmed_KeepsRunning()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out var sample);

        using var harness = new Harness(backend, new HelperServerOptions
        {
            PipeName = HelperProtocol.CreatePipeName(),
            IdleExitTimeoutOverride = TimeSpan.FromMilliseconds(300)
        }, start: false);

        var run = harness.RunTask;
        var applied = HelperPipeClient.Send(harness.PipeName, new HelperRequest
        {
            Command = HelperCommands.Apply,
            AdapterId = sample.Id,
            Payload = JsonSerializer.Serialize(
                new NetworkConfigRequest { Address = "192.168.1.40", PrefixLength = 24, AutoRollbackSeconds = 600 },
                HelperJson.Options)
        });
        Assert.True(applied.IsSuccess, applied.ToUserText());
        Assert.True(applied.RollbackArmed);

        // 空闲时长（300 ms）早已过去：但倒计时武装期间助手必须继续持有（规划 12.5、D-6）
        await Task.Delay(1200);
        Assert.False(run.IsCompleted);

        var status = HelperPipeClient.Send(harness.PipeName, new HelperRequest { Command = HelperCommands.Status });
        Assert.True(status.IsSuccess, status.ToUserText());
        Assert.True(status.RollbackArmed);
        Assert.True(status.RollbackSecondsRemaining > 500);
    }

    [Fact]
    public void Restore_CallsBackendRestore_AndDisarmsCountdown()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out var sample);

        using var harness = new Harness(backend);
        HelperPipeClient.Send(harness.PipeName, new HelperRequest
        {
            Command = HelperCommands.Apply,
            AdapterId = sample.Id,
            Payload = JsonSerializer.Serialize(
                new NetworkConfigRequest { Address = "192.168.1.41", PrefixLength = 24, AutoRollbackSeconds = 120 },
                HelperJson.Options)
        });
        Assert.True(harness.Server.Countdown.IsArmed);

        var restore = HelperPipeClient.Send(harness.PipeName, new HelperRequest
        {
            Command = HelperCommands.Restore,
            AdapterId = sample.Id,
            Payload = JsonSerializer.Serialize(sample, HelperJson.Options)
        });

        Assert.True(restore.IsSuccess, restore.ToUserText());
        var call = Assert.Single(backend.RestoreCalls);
        // 记录类型里的集合成员不参与值相等，逐字段对比（Id/地址/网关/DNS/DHCP）。
        Assert.Equal(sample.Id, call.Snapshot.Id);
        Assert.Equal(sample.DhcpEnabled, call.Snapshot.DhcpEnabled);
        Assert.Equal(sample.Ipv4.Select(a => a.Cidr), call.Snapshot.Ipv4.Select(a => a.Cidr));
        Assert.Equal(sample.Gateways, call.Snapshot.Gateways);
        Assert.Equal(sample.DnsServers, call.Snapshot.DnsServers);
        Assert.False(harness.Server.Countdown.IsArmed);  // 手工恢复成功后无需再自动回滚
        Assert.False(restore.RollbackArmed);
    }

    [Fact]
    public void Status_DoesNotTouchBackend()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out _);

        using var harness = new Harness(backend);
        var response = HelperPipeClient.Send(harness.PipeName, new HelperRequest { Command = HelperCommands.Status });

        Assert.True(response.IsSuccess, response.ToUserText());
        Assert.Equal(0, backend.TotalCallCount);  // status 是纯查询，不做任何读写
        Assert.Equal(0, response.RollbackSecondsRemaining);
    }

    [Fact]
    public void Apply_BackendFailure_PropagatesErrorCode()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out var sample);
        backend.ApplyResult = NetConfigResult.Fail(ErrorCodes.PlatformPermission, "需要管理员权限");

        using var harness = new Harness(backend);
        var response = HelperPipeClient.Send(harness.PipeName, new HelperRequest
        {
            Command = HelperCommands.Apply,
            AdapterId = sample.Id,
            Payload = JsonSerializer.Serialize(
                new NetworkConfigRequest { Address = "192.168.1.42", PrefixLength = 24 },
                HelperJson.Options)
        });

        Assert.False(response.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformPermission, response.ErrorCode);
        Assert.False(harness.Server.Countdown.IsArmed);
    }

    [Fact]
    public void Apply_UnknownAdapter_WithAutoRollback_FailsWithoutWriting()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out _);

        using var harness = new Harness(backend);
        var response = HelperPipeClient.Send(harness.PipeName, new HelperRequest
        {
            Command = HelperCommands.Apply,
            AdapterId = "不存在的网卡",
            Payload = JsonSerializer.Serialize(
                new NetworkConfigRequest { Address = "192.168.1.43", PrefixLength = 24, AutoRollbackSeconds = 60 },
                HelperJson.Options)
        });

        Assert.False(response.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformAdapterNotFound, response.ErrorCode);
        Assert.Empty(backend.ApplyCalls);
    }

    [Fact]
    public async Task Shutdown_RespondsThenStopsServer()
    {
        var backend = FakeNetworkAdapterBackend.WithSampleAdapter(out _);

        using var harness = new Harness(backend);
        var response = HelperPipeClient.Send(harness.PipeName, new HelperRequest { Command = HelperCommands.Shutdown });

        Assert.True(response.IsSuccess, response.ToUserText());
        await harness.RunTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>测试脚手架：启动一个后台运行的助手服务，Dispose 时取消并等待退出。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();

        public Harness(FakeNetworkAdapterBackend backend, HelperServerOptions? options = null, bool start = true)
        {
            Options = options ?? new HelperServerOptions { PipeName = HelperProtocol.CreatePipeName() };
            PipeName = Options.PipeName;
            Server = new HelperServer(backend, Options, HelperLog.Null);
            RunTask = start ? Server.RunAsync(_cts.Token) : Task.Run(() => Server.RunAsync(_cts.Token));
        }

        public HelperServerOptions Options { get; }

        public HelperServer Server { get; }

        public string PipeName { get; }

        public Task RunTask { get; }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
            }
            catch (Exception)
            {
            }

            try
            {
                RunTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }

            _cts.Dispose();
            Server.Dispose();
        }
    }
}
