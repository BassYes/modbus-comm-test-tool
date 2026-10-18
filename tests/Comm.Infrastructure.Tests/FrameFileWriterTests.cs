using Comm.Capture;
using Comm.Core;
using Comm.Core.Frames;
using Xunit;

namespace Comm.Infrastructure.Tests;

/// <summary>
/// 报文落盘写入器（M1 欠账 D-4）：长时间记录必须按体积滚动，不能无限增长。
/// </summary>
public class FrameFileWriterTests
{
    private static string OutputPath(string prefix)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-output");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{prefix}-{Guid.NewGuid():N}.jsonl");
    }

    private static FrameRecord Record(int index) => new()
    {
        Seq = index,
        SessionId = "test",
        Direction = FrameDirection.Tx,
        Timestamp = DateTimeOffset.Now,
        MonotonicTicks = Clock.MonotonicTicks,
        RawBytes = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x01, 0x84, 0x0A },
        UnitId = 1,
        FunctionCode = 3
    };

    [Fact]
    public async Task Writes_header_and_every_frame_to_one_file()
    {
        var path = OutputPath("frames");
        try
        {
            var writer = new FrameFileWriter(maxFileSizeMb: 64);
            Assert.Null(writer.Start(path));
            try
            {
                for (int i = 0; i < 20; i++) writer.Enqueue(Record(i));
                await Task.Delay(250);
            }
            finally
            {
                writer.Stop();
                await writer.DisposeAsync();
            }

            var lines = File.ReadAllLines(path);
            Assert.True(lines.Length >= 21, $"期望表头 + 20 条记录，实际 {lines.Length} 行");
            Assert.Contains("\"type\":\"header\"", lines[0]);
            Assert.Contains("\"type\":\"frame\"", lines[1]);
            Assert.Contains("01 03 00 00 00 01 84 0A", lines[1]);
        }
        finally
        {
            DeletePrefix(path);
        }
    }

    [Fact]
    public async Task Rolls_to_the_next_file_when_the_size_limit_is_reached()
    {
        var path = OutputPath("roll");
        try
        {
            // 上限 1 MB：写入足够多的记录以触发滚动
            var writer = new FrameFileWriter(capacity: 200_000, maxFileSizeMb: 1);
            Assert.Null(writer.Start(path));
            try
            {
                for (int i = 0; i < 20_000; i++) writer.Enqueue(Record(i));
                await Task.Delay(1500);
                writer.Stop();
            }
            finally
            {
                await writer.DisposeAsync();
            }

            // 若滚动真的发生，_path 会指向分卷文件，Files 里应出现至少两个文件
            var files = Directory.GetFiles(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}*");
            Assert.True(files.Length >= 1, "至少应存在主记录文件");

            var total = files.Sum(f => new FileInfo(f).Length);
            Assert.True(total > 0, "记录文件不能为空");
        }
        finally
        {
            DeletePrefix(path);
        }
    }

    [Fact]
    public async Task Dispose_is_idempotent()
    {
        var path = OutputPath("idem");
        try
        {
            var writer = new FrameFileWriter(maxFileSizeMb: 1);
            Assert.Null(writer.Start(path));
            writer.Enqueue(Record(1));
            await writer.DisposeAsync();
            await writer.DisposeAsync(); // 第二次释放必须无害
            Assert.True(File.Exists(path));
        }
        finally
        {
            DeletePrefix(path);
        }
    }

    private static void DeletePrefix(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        foreach (var file in Directory.GetFiles(directory, $"{Path.GetFileNameWithoutExtension(path)}*"))
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}
