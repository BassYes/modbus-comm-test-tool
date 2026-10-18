using System.Collections.ObjectModel;
using Comm.App.Wpf.Services;
using Comm.Core;
using Comm.Core.Codecs;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Session;
using Comm.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>
/// 手动报文与校验自动填入（规划 4.5，需求 R-04）。
/// R-19：显式选择 RTU / TCP，**未连接设备也能生成与校验报文**；只有“发送”需要活动会话。
/// 校验计算全部由编解码器完成，界面不含任何算法。
/// </summary>
public partial class ManualSendViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _app;
    private readonly SessionContext _session;
    private CancellationTokenSource? _loopCts;

    public ManualSendViewModel(AppServices app, SessionContext session)
    {
        _app = app;
        _session = session;
        RefreshTemplates();

        // 寄存器定位页可以把生成的读取报文送到这里（M2 联动）
        _app.HexRequested += OnHexRequested;
    }

    public IReadOnlyList<Option<FrameTransport>> TransportOptions { get; } = new[]
    {
        new Option<FrameTransport>(FrameTransport.FollowSession, "跟随当前连接"),
        new Option<FrameTransport>(FrameTransport.Rtu, "Modbus RTU"),
        new Option<FrameTransport>(FrameTransport.Tcp, "Modbus TCP")
    };

    /// <summary>已收藏的报文模板（规划 4.5.4、6.5）。</summary>
    public ObservableCollection<FrameTemplate> Templates { get; } = new();

    public IReadOnlyList<Option<byte>> FunctionOptions { get; } = new[]
    {
        new Option<byte>(Fn.ReadHoldingRegisters, "03 读保持寄存器"),
        new Option<byte>(Fn.ReadInputRegisters, "04 读输入寄存器"),
        new Option<byte>(Fn.ReadCoils, "01 读线圈"),
        new Option<byte>(Fn.ReadDiscreteInputs, "02 读离散输入"),
        new Option<byte>(Fn.WriteSingleRegister, "06 写单个寄存器"),
        new Option<byte>(Fn.WriteSingleCoil, "05 写单个线圈"),
        new Option<byte>(Fn.WriteMultipleRegisters, "10 写多个寄存器(原始)"),
        new Option<byte>(Fn.WriteMultipleCoils, "0F 写多个线圈(原始)")
    };

    [ObservableProperty] private FrameTransport _transport = FrameTransport.FollowSession;
    [ObservableProperty] private string _templateName = string.Empty;
    [ObservableProperty] private FrameTemplate? _selectedTemplate;
    [ObservableProperty] private bool _transactionIdAutoIncrement = true;

    [ObservableProperty] private byte _unitId = 1;
    [ObservableProperty] private byte _functionCode = Fn.ReadHoldingRegisters;
    [ObservableProperty] private int _startAddress;
    [ObservableProperty] private int _quantity = 1;
    [ObservableProperty] private int _writeValue;
    [ObservableProperty] private bool _coilOn = true;
    [ObservableProperty] private int _tcpTransactionId = 1;
    [ObservableProperty] private byte[] _extraData = Array.Empty<byte>();

    [ObservableProperty] private string _hexText = string.Empty;
    [ObservableProperty] private string _previewText = "尚未生成报文";
    [ObservableProperty] private string _validationText = string.Empty;
    [ObservableProperty] private bool _crcLocked;
    [ObservableProperty] private bool _allowBadChecksum;
    [ObservableProperty] private bool _expectResponse = true;
    [ObservableProperty] private int? _timeoutOverrideMs;
    [ObservableProperty] private int _loopIntervalMs = 1000;

    /// <summary>
    /// 循环发送次数上限（审计 F-07）。循环写会反复修改设备数据，必须有一个硬上限：
    /// 到达后自动停止，不允许"一次确认、无界发送"。
    /// </summary>
    [ObservableProperty] private int _loopMaxCount = 100;
    [ObservableProperty] private bool _isLooping;
    [ObservableProperty] private int _loopSentCount;
    [ObservableProperty] private string _responseText = "（尚无响应）";
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _mbapText = string.Empty;

    public ObservableCollection<string> History { get; } = new();

    /// <summary>当前生效的报文格式（含未连接时的显式选择）。</summary>
    public string EffectiveTransportText => ResolveTransport() switch
    {
        FrameTransport.Tcp => "Modbus TCP",
        _ => "Modbus RTU"
    };

    public bool IsTcpSelected => ResolveTransport() == FrameTransport.Tcp;

    public string ModeHint => ResolveTransport() switch
    {
        FrameTransport.Tcp =>
            "当前为 Modbus TCP：不追加 RTU CRC，程序自动计算 MBAP 长度字段；未连接设备也可生成报文。",
        _ => "当前为 Modbus RTU：程序自动计算并回填 CRC-16（低字节在前）；未连接设备也可生成报文。"
    };

    public string SendAvailabilityText => _session.IsConnected
        ? $"已连接 {_session.Snapshot?.Endpoint}（{_session.Current?.Codec.Name}），可直接发送。"
        : "尚未连接：可以生成、校验、保存模板；发送需要先在“连接中心”建立连接。";

    private FrameTransport ResolveTransport() => Transport switch
    {
        FrameTransport.Rtu => FrameTransport.Rtu,
        FrameTransport.Tcp => FrameTransport.Tcp,
        _ => _session.Kind == ConnectionKind.Tcp ? FrameTransport.Tcp : FrameTransport.Rtu
    };

    private ManualSendService CreateService() =>
        _session.CreateManualSendService(ResolveTransport(), _session.SilenceThresholdMs);

    partial void OnTransportChanged(FrameTransport value)
    {
        OnPropertyChanged(nameof(EffectiveTransportText));
        OnPropertyChanged(nameof(IsTcpSelected));
        OnPropertyChanged(nameof(ModeHint));
        Revalidate();
    }

    /// <summary>按当前格式重新校验并刷新预览（切换格式后必须重算校验字段）。</summary>
    private void Revalidate()
    {
        if (string.IsNullOrWhiteSpace(HexText))
        {
            ValidationText = string.Empty;
            PreviewText = "尚未生成报文";
            MbapText = string.Empty;
            return;
        }

        var service = CreateService();
        var parsed = HexUtil.TryParse(HexText, out var error);
        if (parsed is null)
        {
            ValidationText = $"输入有问题：{error}";
            return;
        }

        var validation = service.Validate(parsed);
        ValidationText = validation.Message;
        PreviewText = service.Describe(parsed);
        MbapText = service.DescribeMbufFields(parsed);
    }

    partial void OnHexTextChanged(string value)
    {
        if (CrcLocked) return;
        if (string.IsNullOrWhiteSpace(value))
        {
            ValidationText = string.Empty;
            PreviewText = "尚未生成报文";
            return;
        }

        var service = CreateService();

        var parsed = HexUtil.TryParse(value, out var error);
        if (parsed is null)
        {
            ValidationText = $"输入有问题：{error}";
            return;
        }

        var validation = service.Validate(parsed);
        ValidationText = validation.Message;
        PreviewText = service.Describe(parsed);
        MbapText = service.DescribeMbufFields(parsed);
    }

    [RelayCommand]
    private void BuildFromFields()
    {
        // R-19：未连接也能生成——格式由“报文格式”下拉框决定，而不是由会话决定。
        var service = CreateService();

        byte[] data;
        switch (FunctionCode)
        {
            case Fn.ReadHoldingRegisters:
            case Fn.ReadInputRegisters:
            case Fn.ReadCoils:
            case Fn.ReadDiscreteInputs:
                data = new[]
                {
                    (byte)(StartAddress >> 8), (byte)(StartAddress & 0xFF),
                    (byte)(Quantity >> 8), (byte)(Quantity & 0xFF)
                };
                break;

            case Fn.WriteSingleRegister:
                data = new[]
                {
                    (byte)(StartAddress >> 8), (byte)(StartAddress & 0xFF),
                    (byte)(WriteValue >> 8), (byte)(WriteValue & 0xFF)
                };
                break;

            case Fn.WriteSingleCoil:
                data = new[]
                {
                    (byte)(StartAddress >> 8), (byte)(StartAddress & 0xFF),
                    CoilOn ? (byte)0xFF : (byte)0x00, (byte)0x00
                };
                break;

            default:
                data = ExtraData;
                break;
        }

        var frame = service.BuildFrame(UnitId, FunctionCode, data, (ushort)TcpTransactionId);
        HexText = HexUtil.ToHex(frame);
        ValidationText = service.Validate(frame).Message;
        PreviewText = service.Describe(frame);
        MbapText = service.DescribeMbufFields(frame);

        StatusMessage = service.Codec.ChecksumLength > 0
            ? $"已生成完整 RTU 报文，并自动填入 CRC：{HexUtil.ToHex(frame[^2..])}（低字节在前）"
            : "已生成完整 Modbus TCP 报文，并自动计算 MBAP 长度字段（TCP 不追加 CRC）";

        if (TransactionIdAutoIncrement) TcpTransactionId = (TcpTransactionId + 1) & 0xFFFF;
    }

    /// <summary>“自动填入校验位”：只对当前输入的主体追加校验字段。</summary>
    [RelayCommand]
    private void AppendChecksum()
    {
        var service = CreateService();

        if (service.Codec.ChecksumLength == 0)
        {
            StatusMessage = "Modbus TCP 不追加 RTU CRC；MBAP 长度由程序在发送与生成时自动计算，无需手工填写校验位。";
            return;
        }

        var body = HexUtil.TryParse(HexText, out var error);
        if (body is null)
        {
            _app.Dialogs.Warn("输入有问题", error ?? "报文解析失败");
            return;
        }

        // 若输入已包含 CRC，则先去除再重新计算，避免重复追加。
        if (body.Length > 4 && service.Validate(body).IsValid) body = body[..^2];

        var frame = service.AppendChecksum(body);
        HexText = HexUtil.ToHex(frame);
        ValidationText = service.Validate(frame).Message;
        StatusMessage = $"已自动计算并填入校验位：{HexUtil.ToHex(frame[^service.Codec.ChecksumLength..])}";
        _app.Log.Debug("ManualSend", $"计算校验：主体 {HexUtil.ToHex(body)} → 校验 {HexUtil.ToHex(frame[^2..])}");
    }

    [RelayCommand]
    private void Validate()
    {
        var service = CreateService();

        var frame = HexUtil.TryParse(HexText, out var error);
        if (frame is null)
        {
            _app.Dialogs.Warn("输入有问题", error ?? "报文解析失败");
            return;
        }

        var validation = service.Validate(frame);
        ValidationText = validation.Message;
        PreviewText = service.Describe(frame);
        StatusMessage = validation.Message;
    }

    [RelayCommand]
    private async Task SendOnceAsync()
    {
        var service = CreateService();
        var parsed = service.ParseHex(HexText);
        if (!parsed.IsSuccess)
        {
            _app.Dialogs.Warn("报文不可发送", parsed.Error!.Message);
            return;
        }

        if (!service.CanSend)
        {
            _app.Dialogs.Warn("尚未连接", "生成与校验不需要连接，但发送需要在“连接中心”建立连接。");
            return;
        }

        if (!service.MatchesSessionProtocol)
        {
            _app.Dialogs.Warn("报文格式不匹配",
                $"当前选择的是 {service.Codec.Name}，而活动连接是 {_session.Current?.Codec.Name}。\n\n" +
                "请把“报文格式”改回“跟随当前连接”，或连接到对应协议的设备。");
            return;
        }

        var frame = parsed.Value!;
        var function = service.Codec.TryGetFunctionCode(frame) ?? 0;
        var unit = service.Codec.TryGetUnitId(frame) ?? 0;
        bool broadcast = unit == 0;

        bool confirmed = true;
        if (ManualSendService.RequiresWriteConfirmation(unit, function, out var reason))
        {
            confirmed = _app.Dialogs.ConfirmWrite(
                $"从站 {unit}", function, $"0x{HexUtil.ToHex(frame[2..4])}", HexUtil.ToHex(frame), broadcast);
            if (!confirmed)
            {
                StatusMessage = "用户取消了写操作。";
                _app.Log.Warning("ManualSend", $"用户取消写操作：{HexUtil.ToHex(frame)}");
                return;
            }
        }

        await SendFrameCoreAsync(service, frame, confirmed, broadcast).ConfigureAwait(true);
    }

    private async Task SendFrameCoreAsync(ManualSendService service, byte[] frame, bool confirmed, bool broadcast)
    {
        var outcome = await service.SendFrameAsync(frame, confirmed, broadcast, AllowBadChecksum, ExpectResponse,
            TimeoutOverrideMs, CancellationToken.None).ConfigureAwait(true);

        StatusMessage = outcome.Message;
        _app.Log.Info("ManualSend",
            $"发送 {HexUtil.ToHex(frame)} → {outcome.Message}" +
            (outcome.Result?.Response is { } response ? $"；响应 {HexUtil.ToHex(response)}" : string.Empty));

        ResponseText = DescribeResult(outcome);
        AddHistory($"[{DateTime.Now:HH:mm:ss}] TX {HexUtil.ToHex(frame)} → {outcome.StatusText}" +
                   (outcome.Result?.Response is { } resp ? $" / RX {HexUtil.ToHex(resp)}" : string.Empty));
    }

    private static string DescribeResult(ManualSendOutcome outcome)
    {
        if (outcome.Result is null) return outcome.Message;

        var result = outcome.Result;
        var text = $"状态：{result.StatusText}\n耗时：{result.Elapsed.TotalMilliseconds:F1} ms\n尝试次数：{result.Attempts}\n";
        if (result.Response is { Length: > 0 } response)
        {
            text += $"响应报文：{HexUtil.ToHex(response)}\n";
            text += $"响应解析：{result.ResponsePdu?.Describe() ?? "(无)"}\n";
        }
        if (result.ExceptionCode is { } code) text += $"异常码含义：{ModbusExceptions.Describe(code)}\n建议：{ModbusExceptions.Hint(code)}\n";
        if (result.Error is not null) text += $"错误：{result.Error.Message}\n错误码：{result.Error.Code}\n建议：{result.Error.Hint}\n";
        return text;
    }

    private void AddHistory(string text)
    {
        History.Insert(0, text);
        while (History.Count > 200) History.RemoveAt(History.Count - 1);
    }

    [RelayCommand]
    private Task ToggleLoopAsync()
    {
        if (IsLooping)
        {
            _loopCts?.Cancel();
            IsLooping = false;
            StatusMessage = $"循环发送已停止，共发送 {LoopSentCount} 次。";
            return Task.CompletedTask;
        }

        var service = CreateService();
        var parsed = service.ParseHex(HexText);
        if (!parsed.IsSuccess)
        {
            _app.Dialogs.Warn("报文不可发送", parsed.Error!.Message);
            return Task.CompletedTask;
        }

        if (!service.CanSend || !service.MatchesSessionProtocol)
        {
            _app.Dialogs.Warn("无法循环发送", "循环发送需要活动连接，且报文格式必须与当前连接协议一致。");
            return Task.CompletedTask;
        }

        var frame = parsed.Value!;
        var function = service.Codec.TryGetFunctionCode(frame) ?? 0;
        var unit = service.Codec.TryGetUnitId(frame) ?? 0;
        bool broadcast = unit == 0;
        bool confirmed = true;
        bool isWrite = ManualSendService.RequiresWriteConfirmation(unit, function, out _);

        // 审计 F-07：次数必须有硬上限；写操作（尤其广播写）的最低间隔不能低到"打爆总线"。
        var maxCount = Math.Clamp(LoopMaxCount <= 0 ? 1 : LoopMaxCount, 1, 100_000);
        int interval = Math.Max(isWrite ? 50 : 10, LoopIntervalMs);

        if (isWrite)
        {
            var limitText = $"最多发送 {maxCount} 次（到达上限自动停止），最小间隔 {interval} ms。";

            if (!_app.Dialogs.Confirm("循环写操作确认",
                    $"即将每 {interval} ms 向从站 {unit} 重复发送写报文：\n{HexUtil.ToHex(frame)}\n\n" +
                    limitText + "\n" +
                    (broadcast ? "目标为广播地址，总线上所有设备都会受影响。\n" : string.Empty) +
                    "循环写会反复修改设备数据，确认继续？"))
            {
                return Task.CompletedTask;
            }

            // 广播循环写：在写确认之外再要一次专项确认（规划 15.7 对广播的更高要求）。
            if (broadcast &&
                !_app.Dialogs.Confirm("广播循环写二次确认",
                    $"目标为广播地址 0：本次操作会让总线上所有从站反复执行写功能码 0x{function:X2}。\n\n" +
                    "现场可能因此停机或误动作。确认已获得授权并承担影响后继续？"))
            {
                return Task.CompletedTask;
            }

            confirmed = true;
        }

        _loopCts = new CancellationTokenSource();
        IsLooping = true;
        LoopSentCount = 0;
        var loopCts = _loopCts;
        var token = loopCts.Token;

        _app.Log.Info("ManualSend",
            $"开始循环发送：{HexUtil.ToHex(frame)}，间隔 {interval} ms，上限 {maxCount} 次" +
            (isWrite ? "（写操作）" : string.Empty));

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(interval));
            try
            {
                do
                {
                    var outcome = await service.SendFrameAsync(frame, confirmed, broadcast, AllowBadChecksum,
                        ExpectResponse, TimeoutOverrideMs, token).ConfigureAwait(false);
                    LoopSentCount++;
                    var text = DescribeResult(outcome);
                    var line = $"[{DateTime.Now:HH:mm:ss}] TX {HexUtil.ToHex(frame)} → {outcome.StatusText}";

                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        ResponseText = text;
                        AddHistory(line);
                    });

                    // 审计 F-07：到达次数上限后自动停止，并明确告知（不是静默停）。
                    if (LoopSentCount >= maxCount)
                    {
                        loopCts.Cancel();
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            IsLooping = false;
                            StatusMessage = $"循环发送已达上限 {maxCount} 次，已自动停止。";
                            AddHistory($"[{DateTime.Now:HH:mm:ss}] 循环发送达到上限 {maxCount} 次，已自动停止");
                        });
                        return;
                    }
                }
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
            }
        }, token);

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();
        ResponseText = "（尚无响应）";
        LoopSentCount = 0;
    }

    // ---------------------------------------------------------------- 报文模板（规划 4.5.4、6.5）

    [RelayCommand]
    private void SaveTemplate()
    {
        var service = CreateService();
        var parsed = service.ParseHex(HexText);
        if (!parsed.IsSuccess)
        {
            _app.Dialogs.Warn("无法收藏", parsed.Error!.Message);
            return;
        }

        var frame = parsed.Value!;
        var function = service.Codec.TryGetFunctionCode(frame) ?? 0;
        var isWrite = Fn.IsWrite(function);
        var transport = ResolveTransport() == FrameTransport.Tcp ? "tcp" : "rtu";

        var template = new FrameTemplate
        {
            Name = string.IsNullOrWhiteSpace(TemplateName)
                ? $"{transport.ToUpperInvariant()} {HexUtil.ToHex(frame)}"
                : TemplateName.Trim(),
            Transport = transport,
            Hex = HexUtil.ToHex(frame),
            IsWrite = isWrite,
            Notes = service.Describe(frame)
        };

        var error = _app.SaveTemplate(template);
        if (!string.IsNullOrEmpty(error))
        {
            _app.Dialogs.Error("保存失败", error);
            return;
        }

        RefreshTemplates();
        StatusMessage = isWrite
            ? $"已收藏报文模板（写操作）：{template.Name}。从模板重发时会再次要求确认。"
            : $"已收藏报文模板：{template.Name}";
    }

    [RelayCommand]
    private void ApplyTemplate()
    {
        if (SelectedTemplate is null) return;

        var template = SelectedTemplate;
        Transport = template.Transport.Equals("tcp", StringComparison.OrdinalIgnoreCase)
            ? FrameTransport.Tcp
            : FrameTransport.Rtu;
        HexText = template.Hex;
        TemplateName = template.Name;
        Revalidate();

        StatusMessage = template.IsWrite
            ? $"已应用模板：{template.DisplayText}。注意：这是写报文，发送前会再次要求二次确认。"
            : $"已应用模板：{template.DisplayText}";
    }

    [RelayCommand]
    private void DeleteTemplate()
    {
        if (SelectedTemplate is null) return;

        var name = SelectedTemplate.DisplayText;
        var error = _app.RemoveTemplate(SelectedTemplate.Id);
        if (!string.IsNullOrEmpty(error))
        {
            _app.Dialogs.Error("删除失败", error);
            return;
        }

        RefreshTemplates();
        StatusMessage = $"已删除模板：{name}";
    }

    private void RefreshTemplates()
    {
        Templates.Clear();
        foreach (var template in _app.Settings.Templates.OrderByDescending(t => t.UpdatedAt)) Templates.Add(template);
    }

    [RelayCommand]
    private void UseHistoryItem(string? item)
    {
        if (string.IsNullOrWhiteSpace(item)) return;
        var start = item.IndexOf("TX ", StringComparison.Ordinal);
        if (start < 0) return;
        start += 3;
        int end = item.IndexOf(" →", start, StringComparison.Ordinal);
        var hex = end > start ? item[start..end] : item[start..];
        HexText = hex.Trim();
        StatusMessage = "已从历史记录载入报文。注意：如为写操作，重发会再次修改设备数据。";
    }

    // ---------------------------------------------------------------- 跨页面事件与释放（M3b：页面属于某个会话）

    private void OnHexRequested(object? sender, HexPayload payload)
    {
        HexText = payload.Hex;
        StatusMessage = payload.Message;
    }

    /// <summary>关闭会话时由工作区调用：停止循环发送并解除全局事件订阅。</summary>
    public void Dispose()
    {
        _app.HexRequested -= OnHexRequested;
        try
        {
            _loopCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经释放
        }
    }

}
