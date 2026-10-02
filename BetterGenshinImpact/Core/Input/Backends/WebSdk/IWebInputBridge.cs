namespace BetterGenshinImpact.Core.Input.Backends.WebSdk;

/// <summary>
/// 云原神网页版 JS SDK（Resources/JavaScript/ys-input-inject.js，编译为嵌入资源）的通用调用接口
/// </summary>
public interface IWebInputBridge
{
    /// <summary>
    /// 调用页面 SDK <c>window.__ysInputInject[method](...args)</c>。
    /// 不等待执行结果，页面侧按到达顺序串行执行；失败由实现类记录日志。
    /// 可选参数请直接省略，不要传 null：JSON 的 null 不会触发 JS 的参数默认值。
    /// </summary>
    void Invoke(string method, params object[] args);

    /// <summary>任务线程使用的有界确认调用。准入在实际投递前执行，页面完成SDK调用后才返回。
    /// 不表示游戏已经响应；调用方仍需输入后的源帧确认。</summary>
    void InvokeAcknowledged(string method, System.Action beforePost, params object[] args)
        => throw new System.NotSupportedException("当前SDK桥不支持有界投递确认");
}
