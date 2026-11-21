# NpsYpmLyrics · YesPlayMusic 时间轴

[![release](https://img.shields.io/github/v/release/Qinweiyi/NpsYpmLyrics)](https://github.com/Qinweiyi/NpsYpmLyrics/releases/latest)

给 NotchPeninsula 补上 YesPlayMusic 的**播放时间轴**：岛上不新增任何媒体窗口，
媒体条、进度、歌词全都还是宿主内置的那一套，插件只负责把「唱到哪一秒」喂准。
定位和 `mediacdp`（网易云/酷狗时间轴获取）完全一致。

## 下载安装（不用编译）

1. 到 [Releases](https://github.com/Qinweiyi/NpsYpmLyrics/releases/latest) 下载 `NpsYpmLyrics-vX.Y.Z.zip`
2. 解压后把整个文件夹放到 `C:\Program Files\NotchPeninsula\plugins\NpsYpmLyrics\`
3. 重启 NotchPeninsula
4. **用包里的 `start-with-cdp.cmd` 启动 YesPlayMusic**（关键一步，见下一节）

需要自己编译的见文末「构建」。

## 它解决什么问题

宿主内置链路是 SMTC → 媒体面板。实测 **YesPlayMusic 的 Electron 外壳只往 SMTC 注册了歌名和歌手，
从不调用 `navigator.mediaSession.setPositionState()`** —— 系统拿到的位置与时长恒为 `0 / 0`
（宿主日志里的「时长参数=0s」就是这么来的），于是进度条不动、歌词钉在第一句。

插件直连播放器的 Chromium 调试端口，从页面里的 Vuex player 状态读真实进度，再实现
`IMediaSessionSource` 交给宿主。宿主判定「SMTC 给不出时长 + 这个 appId 归本插件管」时接管时间轴：

```
CDP（9333）读 _progress / 曲目 dt  ──→ IMediaSessionSource.Position / Duration
                                            ↓
                          宿主媒体面板：进度条、时间、歌词高亮全部跟着走
```

## 启动播放器（重要）

YesPlayMusic 必须以调试端口启动，插件才读得到真实进度：

**双击桌面的 `YPM-lyrics-debug.cmd`**（等价于
`YesPlayMusic-0.4.11.exe --remote-debugging-port=9333`）。

- 用原来的图标启动，插件拿不到进度，宿主只能退回 SMTC —— 也就是现在这种「进度恒为 0」的老状态。
- 端口 9333 是给本插件独占的：9222 常被别的工具/插件占着（`mediacdp` 就死盯 9222），
  两边抢同一个 DevTools 会话会让进度断续。插件按 `9333,9222` 顺序探测，两者都兼容。
- 调试端口只监听 `127.0.0.1`，不对外网开放。

## 验证是否生效

`%LOCALAPPDATA%\NotchPeninsula\lyrics\ypm\plugin.log` 里应该能看到：

```
CDP 已连接（端口 9333）: ws://127.0.0.1:9333/devtools/page/...
宿主读取时间轴 #1 pos=00:01:51.75 dur=235s playing=False
```

宿主只有在**确认接管**之后才会去读 `Position`，所以「宿主读取时间轴」这行出现，就代表媒体面板
的进度已经由插件驱动了。

## 两种模式

| 模式 | 效果 | 开启方式 |
|---|---|---|
| **时间轴模式（默认）** | 岛上只有宿主原生媒体条，插件不注册任何组件 | 默认 |
| 岛内歌词行 | 额外在岛内显示一行当前歌词（点开是 5 行滚动歌词页 + 上一首/播放/下一首） | 注册表 `showWidget` 设为 `1` |

时间轴模式下插件**不会**请求任何歌词接口；宿主自己的歌词引擎接管显示，插件只补进度。

## 可调设置

宿主没接插件设置页，参数直接写注册表（`HKCU\SOFTWARE\NotchPeninsula`），改完重启宿主生效：

| 值名 | 默认 | 说明 |
|---|---|---|
| `Plugin.workbuddy.nps.ypmlyrics.keywords` | `yesplay` | 只接管 SMTC 里 appId 含该关键字的会话；多个用逗号分隔 |
| `Plugin.workbuddy.nps.ypmlyrics.cdpPort` | `9333,9222` | CDP 端口探测顺序 |
| `Plugin.workbuddy.nps.ypmlyrics.showWidget` | `0` | `1` = 额外显示岛内歌词行 |
| `Plugin.workbuddy.nps.ypmlyrics.offsetMs` | `0` | 仅岛内歌词行模式使用：歌词时间偏移（毫秒） |

```powershell
# 例：开关岛内歌词行
Set-ItemProperty HKCU:\SOFTWARE\NotchPeninsula -Name "Plugin.workbuddy.nps.ypmlyrics.showWidget" -Value "1"
```

## 已知边界

- **歌词文本仍由宿主自己匹配**（落月/网易云），它取时长用的是 SMTC 的 `EndTime`，而 YesPlayMusic
  永远是 0 —— 所以遇到同名翻唱/改编时，宿主仍可能挑到另一版。插件只负责把**进度**补准，
  没法替宿主决定用哪份词。想要按曲目 id 精确取词，就开 `showWidget=1` 用插件自带的歌词行。
- 宿主暂停久了会注销 SMTC 会话；没有会话时宿主不显示媒体面板（这是宿主行为，不是插件问题），
  播放起来就恢复。
- 控制按钮（上一首/播放/下一首）优先走 CDP 直接点播放器自己的按钮，不依赖 SMTC 会话是否在线。

## 目录

- `C:\Program Files\NotchPeninsula\plugins\NpsYpmLyrics\` —— 插件本体（`NpsYpmLyrics.dll` + `plugin.json` + 启动脚本）
- `%LOCALAPPDATA%\NotchPeninsula\lyrics\ypm\` —— CDP 日志与歌词缓存（卸载时宿主会问是否删除）

## 构建

前置：`.NET 10 SDK`，并已安装 NotchPeninsula（要从它的解包目录取宿主程序集）。

```cmd
:: 1. 准备引用（只需一次，详见 ref/README.md）
copy "%TEMP%\.net\NotchPeninsula.v.2.2.5\*\NotchPeninsula.dll" ref\
copy "%TEMP%\.net\NotchPeninsula.v.2.2.5\*\SkiaSharp.dll" ref\

:: 2. 编译并部署到宿主 plugins 目录
build.cmd
```

`csproj` 直接引用宿主的 `NotchPeninsula.dll` / `SkiaSharp.dll`（`Private=false`），
不复制接口定义 —— 接口类型身份与运行中的宿主一致，加载时才不会被判成 `NotAPlugin`。
`TargetFramework` 必须与宿主逐字一致（`net10.0-windows10.0.19041.0`）。

仓库里不含这两个 dll（属于宿主的二进制），`ref/` 只有一份取用说明；缺文件时编译会直接给出提示。

## 排障

| 现象 | 处理 |
|---|---|
| 媒体条进度还是不动 | 看 `plugin.log` 有没有 `CDP 已连接`；大概率是用普通图标启动的播放器 |
| 日志里没有「宿主读取时间轴」 | 宿主没接管：确认播放器在放、且 `keywords` 能匹配上 SMTC 的 appId |
| 进度跳一下又回原点 | 和别的工具抢调试端口，改用 9333（`YPM-lyrics-debug.cmd` 已默认） |
| 岛内没有媒体条 | 设置 → 显示设置 → 显示内容，确认媒体项被勾选 |

## 兼容性

针对 NotchPeninsula **2.2.5** 编译（本机安装版本）。宿主升级后若接口变动，重新 `build.cmd` 即可。
