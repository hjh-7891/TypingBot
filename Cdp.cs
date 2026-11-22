using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TypingBot;

internal sealed class CdpTarget
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string WebSocketDebuggerUrl { get; set; } = "";
}

internal static class CdpDiscovery
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    public static async Task<CdpTarget?> FindGameTargetAsync(int port, string gameTarget, CancellationToken ct)
    {
        try
        {
            var json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var isHttp = Options.TryParseHttpUrl(gameTarget, out var expectedUri);
            var fileName = Path.GetFileName(
                isHttp ? expectedUri.AbsolutePath : gameTarget);
            var expectedPath = isHttp
                ? Uri.UnescapeDataString(expectedUri.AbsolutePath).TrimEnd('/')
                : "";
            CdpTarget? best = null;
            var bestScore = int.MinValue;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (!string.Equals(type, "page", StringComparison.OrdinalIgnoreCase))
                    continue;

                var target = new CdpTarget
                {
                    Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Title = item.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                    Url = item.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
                    WebSocketDebuggerUrl = item.TryGetProperty("webSocketDebuggerUrl", out var ws)
                        ? ws.GetString() ?? ""
                        : ""
                };

                if (string.IsNullOrWhiteSpace(target.WebSocketDebuggerUrl))
                    continue;

                var decodedUrl = target.Url;
                try { decodedUrl = Uri.UnescapeDataString(decodedUrl); } catch { /* ignore */ }

                var score = 0;
                if (isHttp &&
                    Options.TryParseHttpUrl(target.Url, out var targetUri) &&
                    string.Equals(targetUri.Host, expectedUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    score += 40;
                    var actualPath = Uri.UnescapeDataString(targetUri.AbsolutePath).TrimEnd('/');
                    if (string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                        score += 120;
                    else if (actualPath.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase))
                        score += 40;
                }
                else if (decodedUrl.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                }

                if (target.Title.Contains("打字", StringComparison.OrdinalIgnoreCase))
                    score += 60;
                if (decodedUrl.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    score += 10;
                if (target.Title.Contains("DevTools", StringComparison.OrdinalIgnoreCase))
                    score -= 100;
                if (decodedUrl.StartsWith("devtools:", StringComparison.OrdinalIgnoreCase))
                    score -= 100;
                // 新建标签页在导航提交前标题为空。此时连上去，执行上下文会在导航时被替换，
                // Runtime.evaluate 可能永远不返回；必须等标题出现。
                if (string.IsNullOrWhiteSpace(target.Title))
                    score -= 200;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = target;
                }
            }

            // 端口可能属于另一个调试实例，或者游戏页还没加载完。
            // 只有 URL/标题确实像目标页面时才返回，避免连到 about:blank 后白等。
            return bestScore >= 50 ? best : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<CdpTarget> WaitForGameTargetAsync(
        int port, string gameTarget, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            var target = await FindGameTargetAsync(port, gameTarget, ct);
            if (target != null)
                return target;
            await Task.Delay(250, ct);
        }

        throw new TimeoutException(
            $"等待 Edge 调试端口 {port} 超时。请确认浏览器已启动，或用 --port 指定正确端口。");
    }
}

internal sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private long _nextId;
    private volatile bool _closed;

    private CdpClient() { }

    public static async Task<CdpClient> ConnectAsync(string webSocketUrl, CancellationToken ct)
    {
        var client = new CdpClient();
        client._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        await client._ws.ConnectAsync(new Uri(webSocketUrl), ct);
        client._receiveLoop = Task.Run(() => client.ReceiveLoopAsync(client._cts.Token));
        return client;
    }

    public async Task<JsonElement?> EvaluateAsync(string expression, CancellationToken ct)
    {
        var result = await SendAsync("Runtime.evaluate", new Dictionary<string, object?>
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = true,
            ["userGesture"] = true
        }, ct);

        if (result.TryGetProperty("exceptionDetails", out var ex))
        {
            var message = "页面脚本执行失败";
            if (ex.TryGetProperty("exception", out var exception) &&
                exception.TryGetProperty("description", out var desc))
                message = desc.GetString() ?? message;
            else if (ex.TryGetProperty("text", out var text))
                message = text.GetString() ?? message;
            throw new InvalidOperationException(message);
        }

        if (!result.TryGetProperty("result", out var remote))
            return null;
        if (!remote.TryGetProperty("value", out var value))
            return null;
        return value.Clone();
    }

    /// <summary>
    /// 把游戏标签页切到前台。后台标签页会被浏览器暂停 rAF，游戏本身也会在
    /// visibilitychange 时自动暂停；聚焦后游戏才能继续推进。
    /// </summary>
    public async Task BringToFrontAsync(CancellationToken ct)
    {
        _ = await SendAsync("Page.bringToFront", new Dictionary<string, object?>(), ct);
    }

    public async Task<bool> ClickAsync(string elementId, CancellationToken ct)
    {        var idJson = JsonSerializer.Serialize(elementId);
        var expression =
            "(()=>{const e=document.getElementById(" + idJson + ");" +
            "if(!e)return false;e.click();return true;})()";
        var value = await EvaluateAsync(expression, ct);
        return value is { ValueKind: JsonValueKind.True };
    }

    public async Task PressKeyAsync(char ch, CancellationToken ct)
    {
        await PressKeysAsync(ch.ToString(), ct);
    }

    /// <summary>
    /// 一次 CDP 往返里派发整串按键，避免每个字符都读一次 DOM。
    /// 游戏监听的是 document keydown，同步派发即可逐个处理。
    /// </summary>
    public async Task PressKeysAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var keys = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var lower = char.ToLowerInvariant(raw);
            if (lower < 'a' || lower > 'z')
                throw new ArgumentOutOfRangeException(nameof(text), raw, "游戏只接受 a-z。");
            keys.Append(lower);
        }

        var keysJson = JsonSerializer.Serialize(keys.ToString());
        var expression =
            "(()=>{const keys=" + keysJson + ";" +
            "for(const k of keys){" +
            "const code='Key'+k.toUpperCase();" +
            "const keyCode=k.toUpperCase().charCodeAt(0);" +
            "const e=new KeyboardEvent('keydown',{key:k,code:code,keyCode:keyCode,which:keyCode," +
            "bubbles:true,cancelable:true,composed:true});" +
            "document.dispatchEvent(e);}" +
            "return keys.length;})()";
        _ = await EvaluateAsync(expression, ct);
    }

    public async Task PressBackspaceAsync(CancellationToken ct)
    {
        const string expression =
            "(()=>{const e=new KeyboardEvent('keydown',{key:'Backspace',code:'Backspace'," +
            "keyCode:8,which:8,bubbles:true,cancelable:true,composed:true});" +
            "document.dispatchEvent(e);return true;})()";
        _ = await EvaluateAsync(expression, ct);
    }

    public async Task PressEscapeAsync(CancellationToken ct)
    {
        const string expression =
            "(()=>{const e=new KeyboardEvent('keydown',{key:'Escape',code:'Escape'," +
            "keyCode:27,which:27,bubbles:true,cancelable:true,composed:true});" +
            "document.dispatchEvent(e);return true;})()";
        _ = await EvaluateAsync(expression, ct);
    }

    private async Task<JsonElement> SendAsync(
        string method, object? parameters, CancellationToken ct)
    {
        if (_closed)
            throw new InvalidOperationException("CDP 连接已关闭。");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        });
        var bytes = Encoding.UTF8.GetBytes(payload);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            await _sendLock.WaitAsync(timeout.Token);
            try
            {
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token);
            }
            finally
            {
                _sendLock.Release();
            }

            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"CDP 调用 {method} 超过 15 秒没有响应。");
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new WebSocketException("浏览器关闭了调试连接。");
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                using var doc = JsonDocument.Parse(message.ToArray());
                if (!doc.RootElement.TryGetProperty("id", out var idElement) ||
                    !idElement.TryGetInt64(out var id))
                    continue;

                if (!_pending.TryRemove(id, out var tcs))
                    continue;

                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    var text = error.TryGetProperty("message", out var m)
                        ? m.GetString()
                        : error.GetRawText();
                    tcs.TrySetException(new InvalidOperationException("CDP 错误：" + text));
                }
                else if (doc.RootElement.TryGetProperty("result", out var res))
                {
                    tcs.TrySetResult(res.Clone());
                }
                else
                {
                    tcs.TrySetResult(default);
                }
            }
        }
        catch (Exception ex)
        {
            _closed = true;
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var tcs))
                    tcs.TrySetException(ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _closed = true;
        _cts.Cancel();

        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeTimeout.Token);
            }
        }
        catch { /* ignore */ }

        if (_receiveLoop != null)
        {
            try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { /* ignore */ }
        }

        _ws.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }
}
