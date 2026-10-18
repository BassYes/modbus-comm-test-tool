namespace Comm.Core.Frames;

/// <summary>把一条报文分发给多个观察者（落盘、界面、解析）。单个观察者异常不影响其他观察者。</summary>
public sealed class CompositeFrameObserver : IFrameObserver
{
    private readonly IFrameObserver[] _observers;

    public CompositeFrameObserver(params IFrameObserver[] observers)
    {
        _observers = observers;
    }

    public void OnFrame(FrameRecord record)
    {
        foreach (var observer in _observers)
        {
            try
            {
                observer.OnFrame(record);
            }
            catch (Exception)
            {
                // 单个消费者的异常不得影响通信路径（见规划 13.1 原则 3）。
            }
        }
    }
}
