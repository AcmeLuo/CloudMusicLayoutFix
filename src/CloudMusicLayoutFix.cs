// CloudMusicLayoutFix —— 单文件启动器 + 布局修正注入器
//
// 作用：
//   1) 以 --remote-debugging-port=<port> 启动网易云音乐（若已在运行则直接接管）；
//   2) 常驻连接 DevTools 协议，把内嵌的 layout-fix.css 注入页面，
//      断线重连、页面跳转/刷新后自动重新注入，每 3 秒保活一次。
//
// 为什么要注入：客户端前端打包在加密容器里，磁盘上没有明文可改；但它接受调试端口，
// 于是可以在运行时补 CSS，让界面在窄视口下自适应（保持原生 DPI 尺寸）。
//
// 编译见 build-exe.cmd（csc + /codepage:65001，CSS 以资源形式内嵌）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CloudMusicLayoutFix
{
    internal static class Program
    {
        private const string Version = "1.0";
        private const string MutexName = "CloudMusicLayoutFix.SingleInstance";
        private const string StyleId = "ppi-layout-fix";
        private const string AppName = "CloudMusicLayoutFix";
        // 运行期目录：只放日志。
        private const string StoreDirName = "CloudMusicLayoutFix";

        private static readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
        private static readonly object LogLock = new object();
        private static Mutex singleInstance;
        private static NotifyIcon tray;
        private static string logPath;
        private static string cssText;
        private static string appPath;
        private static int port = 9222;
        private static int messageId = 1;
        private static volatile bool running = true;
        private static volatile bool manualInject;
        private static DateTime stopAt = DateTime.MaxValue;
        private static bool exitWhenClientStops;
        private static string[] originalArgs = new string[0];
        private static string appPathSource = "default";

        [STAThread]
        private static int Main(string[] args)
        {
            bool created;
            singleInstance = new Mutex(true, MutexName, out created);

            var storeRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            logPath = Path.Combine(storeRoot, StoreDirName, "layout-injector.log");
            try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)); } catch { }

            // 可选参数：--port <端口>，用于本机 9222 已被别的程序占用的情况。
            for (int i = 0; i < args.Length - 1; i++)
            {
                int parsed;
                if (args[i] == "--port" && int.TryParse(args[i + 1], out parsed) && parsed > 0 && parsed < 65536)
                    port = parsed;
            }

            cssText = LoadResource("layout-fix.css");
            appPath = ResolveAppPath(args);
            originalArgs = args;
            bool noLaunch = HasFlag(args, "--no-launch");
            bool once = HasFlag(args, "--once") || HasFlag(args, "--watch");
            bool tray = HasFlag(args, "--tray");

            // 已经顶替了 cloudmusic.exe（同目录存在 cloudmusic.real.exe）时，
            // 任何启动方式都走"注入后即退出"，这样用户不必记任何参数、也不会有常驻进程。
            if (IsInstalledInPlace()) once = true;

            if (HasFlag(args, "--restore") || HasFlag(args, "--uninstall")) return DoRestore();
            if (HasFlag(args, "--install")) return DoInstall();

            if (!once && !tray)
            {
                var answer = MessageBox.Show(
                    "是否把修复安装为网易云音乐的默认启动方式？（推荐，一次搞定）\r\n\r\n" +
                    "是：把启动器放进客户端目录顶替 cloudmusic.exe（官方原文件改名为 cloudmusic.real.exe，可一键还原）。\r\n" +
                    "      之后无论从开始菜单、任务栏还是直接双击 exe 打开，都会自动修正，且不留常驻进程。\r\n\r\n" +
                    "否：只在本次使用中生效（程序退出后不保留）。",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes) return DoInstall();
                once = true;
            }

            if (once)
            {
                // 一次性模式：注入完成后自行退出，不留常驻进程。
                // --watch <秒> 控制注入后继续观察多久（默认 120 秒，用来覆盖早期的页面重载）；
                // --watch 0 表示注入一次后立刻退出。
                int watch = 120;
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i] == "--watch") int.TryParse(args[i + 1], out watch);
                // watch 为 0 时也要留出"连上页面并注入一次"的时间
                stopAt = DateTime.UtcNow.AddSeconds(watch > 0 ? watch : 20);
                exitWhenClientStops = true;

            Log(AppName + " " + Version + " one-shot mode  watch=" + watch + "s  app=" + appPath + "   (found via: " + appPathSource + ")");
                if (!noLaunch && !EnsureAppRunning()) return 1;
                InjectorLoop().GetAwaiter().GetResult();
                Log("one-shot finished, exiting");
                return 0;
            }

            if (!created)
            {
                // 已经有实例常驻托盘了：本次启动只负责把客户端以调试端口拉起来
                // （或者提示重启），注入仍然由那个常驻实例完成。
                Log("second instance: ensure client is running with the debugging port");
                if (!noLaunch) EnsureAppRunning();
                return 0;
            }

            Log(AppName + " " + Version + " starting  css=" + cssText.Length + " bytes  app=" + appPath + "   (found via: " + appPathSource + ")");

            if (!noLaunch)
            {
                if (!EnsureAppRunning()) return 1;
            }

            Task.Run((Func<Task>)InjectorLoop);
            RunTray();

            running = false;
            Log("exit");
            try { if (singleInstance != null) singleInstance.ReleaseMutex(); } catch { }
            return 0;
        }

        // ---------------------------------------------------------------- 启动客户端

        private static bool EnsureAppRunning()
        {
            if (appPath == null || !File.Exists(appPath))
            {
                MessageBox.Show("找不到 cloudmusic.exe。请用 --app \"完整路径\" 指定，或确认网易云音乐已安装。",
                    AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            bool runningNow = Process.GetProcessesByName("cloudmusic").Length > 0;
            if (runningNow)
            {
                if (PortOpen(port))
                {
                    Log("client already running with debugging port " + port);
                    return true;
                }

                var answer = MessageBox.Show(
                    "网易云音乐正在运行，但没有以修复方式启动。\r\n是否现在关闭它并重新以修复方式启动？（不会丢失播放进度）",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes)
                {
                    Log("user declined to restart the client");
                    return false;
                }
                StopClient();
            }

            try
            {
                var psi = new ProcessStartInfo(appPath, BuildClientArguments(port))
                {
                    WorkingDirectory = Path.GetDirectoryName(appPath),
                    UseShellExecute = false
                };
                Process.Start(psi);
                Log("started client: " + appPath + " " + psi.Arguments);
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动网易云音乐失败：" + ex.Message, AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            for (int i = 0; i < 40 && !PortOpen(port); i++) Thread.Sleep(250);
            return true;
        }

        private static void StopClient()
        {
            foreach (var p in Process.GetProcessesByName("cloudmusic"))
            {
                try { p.CloseMainWindow(); } catch { }
            }
            for (int i = 0; i < 20; i++)
            {
                if (Process.GetProcessesByName("cloudmusic").Length == 0) return;
                Thread.Sleep(250);
            }
            foreach (var p in Process.GetProcessesByName("cloudmusic"))
            {
                try { p.Kill(); } catch { }
            }
            Thread.Sleep(1500);
        }

        /// <summary>
        /// 转发启动参数：把调用方（快捷方式 / 开机自启 / 关联命令）传给 cloudmusic.exe 的参数
        /// 原样带给客户端，只额外加上调试端口，并过滤掉本程序自己的开关。
        /// </summary>
        private static string BuildClientArguments(int p)
        {
            var sb = new StringBuilder("--remote-debugging-port=" + p);
            for (int i = 0; i < originalArgs.Length; i++)
            {
                var a = originalArgs[i];
                if (a == "--once" || a == "--no-launch") continue;
                if (a == "--watch" || a == "--port" || a == "--app") { i++; continue; }
                sb.Append(' ').Append(a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a);
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 一劳永逸安装 / 还原

        /// <summary>本程序是否已经顶替了 cloudmusic.exe（判别特征：同目录存在 cloudmusic.real.exe）。</summary>
        private static bool IsInstalledInPlace()
        {
            var dir = Path.GetDirectoryName(Application.ExecutablePath);
            return dir != null && File.Exists(Path.Combine(dir, "cloudmusic.real.exe"));
        }

        private static bool IsElevated()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>需要管理员权限时用 UAC 重新启动自己。</summary>
        private static int ElevateAndExit(string argument)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, argument)
                {
                    UseShellExecute = true,
                    Verb = "runas"
                });
            }
            catch
            {
                MessageBox.Show("这一步需要管理员权限（已被取消）。", AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            return 0;
        }

        /// <summary>把启动器放进客户端目录顶替 cloudmusic.exe，官方原文件改名为 cloudmusic.real.exe。</summary>
        private static int DoInstall()
        {
            if (!IsElevated()) return ElevateAndExit("--install");

            var dir = Path.GetDirectoryName(appPath);
            if (dir == null || !Directory.Exists(dir))
            {
                MessageBox.Show("找不到网易云音乐安装目录。可用 --app <cloudmusic.exe 路径> 指定。", AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            var target = Path.Combine(dir, "cloudmusic.exe");
            var original = Path.Combine(dir, "cloudmusic.real.exe");
            if (!File.Exists(target))
            {
                MessageBox.Show("找不到 " + target, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            // cloudmusic.real.exe 已存在，说明官方原文件早就备份过了，直接覆盖 cloudmusic.exe 即可。
            // （不要靠"两个文件大小是否相同"来判断：覆盖安装时容易把官方备份冲掉。）
            bool alreadyPatched = File.Exists(original);
            StopClient();
            try
            {
                if (!alreadyPatched) File.Move(target, original);
                File.Copy(Application.ExecutablePath, target, true);
                SetAutostartToTarget(target);
            }
            catch (Exception ex)
            {
                Log("install failed: " + ex.Message);
                MessageBox.Show("安装失败：" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            Log("install: replaced " + target + " (original -> cloudmusic.real.exe)");
            MessageBox.Show(
                "安装完成。\r\n\r\n" +
                "以后无论从开始菜单、任务栏、开机自启还是直接双击 exe 打开网易云音乐，都会自动应用修复，" +
                "并且不会留下任何常驻进程。\r\n\r\n" +
                "客户端目录：" + dir + "\r\n" +
                "官方原文件已备份为 cloudmusic.real.exe；如需还原，运行\r\n" +
                "    CloudMusicLayoutFix.exe --restore",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        /// <summary>还原官方 cloudmusic.exe。</summary>
        private static int DoRestore()
        {
            if (!IsElevated()) return ElevateAndExit("--restore");

            var dir = Path.GetDirectoryName(appPath);
            if (dir == null) return 1;
            var target = Path.Combine(dir, "cloudmusic.exe");
            var original = Path.Combine(dir, "cloudmusic.real.exe");
            if (!File.Exists(original))
            {
                MessageBox.Show("没有找到 cloudmusic.real.exe，可能并未安装过。", AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
            try
            {
                StopClient();
                File.Copy(original, target, true);
                File.Delete(original);
            }
            catch (Exception ex)
            {
                MessageBox.Show("还原失败：" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            Log("restore: cloudmusic.exe restored from cloudmusic.real.exe");
            MessageBox.Show("已还原为官方原版 cloudmusic.exe。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        /// <summary>把开机自启里的启动器路径换回官方入口，并去掉本程序的开关。</summary>
        private static void SetAutostartToTarget(string targetExe)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    var current = key.GetValue("cloudmusic") as string;
                    if (string.IsNullOrEmpty(current)) return;
                    var extra = string.Empty;
                    var match = Regex.Match(current, "^\"[^\"]*\"\\s*(.*)$");
                    if (match.Success) extra = match.Groups[1].Value;
                    else
                    {
                        var parts = current.Split(new[] { ' ' }, 2);
                        if (parts.Length > 1) extra = parts[1];
                    }
                    extra = extra.Replace("--once", string.Empty).Replace("--tray", string.Empty).Trim();
                    var value = "\"" + targetExe + "\"" + (extra.Length > 0 ? " " + extra : string.Empty);
                    key.SetValue("cloudmusic", value);
                    Log("autostart -> " + value);
                }
            }
            catch (Exception ex) { Log("autostart update failed: " + ex.Message); }
        }

        private static string ResolveAppPath(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--app") return Path.GetFullPath(args[i + 1]);

            var self = Application.ExecutablePath;
            var selfDir = Path.GetDirectoryName(self);

            // 1) 本程序已经顶替了 cloudmusic.exe：真正的主程序就在旁边
            if (selfDir != null)
            {
                var real = Path.Combine(selfDir, "cloudmusic.real.exe");
                if (File.Exists(real)) { appPathSource = "next to this program (cloudmusic.real.exe)"; return real; }
                var sibling = Path.Combine(selfDir, "cloudmusic.exe");
                if (File.Exists(sibling) && !string.Equals(sibling, self, StringComparison.OrdinalIgnoreCase))
                { appPathSource = "next to this program"; return sibling; }
            }

            // 2) 正在运行的客户端进程 —— 与安装目录无关，最可靠
            try
            {
                foreach (var p in Process.GetProcessesByName("cloudmusic"))
                {
                    try
                    {
                        var file = p.MainModule.FileName;
                        if (!string.IsNullOrEmpty(file) && File.Exists(file))
                        { appPathSource = "running process"; return file; }
                    }
                    catch { }
                }
            }
            catch { }

            // 3) 注册表卸载信息：InstallLocation / DisplayIcon / UninstallString 都可能带路径
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (var sub in new[]
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
                })
                {
                    try
                    {
                        using (var uninstall = root.OpenSubKey(sub))
                        {
                            if (uninstall == null) continue;
                            foreach (var name in uninstall.GetSubKeyNames())
                            {
                                using (var key = uninstall.OpenSubKey(name))
                                {
                                    if (key == null) continue;
                                    var display = (key.GetValue("DisplayName") as string) ?? string.Empty;
                                    if (display.IndexOf("云音乐", StringComparison.Ordinal) < 0 &&
                                        display.IndexOf("CloudMusic", StringComparison.OrdinalIgnoreCase) < 0 &&
                                        display.IndexOf("NetEase", StringComparison.OrdinalIgnoreCase) < 0) continue;

                                    var location = key.GetValue("InstallLocation") as string;
                                    if (!string.IsNullOrEmpty(location))
                                    {
                                        var exe = Path.Combine(location, "cloudmusic.exe");
                                        if (File.Exists(exe)) { appPathSource = "registry InstallLocation"; return exe; }
                                    }
                                    foreach (var valueName in new[] { "DisplayIcon", "UninstallString" })
                                    {
                                        var exe = ExeFromCommandLine(key.GetValue(valueName) as string);
                                        if (exe != null) { appPathSource = "registry " + valueName; return exe; }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            // 4) 常见安装位置（用环境变量拼，不写死盘符）
            foreach (var p in CommonCandidates())
                if (File.Exists(p)) { appPathSource = "common location"; return p; }

            // 5) 兜底：在常见根目录下浅层搜索
            var found = SearchForClient();
            if (found != null) appPathSource = "directory scan";
            return found;
        }

        /// <summary>从 "C:\...\cloudmusic.exe" 或带参数的命令行里取出 cloudmusic.exe 的完整路径。</summary>
        private static string ExeFromCommandLine(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var text = raw.Trim();
            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                var end = text.IndexOf('"', 1);
                if (end > 1) text = text.Substring(1, end - 1);
            }
            else
            {
                var exeAt = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exeAt > 0) text = text.Substring(0, exeAt + 4);
                var space = text.IndexOf(' ');
                if (space > 0) text = text.Substring(0, space);
            }
            try
            {
                var dir = Path.GetDirectoryName(text.Trim());
                if (string.IsNullOrEmpty(dir)) return null;
                var candidate = Path.Combine(dir, "cloudmusic.exe");
                if (File.Exists(candidate)) return candidate;
                if (File.Exists(text.Trim())) return text.Trim();
            }
            catch { }
            return null;
        }

        private static IEnumerable<string> CommonCandidates()
        {
            var roots = new List<string>();
            foreach (var f in new[]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.LocalApplicationData
            })
            {
                var root = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(root)) roots.Add(root);
            }
            foreach (var root in roots)
            {
                yield return Path.Combine(root, "NetEase", "CloudMusic", "cloudmusic.exe");
                yield return Path.Combine(root, "CloudMusic", "cloudmusic.exe");
                yield return Path.Combine(root, "Programs", "NetEase", "CloudMusic", "cloudmusic.exe");
            }
        }

        /// <summary>最后手段：在系统盘/其它固定盘以及常见根目录下浅层查找 cloudmusic.exe。</summary>
        private static string SearchForClient()
        {
            var roots = new List<string>();
            foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                var root = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(root)) roots.Add(root);
            }
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    var r = d.RootDirectory.FullName;
                    if (!roots.Contains(r)) roots.Add(r);
                }
            }
            catch { }

            foreach (var root in roots)
            {
                try
                {
                    foreach (var level1 in Directory.GetDirectories(root))
                    {
                        var direct = Path.Combine(level1, "cloudmusic.exe");
                        if (File.Exists(direct)) return direct;
                        foreach (var level2 in Directory.GetDirectories(level1))
                        {
                            var exe = Path.Combine(level2, "cloudmusic.exe");
                            if (File.Exists(exe)) return exe;
                        }
                    }
                }
                catch { }
            }
            return null;
        }

        private static bool PortOpen(int p)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var task = client.ConnectAsync("127.0.0.1", p);
                    return task.Wait(400) && client.Connected;
                }
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- 注入循环

        private static async Task InjectorLoop()
        {
            Log("injector loop started");
            int idleTicks = 0;
            while (running && DateTime.UtcNow < stopAt)
            {
                string wsUrl = null;
                try { wsUrl = FindPageWebSocketUrl(); } catch { }
                if (wsUrl == null)
                {
                    // 一次性模式下，客户端已经退出就没必要继续等了
                    if (exitWhenClientStops && ++idleTicks % 5 == 0 &&
                        Process.GetProcessesByName("cloudmusic").Length == 0)
                    {
                        Log("client is gone, stopping");
                        break;
                    }
                    await Task.Delay(2000);
                    continue;
                }
                idleTicks = 0;
                try { await RunSession(wsUrl); }
                catch (Exception ex) { Log("session ended: " + ex.Message); }
                await Task.Delay(1500);
            }
        }

        private static string FindPageWebSocketUrl()
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/json/list");
            request.Timeout = 2000;
            request.Proxy = null;
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                var json = reader.ReadToEnd();
                foreach (var chunk in json.Split(new[] { "},{" }, StringSplitOptions.None))
                {
                    if (chunk.IndexOf("\"page\"", StringComparison.Ordinal) < 0) continue;
                    var match = Regex.Match(chunk, "\"webSocketDebuggerUrl\"\\s*:\\s*\"([^\"]+)\"");
                    if (match.Success) return match.Groups[1].Value.Replace("\\/", "/");
                }
            }
            return null;
        }

        private static async Task RunSession(string wsUrl)
        {
            using (var ws = new ClientWebSocket())
            {
                await ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
                Log("attached: " + Regex.Replace(wsUrl, "^ws://[^/]+", ""));

                await Send(ws, "{\"id\":" + messageId++ + ",\"method\":\"Runtime.enable\"}");
                await Send(ws, "{\"id\":" + messageId++ + ",\"method\":\"Page.enable\"}");
                await Inject(ws);

                var cts = new CancellationTokenSource();
                var ticker = Task.Run(async () =>
                {
                    while (!cts.IsCancellationRequested)
                    {
                        // 页面跳转/刷新，或用户在托盘里点了"立即重新注入"时，不用等满 3 秒
                        int delay = manualInject ? 0 : 3000;
                        manualInject = false;
                        try { await Task.Delay(delay, cts.Token); } catch { break; }
                        if (!running || DateTime.UtcNow >= stopAt) break;
                        try { await Inject(ws); } catch { break; }
                    }
                });

                var buffer = new byte[32768];
                var sb = new StringBuilder();
                try
                {
                    while (running && ws.State == WebSocketState.Open && DateTime.UtcNow < stopAt)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                        if (!result.EndOfMessage) continue;
                        var message = sb.ToString();
                        sb.Length = 0;
                        HandleMessage(message);
                    }
                }
                finally { cts.Cancel(); }
                Log("detached");
            }
        }

        private static void HandleMessage(string message)
        {
            if (message.IndexOf("applied vw=", StringComparison.Ordinal) >= 0)
            {
                var match = Regex.Match(message, "\"value\"\\s*:\\s*\"(applied[^\"]*)\"");
                if (match.Success)
                {
                    Log("inject  " + match.Groups[1].Value);
                    // closeVisible=false 通常意味着界面类名变了（客户端大版本更新），修复没生效
                    if (match.Groups[1].Value.IndexOf("closeVisible=false", StringComparison.Ordinal) >= 0)
                        Log("WARNING: 修复似乎没有生效（关闭按钮仍不可见），客户端可能已更新，需要更新 css/layout-fix.css 的选择器");
                }
                return;
            }
            if (message.IndexOf("Page.loadEventFired", StringComparison.Ordinal) >= 0 ||
                message.IndexOf("Page.frameNavigated", StringComparison.Ordinal) >= 0 ||
                message.IndexOf("Runtime.executionContextsCleared", StringComparison.Ordinal) >= 0)
            {
                manualInject = true; // 下一轮 ticker 会立刻重新注入
            }
        }

        private static async Task Inject(ClientWebSocket ws)
        {
            var payload = "{\"id\":" + messageId++ + ",\"method\":\"Runtime.evaluate\",\"params\":{\"expression\":"
                + JsonString(BuildExpression()) + ",\"returnByValue\":true}}";
            await Send(ws, payload);
        }

        private static string BuildExpression()
        {
            return "(()=>{const css=" + JsonString(cssText) + ";"
                + "let s=document.getElementById('" + StyleId + "');"
                + "if(!s){s=document.createElement('style');s.id='" + StyleId + "';"
                + "(document.head||document.documentElement).appendChild(s);}"
                + "if(s.textContent!==css)s.textContent=css;"
                + "const op=document.querySelector('[class*=\"WindowOpBarContainer\"]');"
                + "const ok=op?op.getBoundingClientRect().right<=innerWidth+1:null;"
                + "return 'applied vw='+innerWidth+' dpr='+devicePixelRatio+' closeVisible='+ok;})()";
        }

        private static async Task Send(ClientWebSocket ws, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await SendLock.WaitAsync();
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally { SendLock.Release(); }
        }

        private static string JsonString(string value)
        {
            var sb = new StringBuilder(value.Length + 16);
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ---------------------------------------------------------------- 托盘与工具

        private static void RunTray()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            tray = new NotifyIcon { Text = "网易云音乐多屏布局修复", Visible = true };
            try
            {
                tray.Icon = Icon.ExtractAssociatedIcon(appPath) ?? SystemIcons.Application;
            }
            catch { tray.Icon = SystemIcons.Application; }

            var menu = new ContextMenuStrip();
            menu.Items.Add("打开日志", null, delegate { try { Process.Start("notepad.exe", logPath); } catch { } });
            menu.Items.Add("立即重新注入", null, delegate { manualInject = true; });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate
            {
                running = false;
                tray.Visible = false;
                Application.ExitThread();
            });
            tray.ContextMenuStrip = menu;
            tray.ShowBalloonTip(3000, "网易云音乐多屏布局修复",
                "已启动，正在守护多屏布局。右键托盘图标可查看日志或退出。", ToolTipIcon.Info);

            Application.Run();
            tray.Visible = false;
        }

        private static string LoadResource(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) throw new InvalidOperationException("missing embedded resource: " + name);
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                    return reader.ReadToEnd();
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            foreach (var a in args) if (a == flag) return true;
            return false;
        }

        private static void Log(string message)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message;
            lock (LogLock)
            {
                try { File.AppendAllText(logPath, line + Environment.NewLine, new UTF8Encoding(false)); }
                catch { }
            }
        }
    }
}
