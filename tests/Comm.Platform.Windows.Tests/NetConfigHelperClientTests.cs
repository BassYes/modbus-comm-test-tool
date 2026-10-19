using System.Diagnostics;
using System.IO.Pipes;
using Comm.Core.Errors;
using Comm.Platform.Windows;
using Xunit;

namespace Comm.Platform.Windows.Tests;

/// <summary>
/// 提权助手客户端（规划 12.5 / AD-08）。
/// 这里只验证"不需要真提权"的路径：命令白名单、助手缺失、UAC 被拒后不重复弹窗、协议细节，
/// 以及用测试内的假管道服务器跑通一次真实的命名管道握手（不弹 UAC、不改网卡）。
/// 真实 UAC 弹窗与真实助手进程拉起无法在自动化测试里稳定复现，交内部说明的"未验证点"。
/// </summary>
public class NetConfigHelperClientTests
{
    [Fact]
    public void Send_rejects_unknown_command_without_touching_helper()
    {
        var launcher = new FakeHelperLauncher();
        using var client = new NetConfigHelperClient(launcher, ExistingHelperFile());

        var response = client.Send(new HelperRequest { Command = "format-c:" });

        Assert.False(response.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformHelperProtocol, response.ErrorCode);
        Assert.Contains("未知命令", response.Message);
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public void Send_reports_plt5007_when_helper_executable_is_missing()
    {
        var launcher = new FakeHelperLauncher();
        var missing = Path.Combine(TestPaths.NewDirectory(), "Comm.NetConfigHelper.exe");
        using var client = new NetConfigHelperClient(launcher, missing);

        Assert.False(client.IsHelperPresent);
        Assert.StartsWith("未找到助手：", client.StatusText);

        var response = client.Send(new HelperRequest { Command = HelperCommands.Snapshot });

        Assert.False(response.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformHelperUnavailable, response.ErrorCode);
        Assert.Contains("未找到助手", response.Message);
        // 助手文件都不在，不该去弹 UAC。
        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public void Uac_denied_is_remembered_and_does_not_pop_up_again()
    {
        var launcher = new FakeHelperLauncher
        {
            Result = new HelperLaunchResult(null, ElevationDenied: true, "用户取消了 UAC 提权")
        };
        using var client = new NetConfigHelperClient(launcher, ExistingHelperFile());

        var first = client.Send(new HelperRequest { Command = HelperCommands.Snapshot });
        Assert.False(first.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformHelperUnavailable, first.ErrorCode);
        Assert.Equal(1, launcher.LaunchCount);
        Assert.True(client.ElevationDenied);
        Assert.Equal("用户取消了提权", client.StatusText);

        // 第二次发送：不再拉起助手（不重复弹窗），直接给明确错误。
        var second = client.Send(new HelperRequest { Command = HelperCommands.Snapshot });
        Assert.False(second.IsSuccess);
        Assert.Equal(ErrorCodes.PlatformHelperUnavailable, second.ErrorCode);
        Assert.Contains("重新授权", second.Message);
        Assert.Equal(1, launcher.LaunchCount);

        // 用户主动点击"重新授权"后才允许再次拉起。
        client.ResetElevationDenied();
        Assert.False(client.Send(new HelperRequest { Command = HelperCommands.Snapshot }).IsSuccess);
        Assert.Equal(2, launcher.LaunchCount);
    }

    [Fact]
    public void First_send_generates_pipe_name_and_passes_idle_exit_minutes()
    {
        var launcher = new FakeHelperLauncher();
        using var client = new NetConfigHelperClient(launcher, ExistingHelperFile(), idleExitMinutes: 7);

        // 助手进程不存在（假启动器返回 null），因此 Send 会失败——但握手参数必须已经准备好。
        client.Send(new HelperRequest { Command = HelperCommands.Snapshot });

        Assert.Equal(1, launcher.LaunchCount);
        Assert.StartsWith("comm-netcfg-", launcher.LastPipeName);
        Assert.Equal(7, launcher.LastIdleExitMinutes);
        Assert.Equal(client.ExpectedPath, launcher.LastHelperPath);
    }

    [Fact]
    public void Send_round_trips_over_a_named_pipe_created_by_the_helper()
    {
        // 用测试内的假管道服务器模拟提权助手：验证真实握手（连接 → 长度前缀帧 → 响应），
        // 但不弹 UAC、不启动真实助手、不触碰网卡。
        var launcher = new PipeServerLauncher();
        using var client = new NetConfigHelperClient(launcher, ExistingHelperFile());

        var response = client.Send(new HelperRequest
        {
            Command = HelperCommands.Apply,
            AdapterId = "adapter-1",
            Payload = "{\"Address\":\"192.168.1.50\"}",
            Confirmed = true
        });

        Assert.True(response.IsSuccess, response.ToUserText());
        Assert.Equal("假助手已处理", response.Message);
        Assert.Equal(1, launcher.LaunchCount);

        var request = Assert.Single(launcher.Requests);
        Assert.Equal(HelperCommands.Apply, request.Command);
        Assert.Equal("adapter-1", request.AdapterId);
        Assert.True(request.Confirmed);
        // 一次成功交互后，状态栏显示助手返回的结果（供界面展示）。
        Assert.Equal("假助手已处理", client.StatusText);
    }

    [Fact]
    public void RunAs_helper_launcher_reports_missing_file_instead_of_throwing()
    {
        var result = new RunAsHelperLauncher().Launch(
            Path.Combine(TestPaths.NewDirectory(), "Comm.NetConfigHelper.exe"),
            "comm-netcfg-test", 5);

        Assert.Null(result.Process);
        Assert.False(result.ElevationDenied);
        Assert.Contains("找不到提权助手", result.Error);
    }

    [Fact]
    public void RunAs_helper_launcher_reports_empty_path_instead_of_throwing()
    {
        var result = new RunAsHelperLauncher().Launch(string.Empty, "comm-netcfg-test", 5);

        Assert.Null(result.Process);
        Assert.False(result.ElevationDenied);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void RunAs_helper_launcher_reports_uac_cancellation_message()
    {
        // ERROR_CANCELLED 的文案是契约的一部分（界面直接显示它）。
        Assert.Equal(1223, RunAsHelperLauncher.ErrorCancelled);
    }

    [Fact]
    public void Dispose_without_launching_helper_is_safe()
    {
        var launcher = new FakeHelperLauncher();
        var client = new NetConfigHelperClient(launcher, ExistingHelperFile());

        client.Dispose();
        client.Dispose(); // 重复释放也必须安全

        Assert.Equal(0, launcher.LaunchCount);
    }

    [Fact]
    public void Dispose_sends_shutdown_to_a_running_helper()
    {
        var launcher = new PipeServerLauncher();
        var client = new NetConfigHelperClient(launcher, ExistingHelperFile());

        Assert.True(client.Send(new HelperRequest { Command = HelperCommands.Status }).IsSuccess);
        client.Dispose();

        Assert.Contains(launcher.Requests, request => request.Command == HelperCommands.Shutdown);
    }

    [Fact]
    public void Pipe_names_are_unique_per_run()
    {
        // 规划 12.5：管道名必须带随机后缀，避免被其他进程抢注。
        var names = Enumerable.Range(0, 20).Select(_ => HelperProtocol.CreatePipeName()).ToArray();

        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.All(names, name => Assert.StartsWith("comm-netcfg-", name));
        Assert.All(names, name => Assert.Equal(name, HelperProtocol.PipeName(name)));
    }

    [Fact]
    public void Helper_protocol_round_trips_request_through_stream()
    {
        var request = new HelperRequest
        {
            Command = HelperCommands.Apply,
            AdapterId = "adapter-1",
            Payload = "{\"Address\":\"192.168.1.10\"}",
            Confirmed = true
        };

        using var stream = new MemoryStream();
        HelperProtocol.WriteMessage(stream, HelperProtocol.Serialize(request));
        stream.Position = 0;

        var json = HelperProtocol.ReadMessage(stream);
        var parsed = HelperProtocol.ParseRequest(json!);

        Assert.NotNull(parsed);
        Assert.Equal(request.Command, parsed!.Command);
        Assert.Equal(request.AdapterId, parsed.AdapterId);
        Assert.Equal(request.Payload, parsed.Payload);
        Assert.True(parsed.Confirmed);
    }

    /// <summary>造一个"存在但不会被真的执行"的助手文件路径（只验证存在性检查）。</summary>
    private static string ExistingHelperFile()
    {
        var directory = TestPaths.NewDirectory();
        var path = Path.Combine(directory, NetConfigHelperLocator.FileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    /// <summary>
    /// 测试用"助手进程"：用后台任务在客户端生成的管道名上监听，按协议循环处理请求/响应，
    /// 收到 shutdown（或测试结束时）退出。返回当前进程对象只是为了满足
    /// <see cref="HelperLaunchResult"/> 的契约，客户端不会 Kill 它。
    /// </summary>
    private sealed class PipeServerLauncher : IHelperLauncher
    {
        private readonly object _sync = new();
        private readonly List<HelperRequest> _requests = new();

        public int LaunchCount { get; private set; }

        public IReadOnlyList<HelperRequest> Requests
        {
            get { lock (_sync) return _requests.ToArray(); }
        }

        public HelperLaunchResult Launch(string helperPath, string pipeName, int idleExitMinutes)
        {
            LaunchCount++;
            Task.Run(() => ServeAsync(pipeName, idleExitMinutes));
            return new HelperLaunchResult(Process.GetCurrentProcess(), false, null);
        }

        private async Task ServeAsync(string pipeName, int idleExitMinutes)
        {
            try
            {
                while (true)
                {
                    using var server = new NamedPipeServerStream(
                        HelperProtocol.PipeName(pipeName),
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync().ConfigureAwait(false);

                    var json = HelperProtocol.ReadMessage(server);
                    var request = HelperProtocol.ParseRequest(json ?? string.Empty);
                    if (request is null) return;

                    lock (_sync) _requests.Add(request);

                    var response = request.Command switch
                    {
                        HelperCommands.Shutdown => HelperResponse.Ok(message: "助手退出"),
                        HelperCommands.Status => HelperResponse.Ok(message: "助手空闲"),
                        _ => HelperResponse.Ok(message: "假助手已处理")
                    };

                    HelperProtocol.WriteMessage(server, HelperProtocol.Serialize(response));
                    // idleExitMinutes 只是契约参数：假助手在收到 shutdown 时退出。
                    if (request.Command == HelperCommands.Shutdown || idleExitMinutes <= 0) return;
                }
            }
            catch (Exception)
            {
                // 测试辅助线程：客户端关闭/管道中断都直接结束，不影响用例断言。
            }
        }
    }
}
