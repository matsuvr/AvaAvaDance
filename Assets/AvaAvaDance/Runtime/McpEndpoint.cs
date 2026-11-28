using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AvaAvaDance
{
    // One authenticated call per connection. Only Pump may touch the live Unity document.
    internal sealed class McpEndpoint : IDisposable
    {
        sealed class Call
        {
            internal JObject Request;
            internal readonly TaskCompletionSource<JToken> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int StartedOrExpired;
        }

        readonly Func<JObject, Task<JToken>> dispatch;
        readonly ConcurrentQueue<Call> pending = new();
        readonly HashSet<TcpClient> clients = new();
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly string secret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        readonly string descriptor;
        readonly uint pid;
        volatile bool stopped;
        internal bool HasPending => !pending.IsEmpty;

        internal McpEndpoint(uint pid, bool editor, string appPath, Func<JObject, Task<JToken>> dispatch)
        {
            this.pid = pid;
            this.dispatch = dispatch;
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaAvaDance", "Mcp");
            Directory.CreateDirectory(folder);
            descriptor = Path.Combine(folder, pid + "-" + Guid.NewGuid().ToString("N") + ".json");
            listener.Start(8);
            try
            {
                var info = new { protocol = 1, pid, editor, appPath, token = secret, port = ((IPEndPoint)listener.LocalEndpoint).Port };
                File.WriteAllText(descriptor + ".tmp", JsonConvert.SerializeObject(info));
                File.Move(descriptor + ".tmp", descriptor);
                _ = Task.Run(Accept);
            }
            catch { listener.Stop(); File.Delete(descriptor + ".tmp"); throw; }
        }

        async Task Accept()
        {
            try
            {
                while (!stopped)
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    lock (clients)
                    {
                        if (stopped || clients.Count >= 16) { client.Close(); continue; }
                        clients.Add(client);
                    }
                    _ = Task.Run(() => Serve(client));
                }
            }
            catch (ObjectDisposedException) when (stopped) { }
            catch (SocketException) when (stopped) { }
        }

        void Serve(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = client.SendTimeout = 5000;
                using var stream = client.GetStream();
                using var reader = new BinaryReader(stream, Encoding.UTF8, true);
                if (Encoding.ASCII.GetString(reader.ReadBytes(64)) != secret) return;
                int size = reader.ReadInt32();
                if (size <= 0 || size > 4 * 1024 * 1024) return;
                byte[] bytes = reader.ReadBytes(size);
                if (bytes.Length != size) return;
                JObject reply;
                try
                {
                    var request = JObject.Parse(Encoding.UTF8.GetString(bytes));
                    JToken value;
                    if ((string)request["tool"] == "identify") value = new JObject { ["pid"] = pid };
                    else
                    {
                        var call = new Call { Request = request };
                        pending.Enqueue(call);
                        if (Task.WaitAny(new Task[] { call.Reply.Task }, 10000) < 0 &&
                            Interlocked.CompareExchange(ref call.StartedOrExpired, 1, 0) == 0)
                            throw new TimeoutException("Unity is busy or paused. Check native windows and resume the player.");
                        value = call.Reply.Task.GetAwaiter().GetResult();
                    }
                    reply = new JObject { ["result"] = value };
                }
                catch (Exception ex) { reply = new JObject { ["error"] = ex.Message }; }
                bytes = Encoding.UTF8.GetBytes(reply.ToString(Formatting.None));
                if (bytes.Length > 16 * 1024 * 1024)
                    bytes = Encoding.UTF8.GetBytes("{\"error\":\"Result exceeds 16 MiB. Use a smaller section or page.\"}");
                using var writer = new BinaryWriter(stream);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
            finally { lock (clients) clients.Remove(client); client.Close(); }
        }

        internal async void Pump()
        {
            while (pending.TryDequeue(out var call))
            {
                if (Interlocked.Exchange(ref call.StartedOrExpired, 1) != 0) continue;
                try { call.Reply.TrySetResult(await dispatch(call.Request)); }
                catch (Exception ex) { call.Reply.TrySetException(ex); }
                break;
            }
        }

        public void Dispose()
        {
            stopped = true;
            listener.Stop();
            lock (clients) foreach (var client in clients) client.Close();
            while (pending.TryDequeue(out var call)) call.Reply.TrySetCanceled();
            File.Delete(descriptor);
        }
    }
}
