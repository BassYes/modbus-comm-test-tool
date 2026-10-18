using Comm.Core.Errors;
using Comm.Platform.Windows;
using Xunit;

namespace Comm.Platform.Windows.Tests;

/// <summary>
/// 网卡配置服务（规划 15.8、4.8.3、R-07 / R-13）。
///
/// 全部用假后端 + 假助手，绝不触碰真实网卡（规划 16.3.7）：
///   - 读：假后端（内存）；
///   - 写：假助手（记录命令，并按需"照做"地改假后端）；
///   - 快照：真文件，写在临时目录里，用例结束即删。
/// </summary>
public class AdapterConfigServiceTests
{
    // ---------------------------------------------------------------- 应用成功路径

    [Fact]
    public void ApplyStatic_success_writes_snapshot_then_sends_apply_and_reads_back()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));
        var adapter = fixture.Adapter;

        var result = fixture.Service.ApplyStatic(adapter.Id, new NetworkConfigRequest
        {
            Address = "192.168.1.60",
            PrefixLength = 24,
            Gateway = "192.168.1.1"
        });

        Assert.True(result.IsSuccess, result.ToUserText());
        Assert.Equal("192.168.1.60", result.Readback!.Ipv4.Single().Address);

        // 第 1 步：快照记录的是"修改前"的状态，且未启用倒计时即视为已保留。
        var snapshot = Assert.Single(fixture.Service.ListSnapshots());
        Assert.Equal(adapter.Id, snapshot.AdapterId);
        Assert.Equal("apply", snapshot.Operation);
        Assert.Equal("192.168.1.50", snapshot.Snapshot.Ipv4.Single().Address);
        Assert.False(snapshot.RollbackArmed);
        Assert.True(snapshot.Kept);
        Assert.Null(snapshot.RollbackDeadline);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.RequestSummary));
        Assert.Matches(@"^[0-9a-z]{8}-\d{8}-\d{6}(-\d+)?\.json$", snapshot.FileName);

        // 第 3 步：只发了一条 apply，且没有 .tmp 残留。
        var request = Assert.Single(fixture.Helper.RequestsOf(HelperCommands.Apply));
        Assert.Equal(adapter.Id, request.AdapterId);
        Assert.Contains("192.168.1.60", request.Payload);
        Assert.Empty(Directory.GetFiles(fixture.SnapDirectory, "*.tmp"));

        // AD-08：服务层绝不直接写网卡（写入只能走提权助手）。
        Assert.Empty(fixture.Backend.WriteCalls);
    }

    [Fact]
    public void ApplyStatic_no_op_request_does_not_touch_adapter_or_snapshot()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));

        var first = fixture.Service.ApplyStatic(fixture.Adapter.Id, new NetworkConfigRequest
        {
            Address = "192.168.1.60",
            PrefixLength = 24
        });
        Assert.True(first.IsSuccess, first.ToUserText());

        var second = fixture.Service.ApplyStatic(fixture.Adapter.Id, new NetworkConfigRequest
        {
            Address = "192.168.1.60",
            PrefixLength = 24
        });

        Assert.True(second.IsSuccess);
        Assert.Contains("不会改变配置", second.Message);
        // 只有第一次真正下发；重放不产生新命令、不产生新快照。
        Assert.Single(fixture.Helper.RequestsOf(HelperCommands.Apply));
        Assert.Single(fixture.Service.ListSnapshots());
    }

    // ---------------------------------------------------------------- 校验失败 / 助手不可用

    [Fact]
    public void ApplyStatic_validation_failure_creates_no_snapshot_and_sends_nothing()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter());

        var result = fixture.Service.ApplyStatic(fixture.Adapter.Id, new NetworkConfigRequest
        {
            Address = "192.168.1.0", // 网络地址
            PrefixLength = 24
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationNetworkConfig, result.ErrorCode);
        Assert.Empty(fixture.Helper.Requests);
        Assert.Empty(fixture.Service.ListSnapshots());
    }

    [Fact]
    public void ApplyStatic_missing_helper_returns_plt5007_and_leaves_no_snapshot()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter());
        fixture.Helper.IsHelperPresent = false;

        var result = fixture.Service.ApplyStatic(fixture.Adapter.Id, new NetworkConfigRequest
        {
            Address = "192.168.1.60",
            PrefixLength = 24
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformHelperUnavailable, result.ErrorCode);
        Assert.Contains(fixture.Helper.ExpectedPath, result.Message);
        Assert.Empty(fixture.Helper.Requests);
        Assert.Empty(fixture.Service.ListSnapshots());
    }

    [Fact]
    public void ApplyStatic_uac_denied_returns_plt5007_and_removes_the_snapshot()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter());
        // 助手文件在，但用户拒绝 UAC：服务层已经把快照落盘，必须回头删掉，不能留未决快照。
        Assert.True(fixture.Helper.IsHelperPresent);
        fixture.Helper.ScriptedResponses[HelperCommands.Apply] =
            HelperResponse.Fail(ErrorCodes.PlatformHelperUnavailable, "用户取消了提权");

        var result = fixture.Service.ApplyStatic(fixture.Adapter.Id, new NetworkConfigRequest
        {
            Address = "192.168.1.61",
            PrefixLength = 24
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformHelperUnavailable, result.ErrorCode);
        Assert.Contains("用户取消了提权", result.Message);
        Assert.Empty(fixture.Service.ListSnapshots());
        Assert.Empty(Directory.GetFiles(fixture.SnapDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ApplyStatic_helper_failure_is_reported_with_helper_error_code_and_rolled_back()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));
        fixture.Helper.ScriptedResponses[HelperCommands.Apply] =
            HelperResponse.Fail(ErrorCodes.PlatformNetConfigApplyFailed, "助手写网卡失败");

        var result = fixture.Service.ApplyStatic(fixture.Adapter.Id, new NetworkConfigRequest
        {
            Address = "192.168.1.62",
            PrefixLength = 24
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformNetConfigApplyFailed, result.ErrorCode);
        // 助手失败也要回退（15.8 第 4 步），这里回滚成功。
        Assert.True(result.RolledBack);
        Assert.Single(fixture.Helper.RequestsOf(HelperCommands.Restore));
    }

    // ---------------------------------------------------------------- 回读校验与回滚

    [Fact]
    public void ApplyStatic_readback_mismatch_returns_plt5005_and_rolls_back()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));
        fixture.Helper.ApplyMutations = false; // 助手说成功，但网卡其实没变

        var result = fixture.Service.ApplyStatic(fixture.Adapter.Id, new NetworkConfigRequest
        {
            Address = "192.168.1.60",
            PrefixLength = 24
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformNetConfigReadbackMismatch, result.ErrorCode);
        Assert.True(result.RolledBack);
        Assert.Single(fixture.Helper.RequestsOf(HelperCommands.Restore));
    }

    [Fact]
    public void ApplyStatic_readback_mismatch_with_failed_rollback_is_not_marked_rolled_back()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));
        fixture.Helper.ApplyMutations = false;
        fixture.Helper.FailRestore = true;

        var result = fixture.Service.ApplyStatic(fixture.Adapter.Id, new NetworkConfigRequest
        {
            Address = "192.168.1.60",
            PrefixLength = 24
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformNetConfigReadbackMismatch, result.ErrorCode);
        Assert.False(result.RolledBack);
    }

    // ---------------------------------------------------------------- 倒计时与未决快照

    [Fact]
    public void Rollback_armed_snapshot_stays_pending_until_confirmed()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));

        var result = fixture.Service.ApplyStatic(fixture.Adapter.Id, new NetworkConfigRequest
        {
            Address = "192.168.1.60",
            PrefixLength = 24,
            AutoRollbackSeconds = 60
        });

        Assert.True(result.IsSuccess, result.ToUserText());
        Assert.Contains("保留", result.Message);

        var pending = fixture.Service.FindPendingSnapshot();
        Assert.NotNull(pending);
        Assert.True(pending!.RollbackArmed);
        Assert.False(pending.Kept);
        Assert.True(pending.IsPending);
        Assert.NotNull(pending.RollbackDeadline);
        Assert.True(pending.RollbackDeadline > pending.CreatedAt);

        var kept = fixture.Service.ConfirmKeep();
        Assert.True(kept.IsSuccess, kept.ToUserText());
        Assert.Single(fixture.Helper.RequestsOf(HelperCommands.Confirm));

        // 确认保留后不应再有未决快照（否则下次启动会重复提示）。
        Assert.Null(fixture.Service.FindPendingSnapshot());
        Assert.All(fixture.Service.ListSnapshots(), file => Assert.True(file.Kept));
    }

    [Fact]
    public void ConfirmKeep_without_pending_snapshot_sends_nothing()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter());

        var result = fixture.Service.ConfirmKeep();

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Helper.Requests);
    }

    [Fact]
    public void FindPendingSnapshot_returns_the_latest_unconfirmed_one()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(
            id: "adapter-aaaa-0001", address: "192.168.1.50"));
        fixture.Backend.Add(FakeAdapterBackend.StaticAdapter(id: "adapter-bbbb-0002", address: "192.168.2.50"));

        Assert.True(fixture.Service.ApplyStatic("adapter-aaaa-0001", new NetworkConfigRequest
        {
            Address = "192.168.1.60",
            PrefixLength = 24,
            AutoRollbackSeconds = 60
        }).IsSuccess);

        // 第二块网卡稍后应用，成为最新的未决快照。
        Assert.True(fixture.Service.ApplyStatic("adapter-bbbb-0002", new NetworkConfigRequest
        {
            Address = "192.168.2.60",
            PrefixLength = 24,
            AutoRollbackSeconds = 60
        }).IsSuccess);

        var pending = fixture.Service.FindPendingSnapshot();
        Assert.NotNull(pending);
        Assert.Equal("adapter-bbbb-0002", pending!.AdapterId);

        // 未启用倒计时的成功操作不会产生未决快照。
        Assert.True(fixture.Service.ApplyStatic("adapter-aaaa-0001", new NetworkConfigRequest
        {
            Address = "192.168.1.61",
            PrefixLength = 24
        }).IsSuccess);
        Assert.Equal(2, fixture.Service.ListSnapshots().Count(f => f.IsPending));
    }

    // ---------------------------------------------------------------- 辅助 IP（R-13）

    [Fact]
    public void AddAuxiliary_sends_only_add_aux_and_keeps_existing_address()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));

        var result = fixture.Service.AddAuxiliaryAddress(fixture.Adapter.Id, "192.168.1.51", 24);

        Assert.True(result.IsSuccess, result.ToUserText());
        var request = Assert.Single(fixture.Helper.RequestsOf(HelperCommands.AddAuxiliary));
        Assert.Empty(fixture.Helper.RequestsOf(HelperCommands.Apply));
        Assert.Empty(fixture.Helper.RequestsOf(HelperCommands.Restore));

        // 负载形如 {"address":"…","prefixLength":24}（规划 4.8.1 / R-13），不含其他地址。
        using var payload = System.Text.Json.JsonDocument.Parse(request.Payload!);
        Assert.Equal("192.168.1.51", payload.RootElement.GetProperty("address").GetString());
        Assert.Equal(24, payload.RootElement.GetProperty("prefixLength").GetInt32());

        var readback = result.Readback!;
        Assert.Equal(2, readback.Ipv4.Count);
        Assert.Contains(readback.Ipv4, a => a.Address == "192.168.1.50");
        Assert.Contains(readback.Ipv4, a => a.Address == "192.168.1.51");
        // 辅助 IP 不动网关（R-13）
        Assert.Equal("192.168.1.1", readback.Gateways.Single());

        var snapshot = Assert.Single(fixture.Service.ListSnapshots());
        Assert.Equal("add-aux", snapshot.Operation);
        Assert.True(snapshot.Kept);
        Assert.False(snapshot.RollbackArmed);
    }

    [Fact]
    public void AddAuxiliary_duplicate_address_is_no_op()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));

        var result = fixture.Service.AddAuxiliaryAddress(fixture.Adapter.Id, "192.168.1.50", 24);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Helper.Requests);
        Assert.Empty(fixture.Service.ListSnapshots());
        Assert.Single(result.Readback!.Ipv4);
    }

    [Fact]
    public void AddAuxiliary_invalid_address_returns_val4009_without_snapshot()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));

        var result = fixture.Service.AddAuxiliaryAddress(fixture.Adapter.Id, "192.168.1.999", 24);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationNetworkConfig, result.ErrorCode);
        Assert.Empty(fixture.Service.ListSnapshots());
    }

    [Fact]
    public void RemoveAuxiliary_sends_only_remove_aux_and_keeps_other_addresses()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));
        Assert.True(fixture.Service.AddAuxiliaryAddress(fixture.Adapter.Id, "192.168.1.51", 24).IsSuccess);
        fixture.Helper.Requests.Clear();

        var result = fixture.Service.RemoveAuxiliaryAddress(fixture.Adapter.Id, "192.168.1.51");

        Assert.True(result.IsSuccess, result.ToUserText());
        var request = Assert.Single(fixture.Helper.RequestsOf(HelperCommands.RemoveAuxiliary));
        Assert.Empty(fixture.Helper.RequestsOf(HelperCommands.Apply));
        Assert.Contains("192.168.1.51", request.Payload);
        Assert.Equal("192.168.1.50", result.Readback!.Ipv4.Single().Address);
    }

    [Fact]
    public void RemoveAuxiliary_missing_address_is_idempotent()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));

        var result = fixture.Service.RemoveAuxiliaryAddress(fixture.Adapter.Id, "192.168.1.99");

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.Helper.Requests);
        Assert.Empty(fixture.Service.ListSnapshots());
    }

    // ---------------------------------------------------------------- DHCP 切换

    [Fact]
    public void SetDhcp_switches_adapter_and_keeps_snapshot_of_static_config()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));

        var result = fixture.Service.SetDhcp(fixture.Adapter.Id, autoRollbackSeconds: 0);

        Assert.True(result.IsSuccess, result.ToUserText());
        Assert.True(result.Readback!.DhcpEnabled);

        var request = Assert.Single(fixture.Helper.RequestsOf(HelperCommands.Apply));
        Assert.Contains("\"DhcpEnabled\":true", request.Payload);

        var snapshot = Assert.Single(fixture.Service.ListSnapshots());
        Assert.False(snapshot.Snapshot.DhcpEnabled);
        Assert.Equal("192.168.1.50", snapshot.Snapshot.Ipv4.Single().Address);
        Assert.Equal("切换到 DHCP", snapshot.RequestSummary);
    }

    [Fact]
    public void SetDhcp_with_countdown_arms_the_snapshot()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));

        var result = fixture.Service.SetDhcp(fixture.Adapter.Id, autoRollbackSeconds: 90);

        Assert.True(result.IsSuccess, result.ToUserText());
        var pending = fixture.Service.FindPendingSnapshot();
        Assert.NotNull(pending);
        Assert.Equal(90, (int)(pending!.RollbackDeadline!.Value - pending.CreatedAt).TotalSeconds);
    }

    // ---------------------------------------------------------------- 快照文件管理

    [Fact]
    public void Snapshots_are_atomic_newest_first_and_capped_at_twenty()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "10.0.0.2"));

        for (int index = 0; index < 25; index++)
        {
            var result = fixture.Service.ApplyStatic(fixture.Adapter.Id, new NetworkConfigRequest
            {
                Address = $"10.0.0.{index + 10}",
                PrefixLength = 24
            });
            Assert.True(result.IsSuccess, result.ToUserText());
        }

        var files = fixture.Service.ListSnapshots();
        Assert.Equal(AdapterConfigService.MaxSnapshotFiles, files.Count);

        // 落盘目录里也只有 20 份，且没有临时文件残留（原子写）。
        Assert.Equal(AdapterConfigService.MaxSnapshotFiles, Directory.GetFiles(fixture.SnapDirectory, "*.json").Length);
        Assert.Empty(Directory.GetFiles(fixture.SnapDirectory, "*.tmp"));

        // 倒序排列（最新在前）。
        for (int index = 1; index < files.Count; index++)
        {
            Assert.True(files[index - 1].CreatedAt >= files[index].CreatedAt);
        }
    }

    [Fact]
    public void ListSnapshots_can_filter_by_adapter()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(
            id: "adapter-aaaa-0001", address: "192.168.1.50"));
        fixture.Backend.Add(FakeAdapterBackend.StaticAdapter(id: "adapter-bbbb-0002", address: "192.168.2.50"));

        Assert.True(fixture.Service.SetDhcp("adapter-aaaa-0001", 0).IsSuccess);
        Assert.True(fixture.Service.SetDhcp("adapter-bbbb-0002", 0).IsSuccess);

        var filtered = fixture.Service.ListSnapshots("adapter-aaaa-0001");
        var only = Assert.Single(filtered);
        Assert.Equal("adapter-aaaa-0001", only.AdapterId);
        Assert.Equal(2, fixture.Service.ListSnapshots().Count);
    }

    // ---------------------------------------------------------------- 回滚

    [Fact]
    public void Restore_uses_the_given_snapshot_and_clears_pending_state()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));

        Assert.True(fixture.Service.ApplyStatic(fixture.Adapter.Id, new NetworkConfigRequest
        {
            Address = "192.168.1.60",
            PrefixLength = 24,
            AutoRollbackSeconds = 60
        }).IsSuccess);

        var pending = fixture.Service.FindPendingSnapshot();
        Assert.NotNull(pending);

        var result = fixture.Service.Restore(pending!);

        Assert.True(result.IsSuccess, result.ToUserText());
        Assert.Equal("192.168.1.50", result.Readback!.Ipv4.Single().Address);

        var restore = Assert.Single(fixture.Helper.RequestsOf(HelperCommands.Restore));
        Assert.Equal(fixture.Adapter.Id, restore.AdapterId);
        Assert.Contains("192.168.1.50", restore.Payload);

        // 回滚完成后未决状态解除（不再提示用户恢复）。
        Assert.Null(fixture.Service.FindPendingSnapshot());
        Assert.True(Assert.Single(fixture.Service.ListSnapshots()).Kept);
    }

    [Fact]
    public void Restore_null_snapshot_fails_with_plt5006()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter());

        var result = fixture.Service.Restore(null!);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformNetConfigRollbackFailed, result.ErrorCode);
        Assert.Empty(fixture.Helper.Requests);
    }

    [Fact]
    public void Restore_helper_failure_returns_plt5006()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter(address: "192.168.1.50"));
        Assert.True(fixture.Service.ApplyStatic(fixture.Adapter.Id, new NetworkConfigRequest
        {
            Address = "192.168.1.60",
            PrefixLength = 24,
            AutoRollbackSeconds = 60
        }).IsSuccess);

        fixture.Helper.FailRestore = true;
        var result = fixture.Service.Restore(fixture.Service.FindPendingSnapshot()!);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformNetConfigRollbackFailed, result.ErrorCode);
    }

    // ---------------------------------------------------------------- 网卡不存在

    [Fact]
    public void Unknown_adapter_returns_plt5003()
    {
        using var fixture = new Fixture(FakeAdapterBackend.StaticAdapter());

        var result = fixture.Service.ApplyStatic("不存在的网卡", new NetworkConfigRequest
        {
            Address = "192.168.1.60",
            PrefixLength = 24
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformAdapterNotFound, result.ErrorCode);
    }

    // ---------------------------------------------------------------- 夹具

    /// <summary>一块假网卡 + 假助手 + 服务实例 + 临时快照目录。</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        public Fixture(AdapterSnapshot adapter)
        {
            Adapter = adapter;
            _root = TestPaths.NewDirectory();

            Backend = new FakeAdapterBackend().Add(adapter);
            Helper = new FakeNetConfigHelper(Backend);
            Service = new AdapterConfigService(Backend, Helper, _root);
            SnapDirectory = Path.Combine(_root, "netsnap");
        }

        public AdapterSnapshot Adapter { get; }

        public FakeAdapterBackend Backend { get; }

        public FakeNetConfigHelper Helper { get; }

        public AdapterConfigService Service { get; }

        public string SnapDirectory { get; }

        public void Dispose() => TestPaths.TryDelete(_root);
    }
}
