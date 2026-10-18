using Comm.App.Wpf.ViewModels;
using Comm.Core;
using Comm.Core.Errors;
using Comm.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Comm.App.Wpf.Services;

/// <summary>
/// 会话工作区注册表（M3b R-16，规划 21.2）。
/// 维护"会话 ↔ 工作区（页面集合）"的一一映射：会话建立时创建工作区，会话关闭时释放工作区。
/// 被拆分到独立窗口的工作区仍然属于本注册表，窗口只负责显示（见 <see cref="SessionWindowManager"/>）。
/// </summary>
public sealed class SessionWorkspaces : ObservableObject, IDisposable
{
    private readonly AppServices _app;
    private readonly List<SessionWorkspaceViewModel> _workspaces = new();
    private readonly Dictionary<string, SessionWindowLayout> _pendingLayouts = new();
    private bool _disposed;

    public SessionWorkspaces(AppServices app)
    {
        _app = app;
        _app.Sessions.SessionsChanged += (_, _) => Sync();
        Sync();
    }

    /// <summary>按会话创建顺序排列的工作区。</summary>
    public IReadOnlyList<SessionWorkspaceViewModel> Workspaces => _workspaces;

    /// <summary>工作区增删（会话新建/关闭）时触发。</summary>
    public event EventHandler? Changed;

    public SessionWorkspaceViewModel? Find(string sessionId) =>
        _workspaces.FirstOrDefault(w => w.SessionId == sessionId);

    /// <summary>新建会话（同时建好工作区）。达到上限时返回失败并说明原因。</summary>
    public Result<SessionWorkspaceViewModel> CreateSession(string? name = null)
    {
        var created = _app.Sessions.CreateSession(name);
        if (!created.IsSuccess)
        {
            return Result<SessionWorkspaceViewModel>.Fail(created.Error!);
        }

        Sync();
        var workspace = Find(created.Value!.SessionId);
        return workspace is null
            ? Result<SessionWorkspaceViewModel>.Fail(new ErrorInfo(ErrorCodes.InternalError, ErrorCategory.Internal,
                Severity.Error, "会话已创建但工作区未能建立"))
            : Result<SessionWorkspaceViewModel>.Ok(workspace);
    }

    /// <summary>关闭会话（同时释放工作区）。最后一个会话不允许关闭（规划 21.2.2：至少保留一个）。</summary>
    public Result<string> CloseSession(string sessionId)
    {
        var result = _app.Sessions.CloseSession(sessionId);
        if (result.IsSuccess) Sync();
        return result;
    }

    /// <summary>把会话注册表与会话列表对齐（新建补建、关闭释放）。</summary>
    private void Sync()
    {
        bool changed = false;

        foreach (var session in _app.Sessions.Sessions)
        {
            if (_workspaces.Any(w => w.SessionId == session.SessionId)) continue;
            _workspaces.Add(new SessionWorkspaceViewModel(_app, session));
            changed = true;
        }

        var live = _app.Sessions.Sessions.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
        for (int i = _workspaces.Count - 1; i >= 0; i--)
        {
            if (live.Contains(_workspaces[i].SessionId)) continue;
            _workspaces[i].Dispose();
            _workspaces.RemoveAt(i);
            changed = true;
        }

        // 会话顺序跟随注册表（按创建顺序）。
        var ordered = _app.Sessions.Sessions
            .Select(s => _workspaces.FirstOrDefault(w => w.SessionId == s.SessionId))
            .Where(w => w is not null)
            .Select(w => w!)
            .ToList();

        if (!ordered.SequenceEqual(_workspaces))
        {
            _workspaces.Clear();
            _workspaces.AddRange(ordered);
            changed = true;
        }

        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ 窗口布局持久化（规划 21.2.2 第 5 点）

    /// <summary>读取某个会话上次的窗口位置/尺寸（没有记录时返回 null）。</summary>
    public SessionWindowLayout? GetLayout(string sessionId) =>
        _pendingLayouts.TryGetValue(sessionId, out var pending)
            ? pending
            : _app.Settings.Sessions.WindowLayouts.FirstOrDefault(l => l.SessionId == sessionId);

    /// <summary>记住本次运行期间的布局（尚未落盘时也有值，避免拆分/移回丢失）。</summary>
    public void RememberLayout(SessionWindowLayout layout) => _pendingLayouts[layout.SessionId] = layout;

    /// <summary>把布局写入设置（拆分窗口关闭或移动时调用）。</summary>
    public void SaveLayout(SessionWindowLayout layout)
    {
        RememberLayout(layout);
        var layouts = _app.Settings.Sessions.WindowLayouts
            .Where(l => l.SessionId != layout.SessionId)
            .ToList();
        layouts.Add(layout);
        _app.Update(s => s with { Sessions = s.Sessions with { WindowLayouts = layouts } });
    }

    /// <summary>清除某个会话的窗口布局（会话关闭时调用）。</summary>
    public void ForgetLayout(string sessionId)
    {
        _pendingLayouts.Remove(sessionId);
        var layouts = _app.Settings.Sessions.WindowLayouts.Where(l => l.SessionId != sessionId).ToList();
        if (layouts.Count == _app.Settings.Sessions.WindowLayouts.Count) return;
        _app.Update(s => s with { Sessions = s.Sessions with { WindowLayouts = layouts } });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var workspace in _workspaces) workspace.Dispose();
        _workspaces.Clear();
    }
}
