# ref/ 目录说明

这个目录**不入库**（`.gitignore` 里排除了 `ref/*.dll`）。构建前需要你从本机已安装的
NotchPeninsula 里复制两个程序集过来：

| 文件 | 作用 |
|---|---|
| `NotchPeninsula.dll` | 宿主的插件接口契约（`INotchPlugin` / `IPluginHost` / `IWidget` / `IMediaSessionSource`…） |
| `SkiaSharp.dll` | 宿主使用的 2.88.8 版 SkiaSharp，`WidgetKit.Typeface()` 依赖它 |

## 为什么要引用宿主本体，而不是自己抄一份接口定义

插件加载时宿主是拿**自己的** `Type` 去和插件里的实现比对：插件里若自带一份同名接口，
两边是不同的 `Type`，`plugin is INotchPlugin` 恒为 false —— 表现就是
`PluginFailureKind.NotAPlugin`，而且不报任何异常细节。

所以 `csproj` 用 `Reference` + `Private=false` 直接引用宿主的程序集：编译期类型身份一致，
运行时也不会把宿主 dll 拷进插件目录。

## 从哪里取

NotchPeninsula 是单文件发布的 exe，程序集会在首次启动时解包到临时目录：

```
%TEMP%\.net\NotchPeninsula.v.<版本>\<随机名>\
    NotchPeninsula.dll
    SkiaSharp.dll
    libSkiaSharp.dll
    Microsoft.Windows.SDK.NET.dll
    WinRT.Runtime.dll
    ...
```

把这个目录里的 `NotchPeninsula.dll` 和 `SkiaSharp.dll` 复制到本目录即可：

```cmd
copy "%TEMP%\.net\NotchPeninsula.v.2.2.5\*\NotchPeninsula.dll" ref\
copy "%TEMP%\.net\NotchPeninsula.v.2.2.5\*\SkiaSharp.dll" ref\
```

版本号按你实际安装的改；插件本身也要用**同版本**的宿主编译，`TargetFramework`
（`net10.0-windows10.0.19041.0`）必须与宿主逐字一致。
