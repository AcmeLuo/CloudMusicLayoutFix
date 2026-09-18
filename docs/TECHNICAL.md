# 技术说明

面向想了解原理、改样式或排障的人。只想用的话看 [README](../README.md) 就够了。

## 根因

客户端界面写死了约 **1056 CSS px** 的最小布局宽度：

```css
/* 侧边栏 */   204px
/* 主内容区 */ flex: 1 0 852px   /* flex-shrink: 0，不允许收缩 */
/* 页面根 */   overflow-x: clip /* 溢出部分直接裁掉 */
/* 标题栏 */   display: grid    /* 固定列宽 */
```

每台显示器能提供的 **CSS 宽度 = 物理宽度 ÷ 显示缩放比**：

| 显示器 | 物理分辨率 | 缩放 | 可用 CSS 宽度 | 够用吗 |
| --- | --- | --- | --- | --- |
| 主屏 | 2560×1440 | 150% | 1707 | ✅ |
| 副屏 A | 1080×1920 | 125% | **864** | ❌ |
| 副屏 B | 1600×2560 | 200% | **800** | ❌ |

副屏不够宽 → 内容向右溢出 → `overflow-x: clip` 把最右端的窗口按钮整个裁掉，
标题栏与播放栏内部的元素被压到最小间距甚至重叠。

> 这不是 DPI 感知问题。实测进程 DPI 感知与 CEF 的 `devicePixelRatio` 在副屏上都是正确的
> （1.25 / 2.0），根因是布局宽度。曾试过给 `cloudmusic.exe` 注入 `PerMonitorV2` 清单——
> 进程确实变成 V2 感知，但界面依然错位，而且会破坏它的数字签名，所以没有采用。

## 修复方式

前端打包在加密容器里（`package\orpheus.ntpk`、用户目录的 `web.pack`），磁盘上没有明文副本；
但客户端是 CEF 应用，接受 `--remote-debugging-port`，因此在运行时通过 DevTools 协议注入 CSS：

```css
[class*='RightContainer'] { flex: 1 1 0 !important; min-width: 0 !important; } /* 允许收缩 */
[class*='HeaderRow']      { display: flex !important; }                        /* grid → flex */
[class*='IconBar']        { flex: 0 0 auto !important; }                       /* 窗口按钮永远完整 */
[class*='DomWarpper']     { flex: 1 1 120px !important; min-width: 88px !important; } /* 搜索框先让位 */
[class*='songPlayInfo']   { width: min(84px, calc(50vw - 334px)) !important; } /* 播放栏歌名区让位 */
```

另外还有三处"grid 改 flex 后需要还原"的规则：标题栏里的拖拽间隔（`.draggable:empty`，
8/8/20/20px）、后退按钮（`[class*='HistoryIcon']`，28px）、一起听/识曲图标
（`[class*='listening']`，36×36 居中）——原 grid 布局下它们的尺寸来自网格列宽，
改 flex 后会按内容塌缩（识曲按钮会从 36×36 变成 36×24，hover 高亮比搜索框矮一截），
导致昵称与图标位置、尺寸偏移。完整规则见 [css/layout-fix.css](../css/layout-fix.css)。

`CloudMusicLayoutFix.exe` 把「启动客户端 + 注入 CSS + 断线重连 + 页面刷新后重新注入 + 托盘图标」
打包成一个约 39 KB 的单文件程序（C# / .NET Framework 4.x，源码见 [src/CloudMusicLayoutFix.cs](../src/CloudMusicLayoutFix.cs)）。

## 参数与日志

```
CloudMusicLayoutFix.exe                       # 安装：顶替 cloudmusic.exe（一次搞定，不常驻）
CloudMusicLayoutFix.exe --install             # 同上，不弹询问框
CloudMusicLayoutFix.exe --restore             # 还原官方 cloudmusic.exe
CloudMusicLayoutFix.exe --once [--watch <秒>]  # 只注入一次，不改任何文件
CloudMusicLayoutFix.exe --tray                # 常驻托盘
CloudMusicLayoutFix.exe [--app <路径>] [--port <端口>] [--no-launch]   # 手动指定/只注入
```

日志：`%LOCALAPPDATA%\CloudMusicLayoutFix\layout-injector.log`
正常时可见 `inject  applied vw=… dpr=… closeVisible=true`。

| 情况 | 行为 |
| --- | --- |
| 客户端没在运行 | 自动以 `--remote-debugging-port=9222` 启动 |
| 已在运行、且带调试端口 | 直接接管注入 |
| 已在运行、但没带调试端口 | 弹窗询问是否关闭并重新启动（不会丢失播放进度）|

## 三种用法对比

| 方式 | 常驻进程 | 覆盖范围 |
| --- | --- | --- |
| 直接双击 `CloudMusicLayoutFix.exe`（默认） | 无 | **任意启动方式**：顶替 `cloudmusic.exe`，官方原文件保留为 `cloudmusic.real.exe` |
| `--once` | 无 | 只在本次使用中注入，不动任何文件 |
| `--tray` | 一个托盘图标 | 客户端每次启动，页面重载也不失效 |

随时可切换；`--restore` 或删除 exe 即可回到原状。

## 已验证

Windows 11 24H2 + 网易云音乐 3.1.40.205461，三台显示器（150% / 125% / 200%）：

| 显示器 | CSS 视口 | DPR | 窗口按钮 | 控件重叠 |
| --- | --- | --- | --- | --- |
| 主屏 150% | 1707×920 | 1.5 | 可见 | 0 |
| 副屏 A 125% | 864×1496 | 1.25 | 可见 | 0 |
| 副屏 B 200% | 800×1240 | 2.0 | 可见 | 0 |

主屏上各元素坐标与修复前**像素一致**（搜索框 280..538、一起听图标 553..575、
用户区 1279..1446、右侧图标栏 1456..1667），说明正常屏幕上不会改变任何布局。

## 构建与改样式

```bat
build-exe.cmd     :: 用系统自带的 csc.exe 编译 src\CloudMusicLayoutFix.cs
                  :: css\layout-fix.css 会作为资源内嵌进 exe
```

改了 CSS 要重新编译一次。只调样式不重编译的话，先用
`cloudmusic.exe --remote-debugging-port=9222` 启动客户端，在浏览器打开
`http://127.0.0.1:9222` 用 DevTools 现场改，满意后写回 `css/layout-fix.css`。

## 排障与反馈

提 Issue 时请附上：

1. `%LOCALAPPDATA%\CloudMusicLayoutFix\layout-injector.log` 的最后 20 行；
2. 如果界面仍有元素被裁，说明是哪块屏、哪个界面（截图最好）；
3. 显示器物理分辨率 + 显示缩放比、客户端版本、出问题的是哪块屏。

## 窄屏下的界面审计

在 125%（864 CSS px）与 200%（800 CSS px）两种窄视口下逐个界面看了一遍，除首页之外还有这几个地方会溢出：

| 界面 | 原生表现 | 样式表里的处理 |
| --- | --- | --- |
| 歌单 / 歌曲列表 | 列宽写死 692px（45+317+200+60+70），「时长」整列落在视口外、操作列贴边 | 窄屏时用 `left/right` 约束列表容器、内层跟随 100%，各列按既有 flex 比例压缩 |
| 设置页 · 顶部锚点导航 | 一排锚点链接总宽约 760px，而它是 `overflow-x: hidden`，后面的设置项被整段裁掉 | 允许换行（换行后仍是一行一个，点得到） |
| 设置页 · 内容区 | `.right` / `.table` 写死 592px、`.preview` 616px，外层又是 `nowrap` flex，内容被顶出视口 | 收敛宽度上限 + 允许换行 |

其余界面（首页推荐、精选、播客、关注、我的音乐、最近播放、我的播客、消息中心、搜索、换肤、
播客里的语音卡片网格）在同样的窄视口下没有元素溢出。

桌面歌词与迷你播放器是独立窗口（不在 CEF 调试目标里），当前注入无法覆盖。

## 已知限制

| 限制 | 说明 |
| --- | --- |
| 仅 Windows | 依赖 CEF 调试端口与 Win32 窗口，未在其它平台验证 |
| 需要调试端口 | 只监听 `127.0.0.1`，但同机进程理论上可借此控制客户端页面 |
| 窄屏内容更紧凑 | 搜索框收窄、播放栏歌名省略号截断——这正是原来被重叠/裁掉的空间 |
| 客户端升级后 | 若升级替换了 `cloudmusic.exe`，重新运行一次本程序即可 |
| 依赖类名模糊匹配 | 选择器写成 `[class*='RightContainer']` 这类形式，小版本更新一般不受影响 |
