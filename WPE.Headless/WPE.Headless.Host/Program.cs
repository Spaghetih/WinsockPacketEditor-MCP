using EasyHook;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace WPE.Headless.Host
{
    /// <summary>
    /// Host CLI: injects WPE.Headless.Inject.dll into a target PID,
    /// connects to the named pipe, and bridges packets to stdout / commands from stdin
    /// using a tiny JSON-RPC line protocol.
    /// Methods: list_processes, inject_process, get_packets, send_packet,
    ///          get_stats, reset, stop_capture, ping
    /// </summary>
    internal static class Program
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly object StdoutLock = new object();

        // PID -> session
        private static readonly ConcurrentDictionary<int, Session> Sessions = new ConcurrentDictionary<int, Session>();

        private static int Main(string[] args)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                if (args.Length > 0 && args[0] == "--version")
                {
                    Console.WriteLine("WPE.Headless.Host 1.0.0");
                    return 0;
                }

                Emit(new { type = "ready", host = "WPE.Headless.Host", version = "1.0.0" });

                string line;
                while ((line = Console.In.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    HandleRequest(line);
                }
            }
            catch (Exception ex)
            {
                Emit(new { type = "fatal", message = ex.ToString() });
                return 1;
            }
            return 0;
        }

        private static void HandleRequest(string line)
        {
            Dictionary<string, object> req;
            try { req = (Dictionary<string, object>)Json.DeserializeObject(line); }
            catch (Exception ex)
            {
                Emit(new { type = "error", id = (object)null, message = "invalid json: " + ex.Message });
                return;
            }

            object id = req.TryGetValue("id", out var idv) ? idv : null;
            string method = req.TryGetValue("method", out var mv) ? Convert.ToString(mv) : null;
            var p = req.TryGetValue("params", out var pv) ? pv as Dictionary<string, object> : new Dictionary<string, object>();

            try
            {
                switch (method)
                {
                    case "ping":
                        Reply(id, new { ok = true });
                        break;
                    case "list_processes":
                        Reply(id, ListProcesses(p));
                        break;
                    case "inject_process":
                        Reply(id, InjectProcess(p));
                        break;
                    case "get_packets":
                        Reply(id, GetPackets(p));
                        break;
                    case "send_packet":
                        Reply(id, SendPacket(p));
                        break;
                    case "get_stats":
                        Reply(id, GetStats(p));
                        break;
                    case "reset":
                        Reply(id, Reset(p));
                        break;
                    case "stop_capture":
                        Reply(id, StopCapture(p));
                        break;
                    default:
                        Reply(id, error: "unknown method: " + method);
                        break;
                }
            }
            catch (Exception ex)
            {
                Reply(id, error: ex.Message);
            }
        }

        // ---------- methods ----------

        private static object ListProcesses(Dictionary<string, object> p)
        {
            string filter = p != null && p.TryGetValue("filter", out var f) ? Convert.ToString(f) : null;
            var list = Process.GetProcesses()
                .Where(pr => string.IsNullOrEmpty(filter) || pr.ProcessName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(pr =>
                {
                    string path = null;
                    try { path = pr.MainModule?.FileName; } catch { }
                    return new { pid = pr.Id, name = pr.ProcessName, path = path };
                })
                .ToArray();
            return new { processes = list };
        }

        private static object InjectProcess(Dictionary<string, object> p)
        {
            int pid = Convert.ToInt32(p["pid"]);
            string injectDll = p.TryGetValue("inject_dll", out var dllv)
                ? Convert.ToString(dllv)
                : Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "WPE.Headless.Inject.dll");

            if (!File.Exists(injectDll))
                throw new FileNotFoundException("inject DLL not found: " + injectDll);

            string pipeName = "WPE_Headless_" + pid + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string channel = "WPE_Headless_" + pid;

            RemoteHooking.Inject(pid, injectDll, injectDll, channel, pipeName);

            var session = new Session(pid, pipeName);
            Sessions[pid] = session;
            session.Start();

            return new { pid = pid, pipe = pipeName, status = "injecting" };
        }

        private static object GetPackets(Dictionary<string, object> p)
        {
            int pid = Convert.ToInt32(p["pid"]);
            int max = p != null && p.TryGetValue("max", out var mv) ? Convert.ToInt32(mv) : 100;
            if (!Sessions.TryGetValue(pid, out var s)) throw new InvalidOperationException("no session for pid " + pid);
            var packets = s.DrainPackets(max);
            return new { pid = pid, count = packets.Count, packets = packets };
        }

        private static object SendPacket(Dictionary<string, object> p)
        {
            int pid = Convert.ToInt32(p["pid"]);
            int sock = Convert.ToInt32(p["socket"]);
            string kind = p.TryGetValue("kind", out var kv) ? Convert.ToString(kv) : "WS2_Send";
            string dataB64 = Convert.ToString(p["data"]);
            if (!Sessions.TryGetValue(pid, out var s)) throw new InvalidOperationException("no session for pid " + pid);
            s.SendCommand(new { cmd = "send", socket = sock.ToString(), kind = kind, data = dataB64 });
            return new { pid = pid, queued = true };
        }

        private static object GetStats(Dictionary<string, object> p)
        {
            int pid = Convert.ToInt32(p["pid"]);
            if (!Sessions.TryGetValue(pid, out var s)) throw new InvalidOperationException("no session for pid " + pid);
            s.SendCommand(new { cmd = "stats" });
            var stats = s.WaitForEvent("stats", 1500);
            return new { pid = pid, stats = stats };
        }

        private static object Reset(Dictionary<string, object> p)
        {
            int pid = Convert.ToInt32(p["pid"]);
            if (!Sessions.TryGetValue(pid, out var s)) throw new InvalidOperationException("no session for pid " + pid);
            s.SendCommand(new { cmd = "reset" });
            s.ClearBuffered();
            return new { pid = pid, ok = true };
        }

        private static object StopCapture(Dictionary<string, object> p)
        {
            int pid = Convert.ToInt32(p["pid"]);
            if (!Sessions.TryRemove(pid, out var s)) throw new InvalidOperationException("no session for pid " + pid);
            s.SendCommand(new { cmd = "stop" });
            s.Dispose();
            return new { pid = pid, ok = true };
        }

        // ---------- protocol helpers ----------

        private static void Reply(object id, object result = null, string error = null)
        {
            if (error != null) Emit(new { id = id, error = error });
            else Emit(new { id = id, result = result });
        }

        private static void Emit(object obj)
        {
            string s = Json.Serialize(obj);
            lock (StdoutLock) Console.Out.WriteLine(s);
        }

        public static void EmitNotification(string type, object data)
        {
            Emit(new { type = "notification", channel = type, data = data });
        }

        // ---------- session ----------

        private sealed class Session : IDisposable
        {
            private readonly int _pid;
            private readonly string _pipeName;
            private NamedPipeClientStream _client;
            private StreamReader _reader;
            private StreamWriter _writer;
            private readonly object _writeLock = new object();
            private readonly ConcurrentQueue<object> _packets = new ConcurrentQueue<object>();
            private readonly ConcurrentDictionary<string, Queue<object>> _events = new ConcurrentDictionary<string, Queue<object>>();
            private CancellationTokenSource _cts;

            public Session(int pid, string pipeName) { _pid = pid; _pipeName = pipeName; }

            public void Start()
            {
                _cts = new CancellationTokenSource();
                Task.Run(() => Connect(_cts.Token));
            }

            private void Connect(CancellationToken ct)
            {
                try
                {
                    _client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    _client.Connect(15000);
                    _writer = new StreamWriter(_client, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    _reader = new StreamReader(_client, new UTF8Encoding(false));
                    EmitNotification("session", new { pid = _pid, status = "connected" });
                    ReadLoop(ct);
                }
                catch (Exception ex)
                {
                    EmitNotification("session", new { pid = _pid, status = "error", message = ex.Message });
                }
            }

            private void ReadLoop(CancellationToken ct)
            {
                string line;
                while (!ct.IsCancellationRequested && (line = _reader.ReadLine()) != null)
                {
                    Dictionary<string, object> obj;
                    try { obj = (Dictionary<string, object>)Json.DeserializeObject(line); }
                    catch { continue; }

                    string type = obj.TryGetValue("type", out var t) ? Convert.ToString(t) : null;
                    if (type == "packet") { _packets.Enqueue(obj); continue; }
                    var q = _events.GetOrAdd(type ?? "?", _ => new Queue<object>());
                    lock (q) q.Enqueue(obj.TryGetValue("data", out var d) ? d : obj);
                }
            }

            public List<object> DrainPackets(int max)
            {
                var list = new List<object>(Math.Min(max, 256));
                for (int i = 0; i < max && _packets.TryDequeue(out var p); i++) list.Add(p);
                return list;
            }

            public void ClearBuffered()
            {
                while (_packets.TryDequeue(out _)) { }
            }

            public void SendCommand(object cmd)
            {
                if (_writer == null) throw new InvalidOperationException("pipe not connected yet");
                lock (_writeLock) _writer.WriteLine(Json.Serialize(cmd));
            }

            public object WaitForEvent(string type, int timeoutMs)
            {
                int waited = 0;
                while (waited < timeoutMs)
                {
                    if (_events.TryGetValue(type, out var q))
                    {
                        lock (q) if (q.Count > 0) return q.Dequeue();
                    }
                    Thread.Sleep(20);
                    waited += 20;
                }
                return null;
            }

            public void Dispose()
            {
                try { _cts?.Cancel(); } catch { }
                try { _writer?.Dispose(); } catch { }
                try { _reader?.Dispose(); } catch { }
                try { _client?.Dispose(); } catch { }
            }
        }
    }
}
