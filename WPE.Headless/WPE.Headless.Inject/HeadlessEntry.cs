using EasyHook;
using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WPELibrary.Lib;

namespace WPE.Headless.Inject
{
    /// <summary>
    /// Headless EasyHook entry point: installs WinSock hooks and forwards
    /// captured packets to the host over a per-PID named pipe.
    /// No WinForms UI is created in the target process.
    /// </summary>
    public class HeadlessEntry : IEntryPoint
    {
        private readonly string _pipeName;
        private WinSockHook _hook;
        private NamedPipeServerStream _pipe;
        private StreamWriter _writer;
        private StreamReader _reader;
        private CancellationTokenSource _cts;
        private readonly object _writeLock = new object();

        public HeadlessEntry(RemoteHooking.IContext ctx, string channelName, string pipeName)
        {
            _pipeName = pipeName;
        }

        public void Run(RemoteHooking.IContext ctx, string channelName, string pipeName)
        {
            _cts = new CancellationTokenSource();
            try
            {
                ConfigureCache();
                Socket_Operation.GetWinSockSupportInfo();

                _hook = new WinSockHook();
                _hook.StartHook();

                RunPipeLoop();
            }
            catch (Exception ex)
            {
                TryWriteEvent("error", ex.ToString());
            }
        }

        private static void ConfigureCache()
        {
            Socket_Cache.SocketPacket.SpeedMode = false;
            Socket_Cache.SocketPacket.HookWS1_Send = true;
            Socket_Cache.SocketPacket.HookWS1_SendTo = true;
            Socket_Cache.SocketPacket.HookWS1_Recv = true;
            Socket_Cache.SocketPacket.HookWS1_RecvFrom = true;
            Socket_Cache.SocketPacket.HookWS2_Send = true;
            Socket_Cache.SocketPacket.HookWS2_SendTo = true;
            Socket_Cache.SocketPacket.HookWS2_Recv = true;
            Socket_Cache.SocketPacket.HookWS2_RecvFrom = true;
            Socket_Cache.SocketPacket.HookWSA_Send = true;
            Socket_Cache.SocketPacket.HookWSA_SendTo = true;
            Socket_Cache.SocketPacket.HookWSA_Recv = true;
            Socket_Cache.SocketPacket.HookWSA_RecvFrom = true;
        }

        private void RunPipeLoop()
        {
            _pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            _pipe.WaitForConnection();
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            _reader = new StreamReader(_pipe, new UTF8Encoding(false));

            TryWriteEvent("ready", "{\"pid\":" + System.Diagnostics.Process.GetCurrentProcess().Id + "}");

            var drainTask = Task.Run(() => DrainPacketsLoop(_cts.Token));
            ReadCommandsLoop(_cts.Token);

            _cts.Cancel();
            try { drainTask.Wait(1000); } catch { }
        }

        private void DrainPacketsLoop(CancellationToken ct)
        {
            var sb = new StringBuilder(256);
            while (!ct.IsCancellationRequested)
            {
                bool any = false;
                while (Socket_Cache.SocketQueue.qSocket_PacketInfo.TryDequeue(out var p))
                {
                    any = true;
                    sb.Length = 0;
                    sb.Append("{\"type\":\"packet\",");
                    sb.Append("\"time\":\"").Append(p.PacketTime.ToString("o")).Append("\",");
                    sb.Append("\"socket\":").Append(p.PacketSocket).Append(',');
                    sb.Append("\"kind\":\"").Append(p.PacketType).Append("\",");
                    sb.Append("\"from\":\"").Append(JsonEscape(p.PacketFrom)).Append("\",");
                    sb.Append("\"to\":\"").Append(JsonEscape(p.PacketTo)).Append("\",");
                    sb.Append("\"len\":").Append(p.PacketLen).Append(',');
                    sb.Append("\"action\":\"").Append(p.FilterAction).Append("\",");
                    sb.Append("\"data\":\"").Append(Convert.ToBase64String(p.PacketBuffer ?? new byte[0])).Append("\"}");
                    TryWriteLine(sb.ToString());
                }
                if (!any) Thread.Sleep(20);
            }
        }

        private void ReadCommandsLoop(CancellationToken ct)
        {
            string line;
            while (!ct.IsCancellationRequested && (line = SafeReadLine()) != null)
            {
                try
                {
                    HandleCommand(line);
                }
                catch (Exception ex)
                {
                    TryWriteEvent("error", JsonEscape(ex.Message));
                }
            }
        }

        private string SafeReadLine()
        {
            try { return _reader.ReadLine(); }
            catch { return null; }
        }

        private void HandleCommand(string line)
        {
            // Minimal parser: {"cmd":"<name>", ...}
            var cmd = ExtractJsonString(line, "cmd");
            switch (cmd)
            {
                case "ping":
                    TryWriteEvent("pong", "{}");
                    break;

                case "stats":
                    var stats = "{\"send\":" + Socket_Cache.SocketQueue.Send_CNT
                        + ",\"recv\":" + Socket_Cache.SocketQueue.Recv_CNT
                        + ",\"sendto\":" + Socket_Cache.SocketQueue.SendTo_CNT
                        + ",\"recvfrom\":" + Socket_Cache.SocketQueue.RecvFrom_CNT
                        + ",\"wsasend\":" + Socket_Cache.SocketQueue.WSASend_CNT
                        + ",\"wsarecv\":" + Socket_Cache.SocketQueue.WSARecv_CNT
                        + ",\"queued\":" + Socket_Cache.SocketQueue.qSocket_PacketInfo.Count + "}";
                    TryWriteEvent("stats", stats);
                    break;

                case "reset":
                    Socket_Cache.SocketQueue.ResetSocketQueue();
                    TryWriteEvent("reset", "{}");
                    break;

                case "send":
                    DoSendPacket(line);
                    break;

                case "stop":
                    _cts.Cancel();
                    break;
            }
        }

        // Local P/Invokes so we don't depend on WPELibrary's NativeMethods visibility
        // (WS2_32 is internal there). Sockets are process-local handles, so calling
        // these DllImports inside the injected process targets the same Winsock state.
        [DllImport("WS2_32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true, EntryPoint = "send")]
        private static extern int Ws2SendInternal(int s, IntPtr buf, int len, SocketFlags flags);

        [DllImport("WSOCK32.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true, EntryPoint = "send")]
        private static extern int Ws1SendInternal(int s, IntPtr buf, int len, SocketFlags flags);

        private void DoSendPacket(string line)
        {
            var sockStr = ExtractJsonString(line, "socket");
            var dataB64 = ExtractJsonString(line, "data");
            var typeStr = ExtractJsonString(line, "kind") ?? "WS2_Send";
            if (sockStr == null || dataB64 == null) return;

            int sock = int.Parse(sockStr);
            byte[] data = Convert.FromBase64String(dataB64);

            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, buf, data.Length);

                int sent = typeStr.StartsWith("WS1")
                    ? Ws1SendInternal(sock, buf, data.Length, SocketFlags.None)
                    : Ws2SendInternal(sock, buf, data.Length, SocketFlags.None);

                TryWriteEvent("send_ack", "{\"sent\":" + sent + "}");
            }
            catch (Exception ex)
            {
                TryWriteEvent("send_err", "{\"msg\":\"" + JsonEscape(ex.Message) + "\"}");
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }

        private void TryWriteEvent(string ev, string payload)
        {
            TryWriteLine("{\"type\":\"" + ev + "\",\"data\":" + payload + "}");
        }

        private void TryWriteLine(string s)
        {
            lock (_writeLock)
            {
                try { _writer?.WriteLine(s); } catch { }
            }
        }

        private static string JsonEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private static string ExtractJsonString(string json, string key)
        {
            // Naive scanner. Acceptable since host writes well-formed messages.
            var marker = "\"" + key + "\"";
            int i = json.IndexOf(marker);
            if (i < 0) return null;
            i = json.IndexOf(':', i);
            if (i < 0) return null;
            i++;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
            if (i >= json.Length) return null;
            if (json[i] == '"')
            {
                int end = ++i;
                var sb = new StringBuilder();
                while (end < json.Length)
                {
                    char c = json[end];
                    if (c == '\\' && end + 1 < json.Length) { sb.Append(json[end + 1]); end += 2; continue; }
                    if (c == '"') break;
                    sb.Append(c); end++;
                }
                return sb.ToString();
            }
            int e = i;
            while (e < json.Length && json[e] != ',' && json[e] != '}' && json[e] != ' ') e++;
            return json.Substring(i, e - i);
        }
    }
}
