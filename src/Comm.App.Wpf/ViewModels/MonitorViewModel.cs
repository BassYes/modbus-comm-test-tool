using Comm.App.Wpf.Services;
using Comm.Core;
using Comm.Core.Frames;
using Comm.Protocols.Modbus;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>实时报文监视（见规划 4.6）。</summary>
public partial class MonitorViewModel : ObservableObject
{
    private readonly AppServices _app;

    public MonitorViewModel(AppServices app)
    {
        _app = app;
        app.Monitor.PropertyChanged += (_, _) => RefreshStatus();
        app.Monitor.RowsChanged += (_, _) => RefreshStatus();
        RefreshStatus();
    }

    public MonitorService Monitor => _app.Monitor;

    public IReadOnlyList<Option<DirectionFilter>> DirectionOptions { get; } = new[]
    {
        new Option<DirectionFilter>(DirectionFilter.All, "全部"),
        new Option<DirectionFilter>(DirectionFilter.TxOnly, "仅发送 TX"),
        new Option<DirectionFilter>(DirectionFilter.RxOnly, "仅接收 RX"),
        new Option<DirectionFilter>(DirectionFilter.ErrorsOnly, "仅错误/异常"),
        new Option<DirectionFilter>(DirectionFilter.SystemOnly, "仅系统事件")
    };

    [ObservableProperty] private FrameRecord? _selectedRecord;
    [ObservableProperty] private string _detailText = "选中一行报文可查看详情与解析。";
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _message = string.Empty;

    partial void OnSelectedRecordChanged(FrameRecord? value) => DetailText = BuildDetail(value);

    private static string BuildDetail(FrameRecord? record)
    {
        if (record is null) return "选中一行报文可查看详情与解析。";

        var text = $"序号：{record.Seq}    时间：{record.Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}    方向：{record.DirectionText}\n" +
                   $"原始报文：{record.Hex}\n" +
                   $"字节数：{record.RawBytes.Length}    从站/单元：{record.UnitIdText}    功能码：{record.FunctionText}\n" +
                   $"状态：{(string.IsNullOrEmpty(record.StatusText) ? "正常" : record.StatusText)}" +
                   (string.IsNullOrEmpty(record.MarkerText) ? string.Empty : $"    标记：{record.MarkerText}") + "\n" +
                   (record.ElapsedMs is { } ms ? $"响应耗时：{ms:F1} ms    " : string.Empty) +
                   (record.ErrorCode is { } code ? $"错误码：{code}" : string.Empty) + "\n";

        if (!string.IsNullOrEmpty(record.Summary)) text += $"解析：{record.Summary}\n";
        if (!string.IsNullOrEmpty(record.Detail)) text += $"说明：{record.Detail}\n";

        // 附上多寄存器解释，便于现场快速判断（解析开关关闭时不显示）
        if (record.RawBytes.Length > 4 && record.FunctionCode is Fn.ReadHoldingRegisters or Fn.ReadInputRegisters)
        {
            try
            {
                var pduStart = record.Direction == FrameDirection.Rx && record.RawBytes.Length > 7 && record.RawBytes[7] is 0x03 or 0x04
                    ? 8
                    : 3;
                var registers = new List<ushort>();
                for (int i = pduStart; i + 1 < record.RawBytes.Length - 2; i += 2)
                {
                    registers.Add((ushort)((record.RawBytes[i] << 8) | record.RawBytes[i + 1]));
                }

                if (registers.Count > 0)
                {
                    text += "寄存器原始值：" + string.Join(' ', registers.Select(r => $"0x{r:X4}")) + "\n";
                }
            }
            catch (Exception)
            {
                // 详情展示失败不影响主流程
            }
        }

        return text;
    }

    private void RefreshStatus()
    {
        StatusText = $"共采集 {Monitor.TotalCount} 条；当前显示 {Monitor.Rows.Count} 条" +
                     (Monitor.DroppedFromDisplay > 0 ? $"；显示缓冲已丢弃 {Monitor.DroppedFromDisplay} 条（落盘不受影响）" : string.Empty) +
                     (Monitor.IsRecording ? $"；正在记录到 {Monitor.RecordingFile}" : "；未记录到文件");
    }

    [RelayCommand]
    private void Clear()
    {
        Monitor.Clear();
        DetailText = "选中一行报文可查看详情与解析。";
        Message = "已清空显示缓冲（不影响已记录的文件）。";
    }

    [RelayCommand]
    private void ApplyFilters()
    {
        Monitor.ReapplyFilters();
        Message = "已按当前过滤条件重新提取显示内容。";
    }

    [RelayCommand]
    private void SaveText()
    {
        var path = _app.Dialogs.SaveFile("保存报文（文本）", "文本文件 (*.txt)|*.txt|日志文件 (*.log)|*.log",
            $"报文-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        if (string.IsNullOrEmpty(path)) return;
        var error = Monitor.Export(path, csv: false);
        if (string.IsNullOrEmpty(error)) _app.Dialogs.Info("保存完成", $"已保存 {Monitor.Rows.Count} 条报文到：\n{path}");
        else _app.Dialogs.Error("保存失败", error);
    }

    [RelayCommand]
    private void SaveCsv()
    {
        var path = _app.Dialogs.SaveFile("保存报文（CSV）", "CSV 文件 (*.csv)|*.csv",
            $"报文-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (string.IsNullOrEmpty(path)) return;
        var error = Monitor.Export(path, csv: true);
        if (string.IsNullOrEmpty(error)) _app.Dialogs.Info("保存完成", $"已保存 {Monitor.Rows.Count} 条报文到：\n{path}");
        else _app.Dialogs.Error("保存失败", error);
    }

    [RelayCommand]
    private void StartRecording()
    {
        var directory = _app.CaptureDirectory;
        var folder = _app.Dialogs.PickFolder("选择报文记录目录", directory);
        if (!string.IsNullOrEmpty(folder))
        {
            _app.Update(s => s with { LastCaptureDirectory = folder });
            directory = folder;
        }

        var error = Monitor.StartRecording(directory);
        Message = string.IsNullOrEmpty(error) ? $"已开始记录：{Monitor.RecordingFile}" : error;
        if (!string.IsNullOrEmpty(error)) _app.Dialogs.Error("记录失败", error);
    }

    [RelayCommand]
    private void StopRecording()
    {
        Monitor.StopRecording();
        Message = "已停止记录（文件为 JSONL 格式，可用文本编辑器或脚本分析）。";
    }

    [RelayCommand]
    private void CopySelectedHex()
    {
        if (SelectedRecord is null) return;
        try
        {
            System.Windows.Clipboard.SetText(SelectedRecord.Hex);
            Message = "已复制选中报文的十六进制内容。";
        }
        catch (Exception ex)
        {
            _app.Dialogs.Error("复制失败", ex.Message);
        }
    }

    /// <summary>把选中报文载入手动发送页面（写操作会再次确认）。</summary>
    [RelayCommand]
    private void SendSelectedToManual()
    {
        if (SelectedRecord is null) return;
        _app.Dialogs.ShowText("报文详情",
            BuildDetail(SelectedRecord) + "\n\n提示：可在“报文工具”页粘贴该报文再次发送；若为写操作会再次要求确认。");
    }
}
