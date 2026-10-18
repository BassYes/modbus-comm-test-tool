using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Comm.NetConfigHelper;

/// <summary>
/// 提权助手命名管道的访问控制（审计 F-02，规划 12.5 的"管道 ACL 只允许目标用户"）。
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item>显式构造 <see cref="PipeSecurity"/>，不再依赖进程默认 DACL——默认 DACL 会随启动方式变化，无法审计。</item>
/// <item>允许项固定为：提权后的当前用户（通常是同一账户的提权令牌）、SYSTEM、本机管理员组；
///       另外**只**把拉起助手的主程序所属用户 SID 加进来（处理"标准用户 + 另一个管理员账户提权"的场景）。</item>
/// <item>取不到主程序用户 SID 时退回"已认证用户可读写 + 强制 PID 校验"并记警告：宁可保持可用，
///       也不能让现场网卡功能因为一次 SID 查询失败而完全不可用；真正的身份判定仍由
///       <see cref="HelperServer"/> 的调用方进程标识校验承担（fail closed）。</item>
/// </list>
/// </para>
/// </summary>
internal static class HelperPipeSecurity
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    /// <summary>
    /// 构造管道安全描述符。<paramref name="note"/> 返回需要写进日志的降级说明（成功时为 null）。
    /// 返回 null 表示无法构造（非 Windows 或 API 不可用），调用方应退回默认管道创建方式。
    /// </summary>
    public static PipeSecurity? Create(int? expectedClientProcessId, out string? note)
    {
        note = null;
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            var security = new PipeSecurity();

            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                Allow(security, currentUser, PipeAccessRights.FullControl);
            }

            Allow(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl);
            Allow(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl);

            if (expectedClientProcessId is not null)
            {
                var clientSid = TryGetProcessOwnerSid(expectedClientProcessId.Value);
                if (clientSid is not null)
                {
                    if (currentUser is null || !clientSid.Equals(currentUser))
                    {
                        // 客户端账户与提权账户不同（例如标准用户 + 管理员账户提权）：只放行这一个 SID。
                        Allow(security, clientSid, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize);
                    }
                }
                else
                {
                    // 查询失败：退回"已认证用户可读写"，由 PID 校验兜底，并在日志里明确记录降级。
                    Allow(security, new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                        PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize);
                    note = "无法解析调用方用户 SID，管道 ACL 已退回“已认证用户可读写”（仍强制校验调用方 PID）";
                }
            }

            return security;
        }
        catch (Exception ex)
        {
            note = $"构造管道 ACL 失败（{ex.GetType().Name}: {ex.Message}），退回默认管道安全描述符（仍强制校验调用方 PID）";
            return null;
        }
    }

    /// <summary>以显式安全描述符创建管道服务端；ACL 构造失败时退回默认创建方式。</summary>
    public static NamedPipeServerStream CreateServer(string pipeName, int? expectedClientProcessId, out string? note)
    {
        if (!OperatingSystem.IsWindows())
        {
            note = "非 Windows 平台：使用默认管道安全描述符";
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        var security = Create(expectedClientProcessId, out note);
        if (security is null)
        {
            return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
        }

        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Allow(PipeSecurity security, SecurityIdentifier sid, PipeAccessRights rights) =>
        security.AddAccessRule(new PipeAccessRule(sid, rights, AccessControlType.Allow));

    /// <summary>取进程所属用户 SID（PROCESS_QUERY_LIMITED_INFORMATION + TOKEN_QUERY，失败返回 null）。</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SecurityIdentifier? TryGetProcessOwnerSid(int processId)
    {
        IntPtr processHandle = IntPtr.Zero;
        IntPtr tokenHandle = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (processHandle == IntPtr.Zero) return null;
            if (!OpenProcessToken(processHandle, TokenQuery, out tokenHandle)) return null;

            GetTokenInformation(tokenHandle, TokenUser, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return null;

            buffer = Marshal.AllocHGlobal(needed);
            if (!GetTokenInformation(tokenHandle, TokenUser, buffer, needed, out _)) return null;

            // TOKEN_USER 的第一个字段就是指向 SID 的指针。
            var sidPointer = Marshal.ReadIntPtr(buffer);
            return sidPointer == IntPtr.Zero ? null : new SecurityIdentifier(sidPointer);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (tokenHandle != IntPtr.Zero) CloseHandle(tokenHandle);
            if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation,
        int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
