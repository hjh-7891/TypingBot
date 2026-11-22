using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace TypingBot;

internal sealed class EnemyInfo
{
    public int Index { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public string Word { get; set; } = "";
    public string Py { get; set; } = "";
    public string Done { get; set; } = "";
    public string Todo { get; set; } = "";
    public bool Matching { get; set; }
    public bool Locked { get; set; }
    public bool Dead { get; set; }
}

internal sealed class BossInfo
{
    public string Head { get; set; } = "";
    public string Done { get; set; } = "";
    public string Todo { get; set; } = "";
    public string Timer { get; set; } = "";
    public string Fill { get; set; } = "";
    public string Ghost { get; set; } = "";
    public string Count { get; set; } = "";
}

internal sealed class CardInfo
{
    public int Index { get; set; }
    public string Word { get; set; } = "";
    public string Py { get; set; } = "";
    public string Name { get; set; } = "";
}

internal sealed class GameSnapshot
{
    public string Title { get; set; } = "";
    public string Href { get; set; } = "";
    public bool OverlayVisible { get; set; }
    public string OverlayText { get; set; } = "";
    public bool Start { get; set; }
    public bool Restart { get; set; }
    public bool Resume { get; set; }
    public bool Cancel { get; set; }
    public bool Reward { get; set; }
    public string RewardTyped { get; set; } = "";
    public List<CardInfo> Cards { get; set; } = new();
    public string Typed { get; set; } = "";
    public List<EnemyInfo> Enemies { get; set; } = new();
    public BossInfo? Boss { get; set; }
    public string Score { get; set; } = "";
    public string Wave { get; set; } = "";
    public string Hp { get; set; } = "";
    public string Combo { get; set; } = "";
    public bool Hidden { get; set; }
    public string Visibility { get; set; } = "";
    public bool HasFocus { get; set; }
    public int ViewportWidth { get; set; }
}

internal sealed class GameBot
{
    private const string SnapshotExpression = """
        (() => {
          const $ = id => document.getElementById(id);
          const txt = el => el ? (el.textContent || '') : '';
          const val = id => { const el = $(id); return el ? (el.innerText || el.textContent || '') : ''; };
          const overlay = $('overlay');
          const overlayVisible = !!(overlay && !overlay.classList.contains('hidden'));
          // 这些控件始终留在 DOM 里，只是被隐藏；必须按实际尺寸判断，
          // 否则会把「强化挑战」永久当成进行中，或点到上一块面板的隐藏按钮。
          const shown = id => {
            const el = $(id);
            if (!el) return false;
            const r = el.getBoundingClientRect();
            return r.width > 0 && r.height > 0;
          };
          const typedEl = $('typed');
          const typed = (typedEl && !typedEl.classList.contains('empty'))
            ? (typedEl.innerText || typedEl.textContent || '')
            : '';

          const enemies = [];
          document.querySelectorAll('.enemy').forEach((el, i) => {
            const r = el.getBoundingClientRect();
            const wEl = el.querySelector('.word');
            let word = '';
            if (wEl) {
              const clone = wEl.cloneNode(true);
              clone.querySelectorAll('.rep-tag').forEach(n => n.remove());
              word = (clone.textContent || '').trim();
            }
            const pyEl = el.querySelector('.py');
            enemies.push({
              index: i,
              x: r.left + r.width / 2,
              y: r.top + r.height / 2,
              width: r.width,
              word: word,
              py: txt(pyEl),
              done: txt(el.querySelector('.py .done')),
              todo: txt(el.querySelector('.py .todo')),
              matching: el.classList.contains('matching'),
              locked: el.classList.contains('locked'),
              // 命中后 locked 会立刻移除，但 DOM 还会淡出 320ms；这段同样是尸体。
              dead: el.style.opacity === '0' || getComputedStyle(el).opacity === '0'
            });
          });

          let boss = null;
          const bar = $('boss-bar');
          if (bar) {
            const fill = $('bb-timer-fill');
            boss = {
              head: txt(bar.querySelector('.bb-head')),
              done: txt($('bb-done')),
              todo: txt($('bb-todo')),
              timer: txt($('bb-timer')),
              fill: fill ? (fill.style.width || '') : '',
              ghost: txt($('bb-ghost')),
              count: txt($('bb-count'))
            };
          }

          const cards = [];
          document.querySelectorAll('.card').forEach((c, i) => {
            const pyEl = c.querySelector('.cw-py');
            if (!pyEl) return;
            cards.push({
              index: i,
              word: txt(c.querySelector('.cw-word')),
              py: txt(pyEl),
              name: txt(c.querySelector('.c-name'))
            });
          });

          return {
            title: document.title,
            href: location.href,
            overlayVisible: overlayVisible,
            overlayText: overlayVisible ? ((overlay.innerText || '').slice(0, 800)) : '',
            start: shown('btn-start'),
            restart: shown('btn-restart'),
            resume: shown('btn-resume'),
            cancel: shown('btn-cfm-cancel'),
            reward: shown('reward-input'),
            rewardTyped: (() => {
              const e = $('reward-typed');
              return (e && !e.classList.contains('empty')) ? (e.innerText || '') : '';
            })(),
            cards: cards,
            typed: typed,
            enemies: enemies,
            boss: boss,
            score: val('score'),
            wave: val('wave'),
            hp: val('hp-num'),
            combo: val('combo'),
            hidden: !!document.hidden,
            visibility: document.visibilityState || '',
            hasFocus: !!document.hasFocus(),
            viewportWidth: window.innerWidth || 1200
          };
        })()
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CdpClient _cdp;
    private readonly WordBank _bank;
    private readonly Options _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly bool _trace = Environment.GetEnvironmentVariable("TYPINGBOT_TRACE") == "1";
    private long _keys;
    private long _lastReportTick;
    private bool _reportedStart;
    private readonly HashSet<string> _unresolved = new(StringComparer.Ordinal);
    private string _stallScore = "";
    private long _stallSince;
    private long _lastNudgeTick = -10000;

    public GameBot(CdpClient cdp, WordBank bank, Options options)
    {
        _cdp = cdp;
        _bank = bank;
        _options = options;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"词库：{_options.Cat}，已载入 {_bank.Count} 条词条。");
        Console.WriteLine($"目标速度：{_options.Cps:0.#} 键/秒；自动重开：{(_options.AutoRestart ? "是" : "否")}");
        Console.WriteLine("机器人已启动，按 Ctrl+C 停止。");

        var consecutiveErrors = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snapshot = await TakeSnapshotAsync(ct);
                if (snapshot == null)
                {
                    await Task.Delay(250, ct);
                    continue;
                }

                if (snapshot.Reward)
                {
                    await HandleRewardAsync(snapshot, ct);
                    Report(snapshot);
                    consecutiveErrors = 0;
                    continue;
                }

                if (snapshot.OverlayVisible)
                {
                    if (!await HandleOverlayAsync(snapshot, ct))
                        return;
                    consecutiveErrors = 0;
                    continue;
                }

                await NudgeIfStalledAsync(snapshot, ct);

                if (!_reportedStart)
                {
                    _reportedStart = true;
                    Console.WriteLine("已进入游戏，开始自动输入。");
                }

                await ActAsync(snapshot, ct);
                Report(snapshot);
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                Console.Error.WriteLine($"[CDP] {ex.Message}");
                if (consecutiveErrors >= 8)
                    throw;
                await Task.Delay(500, ct);
            }
        }
    }

    private async Task<GameSnapshot?> TakeSnapshotAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var value = await _cdp.EvaluateAsync(SnapshotExpression, ct);
        var snapshot = value is { } element
            ? JsonSerializer.Deserialize<GameSnapshot>(element.GetRawText(), JsonOptions)
            : null;
        if (_trace)
            Console.Error.WriteLine($"[trace] snapshot {sw.ElapsedMilliseconds}ms enemies={snapshot?.Enemies.Count} typed='{snapshot?.Typed}'");
        return snapshot;
    }

    /// <summary>
    /// Windows 会把被遮住的窗口判定为 occluded，Chrome 可能因此暂停 rAF，
    /// 表现为「页面可见、有焦点、但没有敌人也没有分数变化」。
    /// 即使已经加了禁用后台节流的启动参数，也再用一次 bringToFront 兜底。
    /// </summary>
    private async Task NudgeIfStalledAsync(GameSnapshot snapshot, CancellationToken ct)
    {
        var now = _clock.ElapsedMilliseconds;
        if (!string.Equals(snapshot.Score, _stallScore, StringComparison.Ordinal))
        {
            _stallScore = snapshot.Score;
            _stallSince = now;
            return;
        }

        if (now - _stallSince < 5000 || now - _lastNudgeTick < 6000)
            return;

        _lastNudgeTick = now;
        Console.WriteLine("画面 5 秒没有进展，重新激活游戏页…");
        await _cdp.BringToFrontAsync(ct);
        await Task.Delay(300, ct);
    }

    /// <summary>返回 false 表示机器人应退出（例如关闭了自动重开）。</summary>
    private async Task<bool> HandleOverlayAsync(GameSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Cancel)
        {
            await _cdp.ClickAsync("btn-cfm-cancel", ct);
            Console.WriteLine("已取消确认框，继续当前局。");
            await Task.Delay(250, ct);
            return true;
        }

        if (snapshot.Resume)
        {
            await _cdp.ClickAsync("btn-resume", ct);
            Console.WriteLine("已点击“继续游戏”。");
            await Task.Delay(300, ct);
            return true;
        }

        if (snapshot.Start)
        {
            await _cdp.ClickAsync("btn-start", ct);
            Console.WriteLine("已点击“开始游戏”。");
            await Task.Delay(500, ct);
            return true;
        }

        if (snapshot.Restart)
        {
            if (!_options.AutoRestart)
            {
                Console.WriteLine("本局结束（已禁用自动重开）。");
                return false;
            }
            await _cdp.ClickAsync("btn-restart", ct);
            Console.WriteLine("本局结束，已点击“再来一次”。");
            await Task.Delay(700, ct);
            return true;
        }

        // 机器人一般不会打开设置/帮助/排行榜；若误入这些界面，Esc 通常能退回。
        var text = snapshot.OverlayText.Replace('\n', ' ').Trim();
        if (text.Length > 120)
            text = text[..120] + "…";
        Console.WriteLine($"检测到未知面板：{text}");
        await _cdp.PressEscapeAsync(ct);
        await Task.Delay(400, ct);
        return true;
    }

    private async Task HandleRewardAsync(GameSnapshot snapshot, CancellationToken ct)
    {
        var cards = snapshot.Cards
            .Where(c => IsTypable(c.Py))
            .OrderBy(c => c.Py.Length)
            .ThenBy(c => c.Index)
            .ToList();
        if (cards.Count == 0)
        {
            await Task.Delay(120, ct);
            return;
        }

        var typed = (snapshot.RewardTyped ?? "").Trim();
        if (typed.Length > 0)
        {
            var matching = cards
                .Where(c => c.Py.StartsWith(typed, StringComparison.Ordinal))
                .ToList();
            if (matching.Count > 0)
            {
                var card = matching[0];
                if (typed.Length < card.Py.Length)
                    await TypeTextAsync(card.Py[typed.Length..], ct);
                else
                    await BackspaceAsync(ct);
            }
            else
            {
                await BackspaceAsync(ct);
            }
            return;
        }

        var chosen = cards[0];
        await TypeTextAsync(chosen.Py, ct);
    }

    private async Task ActAsync(GameSnapshot snapshot, CancellationToken ct)
    {
        var targets = new List<EnemyTarget>();
        foreach (var enemy in snapshot.Enemies)
        {
            // 已发射子弹的敌人会被打上 locked 并从 S.enemies 移除，但 DOM 要等
            // 子弹命中才消失；继续对它输入只会被判错，必须跳过。
            if (enemy.Locked || enemy.Dead)
                continue;

            var input = _bank.Resolve(enemy, snapshot.Boss?.Head ?? "");
            if (string.IsNullOrEmpty(input) || !IsTypable(input))
            {
                if (_unresolved.Add(enemy.Word + "|" + enemy.Py))
                    Console.WriteLine($"[提示] 无法还原词条“{enemy.Word}”({enemy.Py})，暂时跳过。");
                continue;
            }
            targets.Add(new EnemyTarget(enemy, input));
        }

        var bossTodo = snapshot.Boss?.Todo ?? "";
        var hasBossText = bossTodo.Length > 0;
        var bossUrgent = hasBossText && BossTimerPercent(snapshot.Boss) <= 45;

        if (_trace)
        {
            var list = string.Join("; ", targets.Select(t =>
                $"{t.Enemy.Word}/{t.Enemy.Py}->{t.Input}@{t.Enemy.X:0}"));
            var locked = snapshot.Enemies.Count(e => e.Locked);
            Console.Error.WriteLine(
                $"[trace] act score='{snapshot.Score}' typed='{snapshot.Typed}' " +
                $"enemies={snapshot.Enemies.Count} locked={locked} " +
                $"hidden={snapshot.Hidden} vis={snapshot.Visibility} focus={snapshot.HasFocus} " +
                $"boss='{snapshot.Boss?.Head}' todo='{bossTodo}' fill='{snapshot.Boss?.Fill}' " +
                $"urgent={bossUrgent} targets=[{list}]");
        }

        // 手中已有半截词时，优先把它打完，避免浪费已输入的进度。
        if (snapshot.Typed.Length > 0)
        {
            if (targets.Count > 0)
            {
                await ContinueEnemyWordAsync(snapshot.Typed, targets, ct);
                return;
            }

            await BackspaceAsync(ct);
            return;
        }

        if (bossUrgent)
        {
            var c = FirstTypablePrefix(bossTodo, targets.Count == 0 ? 6 : 1);
            if (c.Length > 0)
            {
                await TypeTextAsync(c, ct);
                return;
            }
        }

        if (targets.Count > 0)
        {
            await ContinueEnemyWordAsync(snapshot.Typed, targets, ct);
            return;
        }

        if (hasBossText)
        {
            var c = FirstTypablePrefix(bossTodo, 6);
            if (c.Length > 0)
            {
                await TypeTextAsync(c, ct);
                return;
            }
        }

        await Task.Delay(60, ct);
    }

    private async Task ContinueEnemyWordAsync(
        string typed, List<EnemyTarget> targets, CancellationToken ct)
    {
        typed = (typed ?? "").Trim();

        if (typed.Length > 0)
        {
            var candidates = targets
                .Where(t => t.Input.StartsWith(typed, StringComparison.Ordinal))
                .OrderBy(t => t.Enemy.X)
                .ThenBy(t => t.Input.Length)
                .ToList();

            if (candidates.Count == 0)
            {
                await BackspaceAsync(ct);
                return;
            }

            var target = candidates[0];
            if (typed.Length < target.Input.Length)
            {
                await TypeTextAsync(target.Input[typed.Length..], ct);
                return;
            }

            // 理论上一整词打完就该击杀；还留着说明是复读怪或状态刚刷新，退一格重试。
            await BackspaceAsync(ct);
            return;
        }

        var next = targets
            .OrderBy(t => t.Enemy.X)
            .ThenBy(t => t.Input.Length)
            .First();
        await TypeTextAsync(next.Input, ct);
    }

    private async Task TypeAsync(char ch, CancellationToken ct)
    {
        await TypeTextAsync(ch.ToString(), ct);
    }

    private async Task TypeTextAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var started = Stopwatch.GetTimestamp();
        await _cdp.PressKeysAsync(text, ct);
        _keys += text.Length;

        var targetMs = 1000.0 * text.Length / Math.Max(1.0, _options.Cps);
        var elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        var waitMs = (int)Math.Round(targetMs - elapsedMs);
        if (waitMs > 0)
            await Task.Delay(waitMs, ct);
    }

    private async Task BackspaceAsync(CancellationToken ct)
    {
        await _cdp.PressBackspaceAsync(ct);
        var targetMs = 1000.0 / Math.Max(1.0, _options.Cps);
        await Task.Delay((int)Math.Max(8, targetMs), ct);
    }

    private void Report(GameSnapshot snapshot)
    {
        var now = _clock.ElapsedMilliseconds;
        if (now - _lastReportTick < 5000)
            return;
        _lastReportTick = now;

        var typed = snapshot.Typed.Length > 0 ? snapshot.Typed : "-";
        Console.WriteLine(
            $"[{_clock.Elapsed:mm\\:ss}] 分数 {snapshot.Score,-6} 波次 {snapshot.Wave,-3} " +
            $"HP {snapshot.Hp,-3} 连击 {snapshot.Combo,-4} 已敲 {_keys} 键 缓冲 {typed}");
    }

    private static bool IsTypable(string s)
    {
        if (string.IsNullOrEmpty(s))
            return false;
        foreach (var c in s)
        {
            if (c < 'a' || c > 'z')
                return false;
        }
        return true;
    }

    private static string FirstTypablePrefix(string s, int max)
    {
        var sb = new System.Text.StringBuilder(Math.Min(max, s.Length));
        foreach (var c in s)
        {
            var lower = char.ToLowerInvariant(c);
            if (lower < 'a' || lower > 'z')
                break;
            sb.Append(lower);
            if (sb.Length >= max)
                break;
        }
        return sb.ToString();
    }

    private static double BossTimerPercent(BossInfo? boss)
    {
        if (boss == null)
            return 100;
        var raw = (boss.Fill ?? "").Trim().TrimEnd('%');
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return value;
        return 100;
    }

    private sealed record EnemyTarget(EnemyInfo Enemy, string Input);
}
