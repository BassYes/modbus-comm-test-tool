using System.IO.Pipes;
using Comm.Platform.Windows;

namespace Comm.NetConfigHelper.Tests.Fakes;

/// <summary>
/// 测试用的管道客户端：一条连接发一条请求、读一条响应。
/// 服务端是"逐个创建管道实例"的循环，因此连接需要重试（连不上就等 20 ms 再来）。
/// </summary>
internal static class HelperPipeClient
{
    public static HelperResponse Send(string pipeName, HelperRequest request, int timeoutMs = 5000) =>
        SendRaw(pipeName, HelperProtocol.Serialize(request), timeoutMs);

    /// <summary>发送任意原始文本（用于验证畸形 JSON 的 PLT-5008 路径）。</summary>
    public static HelperResponse SendRaw(string pipeName, string rawJson, int timeoutMs = 5000)
    {
        using var client = ConnectWithRetry(pipeName, timeoutMs);
        HelperProtocol.WriteMessage(client, rawJson);

        var responseJson = HelperProtocol.ReadMessage(client);
        if (responseJson is null) throw new InvalidOperationException("助手没有返回响应（管道提前关闭）");

        var response = HelperProtocol.ParseResponse(responseJson);
        if (response is null) throw new InvalidOperationException($"助手响应无法解析：{responseJson}");
        return response;
    }

    /// <summary>只建立连接再立即断开（用于验证"连接但没发请求"的行为）。</summary>
    public static void ConnectAndClose(string pipeName, int timeoutMs = 5000)
    {
        using var client = ConnectWithRetry(pipeName, timeoutMs);
    }

    private static NamedPipeClientStream ConnectWithRetry(string pipeName, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                client.Connect(150);
                return client;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                last = ex;
                client.Dispose();
                Thread.Sleep(20);
            }
        }

        throw new TimeoutException($"连接助手管道 {pipeName} 超时（{timeoutMs} ms）：{last?.Message}");
    }
}
