# 打字大战 emoji · 自动打字机器人

一个用 **C# / .NET 10** 写的自动游玩脚本。它不修改游戏文件，而是通过 **Edge DevTools Protocol (CDP)** 读取页面 DOM，再向页面派发合成键盘事件，所以和真人按键走的是同一条游戏逻辑。

- 零 NuGet 依赖，只用 .NET 自带库。
- 自动启动 Edge、打开线上版（或本地 HTML）、点“开始游戏”。
- 自动识别普通词、拼音、Boss 的镜像/魔镜/蒙面/复读样式、强化挑战三选一。
- 一局结束自动“再来一次”；支持无界面挂机。
- 线上版实测 2 分 15 秒：**4813 分、第 7 波、HP 11、47 连击**，强化挑战与 Boss 均能处理。

## 运行

需要：

- Windows
- .NET 10 SDK（`dotnet --version` 能看到 10.x）
- Microsoft Edge（默认路径 `C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`）

最简单的方式：双击 **`run.cmd`**。

它会先构建（如果还没构建过），然后以 60 键/秒启动机器人。关闭机器人：在控制台按 `Ctrl+C`。

也可以手动运行：

```powershell
cd D:\Users\Desktop\VS\TypingBot
dotnet run -c Release -- --cps 60
```

或者直接运行已编译的程序：

```powershell
.\bin\Release\net10.0\TypingBot.exe --cps 60
```

默认会打开线上版：

`https://immae.top/2nd-Page/TypingEmoji/TypingEmoji.html`

也可以把第一个参数换成游戏 URL 或本地 HTML 文件：

```powershell
# 线上版（默认）
.\bin\Release\net10.0\TypingBot.exe --cps 60

# 显式指定 URL
.\bin\Release\net10.0\TypingBot.exe "https://immae.top/2nd-Page/TypingEmoji/TypingEmoji.html" --cps 60

# 玩本地文件
.\bin\Release\net10.0\TypingBot.exe "D:\Users\Desktop\打字游戏.html" --cps 60
```

## 常用参数

| 参数 | 说明 |
| --- | --- |
| `--cps 60` | 每秒最多派发多少键，默认 32，范围 1–200 |
| `--headless` | 无界面运行，适合后台挂机 |
| `--max-runtime 2.5` | 运行多少分钟后自动停止；不写则一直运行 |
| `--close-browser` | 退出时关闭机器人启动的 Edge |
| `--port 9222` | Edge 调试端口；被占用时换一个，如 9223 |
| `--profile <目录>` | 指定 Edge 用户数据目录（默认自动选可写目录） |
| `--edge <exe>` | 指定 `msedge.exe` 路径 |
| `--cat zh-elem` | 强制词库（`zh-elem` / `zh-mid` / `en-elem` / `en-mid` / `en-cet` / `custom`） |
| `--no-launch` | 不启动 Edge，只连接已经开着调试端口的页面 |
| `--no-restart` | 一局结束后不自动重开 |
| `--edge-no-sandbox` | 给 Edge 加 `--no-sandbox`，受限环境兜底 |
| `-h` / `--help` | 显示帮助 |

示例：

```powershell
# 后台挂机 30 分钟，结束后关闭浏览器
.\bin\Release\net10.0\TypingBot.exe --headless --cps 60 --max-runtime 30 --close-browser

# 指定本地游戏文件和端口
.\bin\Release\net10.0\TypingBot.exe "D:\Users\Desktop\打字游戏.html" --port 9223 --cps 60

# 自己先开好 Edge，再让机器人连接
.\bin\Release\net10.0\TypingBot.exe --no-launch --port 9222
```

## 工作原理

1. 启动 Edge，带独立 `--user-data-dir` 和 `--remote-debugging-port`，打开线上或本地游戏页。
2. 通过 `Runtime.evaluate` 每轮读取一次 DOM 快照：
   - 敌人：`.enemy` 的屏幕坐标、显示词、拼音、已输入/剩余、`locked`、淡出状态
   - Boss：`#boss-bar` 的名字、`#bb-todo` 剩余文本、`#bb-done`、计时条宽度
   - 状态：分数、波次、HP、连击、当前输入缓冲、遮罩/强化面板是否可见
3. 决定这一轮要敲什么：
   - 强化挑战：选最短的一张卡，一次敲完
   - 有敌人：优先打最靠左（最危险）的词；若已有输入前缀，则选能接上前缀的敌人继续
   - 没有敌人：敲 Boss 剩余文本；Boss 快超时时优先打 Boss
4. 用 `document.dispatchEvent(new KeyboardEvent('keydown', ...))` 派发按键，游戏自己的 `keydown` 监听器会照常处理。
5. 每次只做一个动作就重新读快照，避免目标中途死亡、Boss 切换、奖励面板弹出导致输入错位。
6. 启动参数禁用后台标签节流；`Page.bringToFront` 把游戏页切到前台。若 Windows 把窗口判定为“被遮挡”而暂停 `requestAnimationFrame`，程序发现分数连续 5 秒不动时会自动重新激活页面。

拼音还原优先直接使用页面 `.py` 的显示串；蒙面模式则用内置/自定义词库反查。线上版启动时会额外拉取 `banks/manifest.json` 和 5 个完整词库（实测 1561 条），蒙面 Boss 也有足够映射。镜中人和魔镜在 DOM 上同形，程序用 `#boss-bar` 里的 Boss 名区分。

## 常见问题

**启动后一直“等待调试端口”**
先换端口：`--port 9223`。某些残留 Edge 进程会占住 9222；程序也会自动改用 `--no-sandbox` 重启一次 Edge。

**提示 `Access to the path ... is denied`**
用工作区里的目录作为 profile：

```powershell
.\bin\Release\net10.0\TypingBot.exe --profile .\.edge-profile
```

**有页面但机器人不输入**
确认游戏页在前台（程序会自动 `Page.bringToFront`）。如果是手动开的 Edge，先点一下游戏页，或加 `--no-launch` 前确保调试端口是通的。

**提示“无法还原词条”**
说明当前词库里有程序没解析到的词（通常是自定义词库）。可以显式指定词库：

```powershell
.\bin\Release\net10.0\TypingBot.exe --cat zh-elem
```

**想自己看它打**
不要加 `--headless`。默认会打开一个独立的 Edge 窗口，机器人就在里面玩。

## 文件

| 文件 | 作用 |
| --- | --- |
| `Program.cs` | 参数解析、启动/回退 Edge、连接 CDP、主流程 |
| `Cdp.cs` | CDP 发现、WebSocket 请求/响应、读 DOM、派发按键 |
| `GameBot.cs` | 快照结构、战斗循环、Boss/奖励/遮罩处理 |
| `WordBank.cs` | 解析 HTML 内置词库、线上/本地 `banks/*.json`、自定义词库，还原拼音 |
| `run.cmd` | 一键构建并运行 |

> 仅供学习和单机娱乐使用。游戏文件本身不会被修改。
