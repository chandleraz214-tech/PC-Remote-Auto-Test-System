using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace PCAutoTestClient
{
    internal class Program
    {
        private static ClientWebSocket? _ws;
        private static bool _running = true;
        private static string _serverUrl = "ws://192.168.5.108:8765";  // Mac IP

        static async Task Main(string[] args)
        {
            string hostName = Dns.GetHostName();
            string ip = GetLocalIPAddress();

            Console.WriteLine("========================================");
            Console.WriteLine("   PC Auto Test - 客户端");
            Console.WriteLine("========================================");
            Console.WriteLine($"设备: {hostName}");
            Console.WriteLine($"华为 IP: {ip}");
            Console.WriteLine($"连接到: {_serverUrl}");
            Console.WriteLine("按 Ctrl+C 退出");
            Console.WriteLine("========================================");

            await ConnectLoop();

            while (_running)
            {
                await Task.Delay(1000);
            }
        }

        private static async Task ConnectLoop()
        {
            while (_running)
            {
                try
                {
                    await ConnectToServer();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebSocket] 连接失败: {ex.Message}");
                }

                if (_running)
                {
                    Console.WriteLine("[WebSocket] 5秒后重连...");
                    await Task.Delay(5000);
                }
            }
        }

        private static async Task ConnectToServer()
        {
            _ws = new ClientWebSocket();
            var uri = new Uri(_serverUrl);

            Console.WriteLine("[WebSocket] 正在连接...");
            await _ws.ConnectAsync(uri, CancellationToken.None);
            Console.WriteLine("[WebSocket] ✅ 已连接到 Mac 服务端");

            await SendMessage(new { type = "register", client = Environment.MachineName });
            await ReceiveLoop();
        }

        private static async Task ReceiveLoop()
        {
            byte[] buffer = new byte[4096];
            while (_ws != null && _ws.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                        Console.WriteLine("[WebSocket] 连接已关闭");
                        break;
                    }

                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Console.WriteLine($"[WebSocket] 收到: {message}");

                    try
                    {
                        var json = JsonConvert.DeserializeObject<dynamic>(message);
                        string? command = json?.command;
                        if (!string.IsNullOrEmpty(command))
                        {
                            Console.WriteLine($"[命令] 执行: {command}");
                            string resultText = ExecuteCommand(command);
                            await SendMessage(new { type = "result", result = resultText });
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[错误] 处理消息失败: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WebSocket] 接收错误: {ex.Message}");
                    break;
                }
            }

            if (_running)
            {
                Console.WriteLine("[WebSocket] 连接断开，将重连...");
                await ConnectLoop();
            }
        }

        private static async Task SendMessage(object obj)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            string json = JsonConvert.SerializeObject(obj);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private static string ExecuteCommand(string cmd)
        {
            try
            {
                string lower = cmd.ToLower();
                switch (lower)
                {
                    case "shutdown": PowerControl.Shutdown(); return "关机命令已执行";
                    case "restart": PowerControl.Restart(); return "重启命令已执行";
                    case "sleep": PowerControl.Sleep(); return "睡眠命令已执行";
                    case "memory":
                        var mem = MemoryInfo.GetMemoryInfo();
                        return JsonConvert.SerializeObject(mem);
                    case "test":
                        BurnInTestController.RunStandardTest(180);
                        return "标准压测 (3小时) 已启动";
                    case "customtest":
                        BurnInTestController.RunCustomTest15Min();
                        return "自定义压测 (15分钟) 已启动";
                    case "getresult":
                        return BurnInTestController.GetTestResult();
                    default:
                        return "未知命令";
                }
            }
            catch (Exception ex)
            {
                return $"执行失败: {ex.Message}";
            }
        }

        private static string GetLocalIPAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                        return ip.ToString();
            }
            catch { }
            return "127.0.0.1";
        }
    }

    // ========== 功能类 ==========
    public static class PowerControl
    {
        public static void Shutdown()
        {
            try
            {
                Process.Start("shutdown", "/s /t 5");
                Console.WriteLine("[信息] 关机");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 关机失败: {ex.Message}");
            }
        }
        public static void Restart()
        {
            try
            {
                Process.Start("shutdown", "/r /t 5");
                Console.WriteLine("[信息] 重启");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 重启失败: {ex.Message}");
            }
        }
        public static void Sleep()
        {
            try
            {
                Process.Start("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0");
                Console.WriteLine("[信息] 睡眠");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 睡眠失败: {ex.Message}");
            }
        }
    }

    public static class MemoryInfo
    {
        public static List<Dictionary<string, object>> GetMemoryInfo()
        {
            var list = new List<Dictionary<string, object>>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PhysicalMemory"))
                {
                    foreach (ManagementObject mem in searcher.Get())
                    {
                        var dict = new Dictionary<string, object>
                        {
                            ["Capacity"] = Convert.ToInt64(mem["Capacity"]) / 1024 / 1024 / 1024,
                            ["Speed"] = mem["Speed"],
                            ["PartNumber"] = mem["PartNumber"]?.ToString()?.Trim() ?? "",
                            ["SerialNumber"] = mem["SerialNumber"]?.ToString()?.Trim() ?? "",
                            ["Manufacturer"] = mem["Manufacturer"]?.ToString()?.Trim() ?? ""
                        };
                        list.Add(dict);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 获取内存信息失败: {ex.Message}");
            }
            return list;
        }
    }

    public static class BurnInTestController
    {
        private const string BIT_PATH = @"C:\Program Files\BurnInTest\bit.exe";
        private const string REPORT_DIR = @"D:\log\RAMTest\";

        public static void RunStandardTest(int minutes = 180)
        {
            try
            {
                Process.Start(BIT_PATH, $"/r /D {minutes}");
                Console.WriteLine($"[压测] 标准测试 {minutes}分钟");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 启动压测失败: {ex.Message}");
            }
        }

        public static void RunCustomTest15Min()
        {
            try
            {
                string args = $@"/C ""D:\log\CX-test.bitcfg"" /D 15 /R ""{REPORT_DIR}"" /AutoExit";
                Process.Start(BIT_PATH, args);
                Console.WriteLine("[压测] 自定义测试 15分钟");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[错误] 启动自定义压测失败: {ex.Message}");
            }
        }

        public static string GetTestResult()
        {
            try
            {
                if (!Directory.Exists(REPORT_DIR))
                    return $"{{\"status\":\"error\",\"message\":\"报告目录不存在: {REPORT_DIR}\"}}";

                var files = Directory.GetFiles(REPORT_DIR, "BIT_log*.htm");
                if (files.Length == 0)
                    return $"{{\"status\":\"error\",\"message\":\"未找到报告文件\"}}";

                var latestFile = files.OrderByDescending(f => File.GetLastWriteTime(f)).First();
                string html = File.ReadAllText(latestFile);

                string cpu = ExtractResult(html, "CPU");
                string memory = ExtractResult(html, "Memory");
                string gpu2d = ExtractResult(html, "2D Graphics");
                string gpu3d = ExtractResult(html, "3D Graphics");
                string overall = ExtractOverall(html);

                var result = new
                {
                    status = "ok",
                    file = Path.GetFileName(latestFile),
                    results = new
                    {
                        CPU = string.IsNullOrEmpty(cpu) ? "Not found" : cpu,
                        Memory = string.IsNullOrEmpty(memory) ? "Not found" : memory,
                        TwoDGraphics = string.IsNullOrEmpty(gpu2d) ? "Not found" : gpu2d,
                        ThreeDGraphics = string.IsNullOrEmpty(gpu3d) ? "Not found" : gpu3d,
                        Overall = string.IsNullOrEmpty(overall) ? "Not found" : overall
                    }
                };
                return JsonConvert.SerializeObject(result);
            }
            catch (Exception ex)
            {
                return $"{{\"status\":\"error\",\"message\":\"{ex.Message}\"}}";
            }
        }

        private static string ExtractResult(string html, string testName)
        {
            string text = Regex.Replace(html, @"<[^>]+>", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();

            string pattern = $@"{Regex.Escape(testName)}[^A-Za-z]*(PASS|FAIL)";
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
                return match.Groups[1].Value.ToUpper();

            var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.IndexOf(testName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var m = Regex.Match(line, @"(PASS|FAIL)", RegexOptions.IgnoreCase);
                    if (m.Success)
                        return m.Groups[1].Value.ToUpper();
                }
            }
            return null;
        }

        private static string ExtractOverall(string html)
        {
            string text = Regex.Replace(html, @"<[^>]+>", " ");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            string pattern = @"TEST\s*RUN\s*(PASSED|FAILED)";
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
                return match.Groups[1].Value.ToUpper();
            return null;
        }
    }
}