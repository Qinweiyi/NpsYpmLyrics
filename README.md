# NpsYpmLyrics · YesPlayMusic 歌词时间轴

[![release](https://img.shields.io/github/v/release/Qinweiyi/NpsYpmLyrics)](https://github.com/Qinweiyi/NpsYpmLyrics/releases/latest)

对齐 [YesPlayMusic](https://github.com/qier222/YesPlayMusic) 的歌词时间轴到
[NotchPeninsula](https://github.com/GEORGEWWWU/NotchPeninsula) 媒体条；
进度条、时间、歌词高亮全部复用宿主内置的媒体控制。

插件不注册任何岛内组件，只负责把「唱到哪一秒」喂准，定位和 `mediacdp`（网易云/酷狗时间轴获取）完全一致。

## 下载安装（不用编译）

1. 到 [Releases](https://github.com/Qinweiyi/NpsYpmLyrics/releases/latest) 下载 `NpsYpmLyrics-vX.Y.Z.zip`
2. 解压后把整个文件夹放到 `C:\Program Files\NotchPeninsula\plugins\NpsYpmLyrics\`
3. 重启 NotchPeninsula
4. **用包里的 `start-with-cdp.cmd` 启动 YesPlayMusic**（关键一步，见下节）

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

## 必须带调试端口启动（实测结论）

YesPlayMusic 必须以调试端口启动，否则这个插件拿不到任何可用数据：

```cmd
YesPlayMusic-0.4.11.exe --remote-debugging-port=9333
```

包里的 `start-with-cdp.cmd` 就是这一行，直接双击它即可。

这不是猜测，是做过的对照实验（同一台机器、同一首歌、都在播放中）：

| 条件 | 插件侧日志 | 结果 |
|---|---|---|
| 有 CDP（端口可达） | `CDP 已连接（端口 9222）` + `宿主读取时间轴 #1 pos=00:02:07.50 dur=235s playing=True` | 宿主接管，进度/歌词正常 |
| 无 CDP（端口不可达） | 只有 `CDP: 端口 9999 未开放`，**没有任何 `宿主读取时间轴`** | 宿主持不到时长，回落到 SMTC，进度仍是 0 |

对照时 SMTC 里确实有会话在播放（`APP=YesPlayMusic.exe / STATUS=Playing / POS=0 / DUR=0`），
排除了「没在播所以没数据」的假阴性。

原因在宿主侧的接管条件：`smtcDuration == 0` **且** 外部源 `Duration > 0` 才会接管
（`Media/MediaController.ExternalSource.cs`）。没有 CDP 就没有时长，宿主自然不接管。

端口只监听 `127.0.0.1`，不对外网开放。

## 验证是否生效

日志在 `%LOCALAPPDATA%\NotchPeninsula\ypm-timeline\plugin.log`，出现下面两行就说明接管成功：

```
CDP 已连接（端口 9333）: ws://127.0.0.1:9333/devtools/page/...
宿主读取时间轴 #1 pos=00:01:51.75 dur=235s playing=False
```

宿主只在**确认接管之后**才读 `Position`，所以第二行是接管的直接证据。

## 可调设置

宿主没接插件设置页，参数直接写注册表（`HKCU\SOFTWARE\NotchPeninsula`），改完重启宿主生效：

| 值名 | 默认 | 说明 |
|---|---|---|
| `Plugin.qwy.nps.ypmlyrics.keywords` | `yesplay` | 只接管 SMTC 里 appId 含该关键字的会话；多个用逗号分隔 |
| `Plugin.qwy.nps.ypmlyrics.cdpPort` | `9333,9222` | CDP 端口探测顺序 |

```powershell
# 例：换成别的调试端口
Set-ItemProperty HKCU:\SOFTWARE\NotchPeninsula -Name "Plugin.qwy.nps.ypmlyrics.cdpPort" -Value "9333"
```

## 已知边界

- **歌词文本仍由宿主自己匹配**（落月/网易云），它取时长用的是 SMTC 的 `EndTime`，对 YesPlayMusic
  永远是 0 —— 所以遇到同名翻唱/改编时，宿主仍可能挑到另一版。插件只负责把**进度**补准，
  没法替宿主决定用哪份词。
- 宿主暂停久了会注销 SMTC 会话；没有会话时宿主不显示媒体面板（宿主行为，不是插件问题），
  播放起来就恢复。
- 传输控制（上一首 / 播放 / 下一首）优先走 CDP 直接点播放器自己的按钮，不依赖 SMTC 会话是否在线。

## 目录

- `C:\Program Files\NotchPeninsula\plugins\NpsYpmLyrics\` —— 插件本体（`NpsYpmLyrics.dll` + `plugin.json` + 启动脚本）
- `%LOCALAPPDATA%\NotchPeninsula\ypm-timeline\` —— 插件日志（卸载时宿主会问是否删除）

## 构建

前置：`.NET 10 SDK`，并已安装 NotchPeninsula（要从它的解包目录取宿主程序集）。

```cmd
:: 1. 准备引用（只需一次，详见 ref/README.md）
copy "%TEMP%\.net\NotchPeninsula.v.2.2.5\*\NotchPeninsula.dll" ref\

:: 2. 编译并部署到宿主 plugins 目录
build.cmd
```

`csproj` 直接引用宿主的 `NotchPeninsula.dll`（`Private=false`），
不复制接口定义 —— 接口类型身份与运行中的宿主一致，加载时才不会被判成 `NotAPlugin`。
`TargetFramework` 必须与宿主逐字一致（`net10.0-windows10.0.19041.0`）。

仓库里不含这个 dll（属于宿主的二进制），`ref/` 只有一份取用说明；缺文件时编译会直接给出提示。

## 排障

| 现象 | 处理 |
|---|---|
| 媒体条进度还是不动 | 看 `plugin.log` 有没有 `CDP 已连接`；大概率是用普通图标启动的播放器 |
| 日志里没有「宿主读取时间轴」 | 宿主没接管：确认播放器带调试端口、且在播放，`keywords` 能匹配上 SMTC 的 appId |
| 进度跳一下又回原点 | 和别的工具抢调试端口，改用 9333（`start-with-cdp.cmd` 已默认） |
| 岛内没有媒体条 | 设置 → 显示设置 → 显示内容，确认媒体项被勾选 |

## 相关仓库

| 项目 | 仓库 | 在这一环里的角色 |
|---|---|---|
| YesPlayMusic | https://github.com/qier222/YesPlayMusic | 播放器本体；插件通过它的 DevTools 端口读真实进度 |
| NotchPeninsula | https://github.com/GEORGEWWWU/NotchPeninsula | 宿主（NPS）；提供 `IMediaSessionSource` 接口与内置媒体条 |
| 本插件 | https://github.com/Qinweiyi/NpsYpmLyrics | 把上面两者的时间轴接起来 |

## 兼容性

针对 NotchPeninsula **2.2.5** 编译（本机安装版本）。宿主升级后若接口变动，重新 `build.cmd` 即可。
