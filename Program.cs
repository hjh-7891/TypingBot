using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TypingBot;

internal sealed class Options
{
    public const string DefaultGameUrl = "https://immae.top/2nd-Page/TypingEmoji/TypingEmoji.html";

    public string GameTarget { get; set; } = "";
    public int Port { get; set; } = 9222;
    public double Cps { get; set; } = 32;
    public bool Launch { get; set; } = true;
    public bool Headless { get; set; }
    public bool EdgeNoSandbox { get; set; }
    public bool CloseBrowser { get; set; }
    public bool AutoRestart { get; set; } = true;
    public string? EdgePath { get; set; }
    public string? ProfileDir { get; set; }
    public string? CatOverride { get; set; }
    public string Cat { get; set; } = "zh-elem";
    public double MaxRuntimeMinutes { get; set; }
    public int LaunchTimeoutSeconds { get; set; } = 30;

    public static Options Parse(string[] args)
    {
        var options = new Options();
        string? positional = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next()
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"参数 {arg} 缺少值。");
                return args[++i];
            }

            switch (arg)
            {
                case "-h":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                case "--port":
                    options.Port = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--cps":
                    options.Cps = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--profile":
                    options.ProfileDir = Next();
                    break;
                case "--edge":
                    options.EdgePath = Next();
                    break;
                case "--cat":
                    options.CatOverride = Next();
                    break;
                case "--max-runtime":
                    options.MaxRuntimeMinutes = double.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--launch-timeout":
                    options.LaunchTimeoutSeconds = int.Parse(Next(), CultureInfo.InvariantCulture);
                    break;
                case "--no-launch":
                    options.Launch = false;
                    break;
                case "--headless":
                    options.Headless = true;
                    break;
                case "--edge-no-sandbox":
                    options.EdgeNoSandbox = true;
                    break;
                case "--close-browser":
                    options.CloseBrowser = true;
                    break;
                case "--no-restart":
                    options.AutoRestart = false;
                    break;
                default:
                    if (arg.StartsWith('-'))
                        throw new ArgumentException($"未知参数：{arg}");
                    if (positional == null)
                        positional = arg;
                    else
                        throw new ArgumentException($"多余的位置参数：{arg}");
                    break;
            }
        }

        options.GameTarget = ResolveGameTarget(positional);
        if (options.Port is < 1 or > 65535)
            throw new ArgumentException("端口必须在 1–65535 之间。");
        if (options.Cps is < 1 or > 200)
            throw new ArgumentException("--cps 建议在 1–200 之间，避免把页面输入队列打爆。");
        if (options.LaunchTimeoutSeconds < 3)
            throw new ArgumentException("--launch-timeout 至少为 3 秒。");

        return options;
    }

    private static string ResolveGameTarget(string? provided)
    {
        if (!string.IsNullOrWhiteSpace(provided))
        {
            if (TryParseHttpUrl(provided, out var uri))
                return uri.AbsoluteUri;

            var full = Path.GetFullPath(provided);
            if (!File.Exists(full))
                throw new FileNotFoundException($"找不到游戏文件：{full}", full);
            return full;
        }

        // 不再依赖桌面上的本地副本，默认直接玩线上版本。
        return DefaultGameUrl;
    }

    public static bool TryParseHttpUrl(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) &&
            !string.IsNullOrWhiteSpace(parsed.Host))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    public static bool IsHttpUrl(string value) => TryParseHttpUrl(value, out _);

    public static string ToLaunchUrl(string gameTarget)
    {
        if (TryParseHttpUrl(gameTarget, out var uri))
            return uri.AbsoluteUri;
        return new Uri(Path.GetFullPath(gameTarget)).AbsoluteUri;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
            打字大战 emoji 自动打字机器人（C# + Edge DevTools Protocol）

            用法：
              TypingBot.exe [游戏.html 或 URL] [选项]

            第一个参数省略时，默认打开：
              https://immae.top/2nd-Page/TypingEmoji/TypingEmoji.html

            常用选项：
              --cps 32              每秒按键数，默认 32
              --port 9222           Edge 调试端口，默认 9222
              --headless            无界面运行（后台挂机）
              --no-launch           不启动浏览器，只连接已开调试端口的 Edge
              --no-restart          一局结束后不自动“再来一次”
              --close-browser       机器人退出时关闭它启动的 Edge
              --profile <目录>      指定 Edge 用户数据目录
              --edge <exe>          指定 msedge.exe 路径
              --cat zh-elem         强制词库（zh-elem/zh-mid/en-elem/en-mid/en-cet/custom）
              --max-runtime 30      最多运行多少分钟后退出
              --edge-no-sandbox     给 Edge 加 --no-sandbox（受限环境兜底，不推荐日常使用）
              -h, --help            显示本帮助

            例子：
              TypingBot.exe --cps 60
              TypingBot.exe "https://immae.top/2nd-Page/TypingEmoji/TypingEmoji.html" --headless
              TypingBot.exe "D:\Users\Desktop\打字游戏.html" --cps 36
            """);
    }
}

internal sealed class EdgeLauncher : IDisposable
{
    private readonly Process _process;
    private bool _disposed;

    public string ProfileDir { get; }

    private EdgeLauncher(Process process, string profileDir)
    {
        _process = process;
        ProfileDir = profileDir;
    }

    public int ProcessId
    {
        get
        {
            try { return _process.Id; }
            catch { return 0; }
        }
    }

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch { return true; }
        }
    }

    public static EdgeLauncher Start(Options options, string gameUrl)
    {
        var edge = FindEdge(options.EdgePath);
        var profile = ResolveProfile(options.ProfileDir, options.Port);
        Directory.CreateDirectory(profile);

        var args = new List<string>
        {
            $"--remote-debugging-port={options.Port}",
            "--remote-allow-origins=*",
            $"--user-data-dir={profile}",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-sync",
            "--disable-extensions",
            "--disable-default-apps",
            "--disable-component-update",
            "--disable-background-networking",
            "--disable-background-timer-throttling",
            "--disable-backgrounding-occluded-windows",
            "--disable-renderer-backgrounding",
            "--disable-popup-blocking",
            "--disable-breakpad",
            "--disable-features=msEdgeSidebarV2,msEdgeShoppingAssistant,msEdgeCollections,msEdgeWorkspaces,CalculateNativeWinOcclusion",
            "--autoplay-policy=no-user-gesture-required",
            "--window-size=1280,900"
        };

        if (options.Headless)
        {
            args.Add("--headless=new");
            args.Add("--disable-gpu");
        }
        if (options.EdgeNoSandbox)
            args.Add("--no-sandbox");

        // 放在 headless 之后，和手工验证可用的顺序一致。
        args.Add("--new-window");
        args.Add(gameUrl);

        var psi = new ProcessStartInfo
        {
            FileName = edge,
            // 通过 ShellExecute 交给资源管理器启动，避免 Edge 以本进程孙进程的身份
            // 继承沙箱/作业对象限制（否则 renderer 子进程起不来，页面永远空白）。
            UseShellExecute = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        Console.WriteLine($"启动 Edge：{edge}");
        Console.WriteLine($"用户数据目录：{profile}");

        var process = new Process { StartInfo = psi };
        var launcher = new EdgeLauncher(process, profile);
        process.Start();
        Console.WriteLine($"Edge 进程 PID：{launcher.ProcessId}");
        return launcher;
    }

    public string GetLogTail(int lines = 12)
    {
        return "（Edge 输出未被捕获；若进程已退出，请直接看控制台中的 Chromium 报错。）";
    }

    private static string ResolveProfile(string? requested, int port)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var full = Path.GetFullPath(requested);
            Directory.CreateDirectory(full);
            return full;
        }

        var candidates = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            candidates.Add(Path.Combine(local, "TypingBot", $"edge-profile-{port}"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "TypingBot", $"edge-profile-{port}"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "edge-profile"));

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch
            {
                // 当前目录不可写，换下一个兜底。
            }
        }

        throw new IOException("找不到可写的 Edge 用户数据目录，请用 --profile 指定。");
    }

    public static string FindEdge(string? requested)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(requested))
            candidates.Add(requested);
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EDGE_PATH")))
            candidates.Add(Environment.GetEnvironmentVariable("EDGE_PATH")!);

        candidates.Add(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe");
        candidates.Add(@"C:\Program Files\Microsoft\Edge\Application\msedge.exe");

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            candidates.Add(Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe"));

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { /* ignore */ }
        }

        throw new FileNotFoundException(
            "找不到 Microsoft Edge（msedge.exe）。请用 --edge 指定路径，或设置环境变量 EDGE_PATH。");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch { /* ignore */ }

        _process.Dispose();
    }
}

internal sealed class PageSettings
{
    public string Cat { get; set; } = "zh-elem";
    public string Custom { get; set; } = "";
}

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "打字大战 emoji 自动打字机器人";

        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("参数错误：" + ex.Message);
            Options.PrintHelp();
            return 2;
        }

        Console.WriteLine("==============================================");
        Console.WriteLine("  打字大战 emoji · 自动打字机器人（C# / CDP）");
        Console.WriteLine("==============================================");
        Console.WriteLine($"游戏地址：{options.GameTarget}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\n收到 Ctrl+C，正在停止…");
            cts.Cancel();
        };

        if (options.MaxRuntimeMinutes > 0)
            cts.CancelAfter(TimeSpan.FromMinutes(options.MaxRuntimeMinutes));

        var gameUrl = Options.ToLaunchUrl(options.GameTarget);
        EdgeLauncher? launcher = null;

        try
        {
            CdpTarget? target = null;
            if (options.Launch)
            {
                target = await CdpDiscovery.FindGameTargetAsync(options.Port, options.GameTarget, cts.Token);
                if (target == null)
                {
                    launcher = EdgeLauncher.Start(options, gameUrl);

                    // 有的宿主/沙箱环境里 Chromium 的 renderer 子进程起不来，
                    // 页面会永远空白。先给普通启动 8 秒，再自动用 --no-sandbox 重试。
                    var probeSeconds = Math.Min(8, options.LaunchTimeoutSeconds);
                    try
                    {
                        target = await CdpDiscovery.WaitForGameTargetAsync(
                            options.Port,
                            options.GameTarget,
                            TimeSpan.FromSeconds(probeSeconds),
                            cts.Token);
                    }
                    catch (TimeoutException)
                    {
                        target = null;
                    }

                    if (target == null && !options.EdgeNoSandbox)
                    {
                        Console.WriteLine("页面未加载（renderer 可能被沙箱阻止），改用 --no-sandbox 重启 Edge…");
                        launcher.Dispose();
                        launcher = null;
                        await Task.Delay(800, cts.Token);

                        options.EdgeNoSandbox = true;
                        launcher = EdgeLauncher.Start(options, gameUrl);
                    }
                }
            }

            if (target == null)
            {
                target = await CdpDiscovery.WaitForGameTargetAsync(
                    options.Port,
                    options.GameTarget,
                    TimeSpan.FromSeconds(options.LaunchTimeoutSeconds),
                    cts.Token);
            }

            Console.WriteLine($"已找到页面：{target.Title}");
            Console.WriteLine($"页面地址：{target.Url}");
            Console.WriteLine($"调试地址：{target.WebSocketDebuggerUrl}");

            // 标题出现代表导航已提交；再给页面脚本一点时间建立好执行上下文。
            await Task.Delay(500, cts.Token);

            await using var cdp = await CdpClient.ConnectAsync(target.WebSocketDebuggerUrl, cts.Token);
            await cdp.BringToFrontAsync(cts.Token);
            await Task.Delay(400, cts.Token);

            var pageSettings = await ReadPageSettingsAsync(cdp, cts.Token);
            options.Cat = options.CatOverride ?? pageSettings.Cat;

            var bank = await WordBank.LoadAsync(
                options.GameTarget, options.Cat, pageSettings.Custom, cts.Token);
            var bot = new GameBot(cdp, bank, options);
            await bot.RunAsync(cts.Token);

            Console.WriteLine("机器人已停止。");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("机器人已停止。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("启动/运行失败：" + ex.Message);
            if (launcher != null && launcher.HasExited)
            {
                var tail = launcher.GetLogTail();
                if (!string.IsNullOrWhiteSpace(tail))
                {
                    Console.Error.WriteLine("Edge 输出（末尾）：");
                    Console.Error.WriteLine(tail);
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine("排查建议：");
                Console.Error.WriteLine("  1. 换一个端口：--port 9223");
                Console.Error.WriteLine("  2. 指定可写的用户目录：--profile %TEMP%\\TypingBotProfile");
                Console.Error.WriteLine("  3. 若提示 sandbox / 拒绝访问：加 --edge-no-sandbox");
                Console.Error.WriteLine("  4. 若 Edge 已用同一 profile 运行，先完全退出 Edge 再试");
            }
            return 1;
        }
        finally
        {
            if (launcher != null)
            {
                if (options.CloseBrowser)
                    launcher.Dispose();
                else
                    Console.WriteLine("（浏览器保持打开；需要自动关闭请加 --close-browser）");
            }
        }
    }

    private static async Task<PageSettings> ReadPageSettingsAsync(
        CdpClient cdp, CancellationToken ct)
    {
        const string expression = """
            (() => {
              try {
                const s = JSON.parse(localStorage.getItem('typingEmojiBattle_v2') || '{}');
                return {
                  cat: s.cat || 'zh-elem',
                  custom: localStorage.getItem('TE_CUSTOM_BANK_V1') || ''
                };
              } catch (e) {
                return { cat: 'zh-elem', custom: '' };
              }
            })()
            """;

        var value = await cdp.EvaluateAsync(expression, ct);
        if (value is not { } element)
            return new PageSettings();

        try
        {
            return JsonSerializer.Deserialize<PageSettings>(
                       element.GetRawText(),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new PageSettings();
        }
        catch
        {
            return new PageSettings();
        }
    }
}
