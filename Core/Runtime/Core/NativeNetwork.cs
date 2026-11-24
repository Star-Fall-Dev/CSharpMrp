using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private readonly Dictionary<int, Socket> networkSockets = new Dictionary<int, Socket>();
        private readonly Dictionary<int, IAsyncResult> connectingSockets = new Dictionary<int, IAsyncResult>();
        private readonly Dictionary<int, uint> socketStates = new Dictionary<int, uint>();
        private readonly HashSet<int> cmwapProxySockets = new HashSet<int>();
        private readonly HashSet<int> cmwapStartedSockets = new HashSet<int>();
        private readonly Dictionary<int, (Task<IPAddress[]> Task, long Deadline, ushort Port)> proxyDns =
            new Dictionary<int, (Task<IPAddress[]> Task, long Deadline, ushort Port)>();
        private readonly Dictionary<int, SimplifiedWebExchange> simplifiedWeb = new Dictionary<int, SimplifiedWebExchange>();
        public bool SimplifiedWebPages { get; set; }
        public bool SimplifiedUcPages { get; set; }
        private bool cmwapMode;
        private int nextSocket = 1;
        private bool networkOpen;
        private Task<IPAddress[]>? pendingDns;
        private uint pendingInitCallback;
        private ScriptTable? pendingInitTable;
        private int pendingInitGeneration;
        private uint pendingDnsCallback;
        private ScriptTable? pendingDnsTable;
        private int networkGeneration;
        private int pendingDnsGeneration;
        private long pendingDnsDeadline;
        private readonly int platformThread = System.Threading.Thread.CurrentThread.ManagedThreadId;

        private uint NetworkDispatch(int slot, ArmCpu cpu)
        {
            uint arg(int index) => cpu.ReadArgument(index);
            if (!hostNetwork)
            {
                Record($"network unavailable: slot {slot} returns MR_FAILED=-1; no callback or host connection");
                return uint.MaxValue;
            }
            if (slot == 81)
            {
                string mode = arg(1) == 0 ? "cmnet" : Memory.CString(arg(1));
                return BeginNetwork(arg(0), null, mode);
            }
            if (slot == 82)
            {
                CloseNetwork();
                Record("host network closed");
                return 0;
            }
            if (!networkOpen)
            {
                return uint.MaxValue;
            }
            try
            {
                switch (slot)
                {
                    case 83:
                        return ResolveHost(Memory.CString(arg(0)), arg(1));
                    case 84:
                        return CreateSocket((int)arg(0), (int)arg(1));
                    case 85:
                        return ConnectSocket((int)arg(0), arg(1), (ushort)arg(2), (int)arg(3));
                    case 86:
                        return CloseSocket((int)arg(0));
                    case 87:
                        return ReceiveSocket((int)arg(0), arg(1), arg(2), false, 0, 0);
                    case 88:
                        return ReceiveSocket((int)arg(0), arg(1), arg(2), true, arg(3), arg(4));
                    case 89:
                        return SendSocket((int)arg(0), arg(1), arg(2), false, 0, 0);
                    case 90:
                        return SendSocket((int)arg(0), arg(1), arg(2), true, arg(3), (ushort)arg(4));
                }
            }
            catch (SocketException error)
            {
                Record($"socket {slot}: {error.SocketErrorCode}");
                return uint.MaxValue;
            }
            catch (ObjectDisposedException)
            {
                return uint.MaxValue;
            }
            return uint.MaxValue;
        }

        private uint BeginNetwork(uint callback, ScriptTable? table, string mode)
        {
            if (!hostNetwork)
            {
                return uint.MaxValue;
            }
            if (pendingInitCallback != 0 || pendingInitTable != null)
            {
                Record("host network initialization already pending");
                return uint.MaxValue;
            }
            networkOpen = true;
            cmwapMode = mode.StartsWith("cmwap", StringComparison.OrdinalIgnoreCase);
            if (callback == 0 && table == null)
            {
                Record("host network initialized synchronously: " + mode);
                return 0;
            }
            pendingInitCallback = callback;
            pendingInitTable = table;
            pendingInitGeneration = networkGeneration;
            Record("host network initialization queued: " + mode);
            return 2;
        }

        private uint ResolveHost(string name, uint callback, ScriptTable? table = null)
        {
            if (name.Length == 0 || name.Length > 253)
            {
                return uint.MaxValue;
            }
            bool asynchronous = callback != 0 || table != null;
            if (asynchronous && pendingDns != null) return uint.MaxValue;
            if (IPAddress.TryParse(name, out IPAddress? address) && !asynchronous)
            {
                return address.AddressFamily == AddressFamily.InterNetwork ? HostIp(address) : uint.MaxValue;
            }
            Task<IPAddress[]> task;
            try
            {
                if (address != null)
                {
                    if (address.AddressFamily != AddressFamily.InterNetwork) return uint.MaxValue;
                    task = Task.FromResult(new[] { address });
                }
                else
                {
                    task = Dns.GetHostAddressesAsync(name);
                    // A closed session cannot cancel this OS lookup on the Unity target API.
                    // Observe a late failure even after the guest callback is discarded.
                    _ = task.ContinueWith(static completed => { _ = completed.Exception; },
                        TaskContinuationOptions.OnlyOnFaulted);
                }
            }
            catch (Exception error) when (error is SocketException || error is ArgumentException)
            {
                Record($"DNS start failed: {error.GetType().Name}");
                return uint.MaxValue;
            }
            if (callback == 0 && table == null)
            {
                try
                {
                    task.Wait(3000);
                }
                catch (AggregateException)
                {
                    // ResolveDnsResult records the underlying host error.
                }
                if (!task.IsCompleted)
                {
                    Record("DNS synchronous lookup timed out after 3000 ms");
                    return uint.MaxValue;
                }
                return ResolveDnsResult(task);
            }
            pendingDnsCallback = callback;
            pendingDnsTable = table;
            pendingDnsGeneration = networkGeneration;
            pendingDnsDeadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3;
            pendingDns = task;
            return 2;
        }

        private uint ResolveDnsResult(Task<IPAddress[]> task)
        {
            try
            {
                foreach (IPAddress address in task.GetAwaiter().GetResult())
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork) return HostIp(address);
                }
                Record("DNS returned no IPv4 address");
            }
            catch (Exception error) when (error is SocketException || error is ArgumentException ||
                error is OperationCanceledException)
            {
                Record($"DNS failed: {error.GetType().Name}");
            }
            return uint.MaxValue;
        }

        public void PumpPlatform()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != platformThread)
            {
                throw new RuntimeFault("platform callbacks require the owning runtime thread");
            }
            Live();
            PumpSmsResult();
            if (disposed || exitRequested)
            {
                return;
            }
            int generationAtPump = networkGeneration;
            uint initCallback = pendingInitCallback;
            ScriptTable? initTable = pendingInitTable;
            if (initCallback != 0 || initTable != null)
            {
                int generation = pendingInitGeneration;
                pendingInitCallback = 0;
                pendingInitTable = null;
                if (networkOpen && generation == networkGeneration)
                {
                    if (initTable != null)
                    {
                        initTable["state"] = 2;
                    }
                    if (initCallback != 0)
                    {
                        InvokeGuestHook(initCallback, stackalloc uint[] { 0u });
                    }
                }
            }
            if (disposed || exitRequested || networkGeneration != generationAtPump)
            {
                return;
            }
            Task<IPAddress[]>? task = pendingDns;
            if (task == null || !task.IsCompleted && Stopwatch.GetTimestamp() < pendingDnsDeadline)
            {
                return;
            }
            pendingDns = null;
            uint callback = pendingDnsCallback;
            ScriptTable? table = pendingDnsTable;
            pendingDnsCallback = 0;
            pendingDnsTable = null;
            if (!networkOpen || pendingDnsGeneration != networkGeneration)
            {
                return;
            }
            uint result = uint.MaxValue;
            if (task.IsCompleted)
            {
                result = ResolveDnsResult(task);
            }
            else
            {
                Record("DNS asynchronous lookup timed out after 3000 ms");
            }
            if (table != null) table["ip"] = unchecked((int)result);
            if (callback != 0) InvokeGuestHook(callback, stackalloc uint[] { result });
        }

        private uint CreateSocket(int type, int protocol)
        {
            if ((type != 0 || protocol != 0) && (type != 1 || protocol != 1))
            {
                return uint.MaxValue;
            }
            try
            {
                Socket socket = new Socket(AddressFamily.InterNetwork,
                    type == 0 ? SocketType.Stream : SocketType.Dgram,
                    type == 0 ? ProtocolType.Tcp : ProtocolType.Udp) { Blocking = false };
                int handle = nextSocket++;
                networkSockets.Add(handle, socket);
                socketStates.Add(handle, 2);
                return (uint)handle;
            }
            catch (SocketException error)
            {
                Record($"socket create: {error.SocketErrorCode}");
                return uint.MaxValue;
            }
        }

        private uint ConnectSocket(int handle, uint ip, ushort port, int type, bool proxyTarget = false)
        {
            if (type < 0 || type > 1 || !networkSockets.TryGetValue(handle, out Socket? socket) ||
                connectingSockets.ContainsKey(handle))
            {
                return uint.MaxValue;
            }
            if (socket.SocketType == SocketType.Stream && cmwapMode && ip == 0x0a0000ac && !proxyTarget)
            {
                cmwapProxySockets.Add(handle);
                socketStates[handle] = 0;
                return 0;
            }
            try
            {
                IAsyncResult connection = socket.BeginConnect(Endpoint(ip, port), null, null);
                if (type == 1 && !connection.IsCompleted)
                {
                    connectingSockets.Add(handle, connection);
                    return 2;
                }
                if (type == 0 && !connection.AsyncWaitHandle.WaitOne(3000))
                {
                    connection.AsyncWaitHandle.Close();
                    CloseSocket(handle);
                    return uint.MaxValue;
                }
                try
                {
                    socket.EndConnect(connection);
                    socketStates[handle] = 0;
                    return 0;
                }
                finally
                {
                    connection.AsyncWaitHandle.Close();
                }
            }
            catch (SocketException error)
            {
                socketStates[handle] = uint.MaxValue;
                Record($"socket connect: {error.SocketErrorCode}");
                return uint.MaxValue;
            }
        }

        private uint SocketState(int handle)
        {
            if (networkOpen && cmwapProxySockets.Contains(handle))
            {
                return socketStates[handle];
            }
            return TransportSocketState(handle);
        }

        private uint TransportSocketState(int handle)
        {
            if (!networkOpen || !networkSockets.TryGetValue(handle, out Socket? socket))
            {
                return uint.MaxValue;
            }
            if (connectingSockets.TryGetValue(handle, out IAsyncResult? connection))
            {
                if (!connection.IsCompleted) return 2;
                connectingSockets.Remove(handle);
                try
                {
                    socket.EndConnect(connection);
                    socketStates[handle] = 0;
                    return 0;
                }
                catch (SocketException error)
                {
                    socketStates[handle] = uint.MaxValue;
                    Record($"socket connect: {error.SocketErrorCode}");
                    return uint.MaxValue;
                }
                finally
                {
                    connection.AsyncWaitHandle.Close();
                }
            }
            return socketStates[handle];
        }

        private uint CloseSocket(int handle)
        {
            if (!networkSockets.TryGetValue(handle, out Socket? socket)) return uint.MaxValue;
            networkSockets.Remove(handle);
            socketStates.Remove(handle);
            cmwapProxySockets.Remove(handle);
            cmwapStartedSockets.Remove(handle);
            proxyDns.Remove(handle);
            if (simplifiedWeb.TryGetValue(handle, out SimplifiedWebExchange? web))
            {
                simplifiedWeb.Remove(handle);
                web.Dispose();
            }
            if (connectingSockets.TryGetValue(handle, out IAsyncResult? connection))
            {
                connectingSockets.Remove(handle);
                connection.AsyncWaitHandle.Close();
            }
            socket.Dispose();
            for (int index = networkUsers.Count - 1; index >= 0; index--)
            {
                ScriptUserdata user = networkUsers[index];
                if (Memory.U32(user.Pointer) != (uint)handle) continue;
                Memory.Put32(user.Pointer, uint.MaxValue);
                Memory.Put32(user.Pointer + 4, 3);
                networkUsers.RemoveAt(index);
            }
            return 0;
        }

        private uint BindSocket(int handle, ushort port)
        {
            if (!networkOpen || !networkSockets.TryGetValue(handle, out Socket? socket)) return uint.MaxValue;
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
                return 0;
            }
            catch (SocketException error)
            {
                Record($"socket bind: {error.SocketErrorCode}");
                return uint.MaxValue;
            }
        }

        private uint ReceiveSocket(int handle, uint pointer, uint length, bool datagram, uint ipOut, uint portOut)
        {
            if (length > 1024 * 1024 || !networkSockets.TryGetValue(handle, out Socket? socket))
            {
                return uint.MaxValue;
            }
            Memory.Check(pointer, length);
            if (datagram)
            {
                Memory.Check(ipOut, 4);
                Memory.Check(portOut, 2);
            }
            if (length == 0) return 0;
            if (!datagram && simplifiedWeb.TryGetValue(handle, out SimplifiedWebExchange? web))
            {
                uint result = web.Receive(Memory, pointer, (int)length, out string? summary);
                if (summary != null)
                {
                    Record(summary);
                }
                return result;
            }
            try
            {
                if (!socket.Poll(0, SelectMode.SelectRead))
                {
                    return 0;
                }
                byte[] bytes = new byte[(int)length];
                int count;
                if (datagram)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    count = socket.ReceiveFrom(bytes, ref from);
                    var peer = (IPEndPoint)from;
                    Memory.Put32(ipOut, HostIp(peer.Address));
                    Memory.Put16(portOut, (ushort)peer.Port);
                }
                else count = socket.Receive(bytes);
                if (count > 0)
                {
                    if (count != bytes.Length) Array.Resize(ref bytes, count);
                    Memory.Write(pointer, bytes);
                }
                return (uint)count;
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.WouldBlock)
            {
                return 0;
            }
            catch (SocketException error)
            {
                Record($"socket receive: {error.SocketErrorCode}");
                return uint.MaxValue;
            }
        }

        private uint SendSocket(int handle, uint pointer, uint length, bool datagram, uint ip, ushort port)
        {
            if (length > 1024 * 1024 || !networkSockets.TryGetValue(handle, out Socket? socket))
            {
                return uint.MaxValue;
            }
            byte[] bytes = Memory.Read(pointer, length);
            if (bytes.Length == 0) return 0;
            bool stream = !datagram && socket.SocketType == SocketType.Stream;
            if (stream && SimplifiedUcPages &&
                (simplifiedWeb.ContainsKey(handle) || UcWebPage.TryRequest(bytes, out _)))
            {
                if (socketStates[handle] == uint.MaxValue)
                {
                    return uint.MaxValue;
                }
                return SendSimplifiedWeb(handle, bytes, true);
            }
            if (stream && cmwapProxySockets.Contains(handle))
            {
                if (socketStates[handle] == uint.MaxValue)
                {
                    return uint.MaxValue;
                }
                if (SimplifiedWebPages && (simplifiedWeb.ContainsKey(handle) || SimplifiedWebExchange.IsRequestPrefix(bytes)))
                {
                    return SendSimplifiedWeb(handle, bytes, false);
                }
                if (!cmwapStartedSockets.Contains(handle))
                {
                    uint target = ResolveProxyTarget(handle, bytes, out uint targetIp, out ushort targetPort);
                    if (target == 2)
                    {
                        return 0;
                    }
                    if (target == uint.MaxValue)
                    {
                        socketStates[handle] = uint.MaxValue;
                        return uint.MaxValue;
                    }
                    cmwapStartedSockets.Add(handle);
                    uint connection = ConnectSocket(handle, targetIp, targetPort, 1, true);
                    return connection == uint.MaxValue ? uint.MaxValue : 0;
                }
                uint state = TransportSocketState(handle);
                if (state == 2) return 0;
                if (state != 0) return uint.MaxValue;
            }
            try
            {
                return (uint)(datagram ? socket.SendTo(bytes, Endpoint(ip, port)) : socket.Send(bytes));
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.WouldBlock)
            {
                return 0;
            }
            catch (SocketException error)
            {
                Record($"socket send: {error.SocketErrorCode}");
                return uint.MaxValue;
            }
        }

        private uint ResolveProxyTarget(int handle, byte[] request, out uint ip, out ushort port)
        {
            ip = 0;
            port = 0;
            if (proxyDns.TryGetValue(handle, out var lookup))
            {
                if (!lookup.Task.IsCompleted && Stopwatch.GetTimestamp() < lookup.Deadline)
                {
                    return 2;
                }
                proxyDns.Remove(handle);
                if (!lookup.Task.IsCompleted)
                {
                    Record("cmwap DNS timed out after 3000 ms");
                    return uint.MaxValue;
                }
                ip = ResolveDnsResult(lookup.Task);
                port = lookup.Port;
                return ip == uint.MaxValue ? uint.MaxValue : 0;
            }
            Uri? uri = ProxyRequestUri(request);
            if (uri == null)
            {
                Record("cmwap first send has no valid HTTP destination");
                return uint.MaxValue;
            }
            int requestedPort = uri.Port;
            if (requestedPort < 1 || requestedPort > 65535)
            {
                return uint.MaxValue;
            }
            port = (ushort)requestedPort;
            if (IPAddress.TryParse(uri.Host, out IPAddress? address))
            {
                if (address.AddressFamily != AddressFamily.InterNetwork)
                {
                    return uint.MaxValue;
                }
                ip = HostIp(address);
                return 0;
            }
            try
            {
                Task<IPAddress[]> task = Dns.GetHostAddressesAsync(uri.Host);
                // Unity cannot cancel the OS lookup; it must never write session state on completion.
                _ = task.ContinueWith(static completed =>
                {
                    _ = completed.Exception;
                }, TaskContinuationOptions.OnlyOnFaulted);
                proxyDns.Add(handle, (task, Stopwatch.GetTimestamp() + Stopwatch.Frequency * 3, port));
                return 2;
            }
            catch (Exception error) when (error is SocketException || error is ArgumentException)
            {
                Record($"cmwap DNS start failed: {error.GetType().Name}");
                return uint.MaxValue;
            }
        }

        private static Uri? ProxyRequestUri(byte[] request)
        {
            const string scheme = "http://";
            string headers = Encoding.ASCII.GetString(request, 0, Math.Min(request.Length, 32 * 1024));
            int lineEnd = headers.IndexOf('\n');
            string line = (lineEnd < 0 ? headers : headers.Substring(0, lineEnd)).TrimEnd('\r');
            int start = line.IndexOf(' ') + 1;
            int stop = line.IndexOf(' ', start);
            if (stop < 0) stop = line.Length;
            if (start == 0 || stop == start) return null;

            string target = line.Substring(start, stop - start);
            if (target[0] == '/')
            {
                if (lineEnd < 0 || target.StartsWith("//", StringComparison.Ordinal)) return null;
                string? host = ProxyRequestHost(headers, lineEnd + 1);
                if (string.IsNullOrEmpty(host) || host.IndexOfAny(new[] { '/', '\\', '?', '#', '@', ' ', '\t' }) >= 0)
                {
                    return null;
                }
                target = scheme + host;
            }
            if (!target.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
            int authorityEnd = target.IndexOfAny(new[] { '/', '?', '#' }, scheme.Length);
            if (authorityEnd < 0) authorityEnd = target.Length;
            // Uri can normalize a trailing colon away, including in a nested scheme.
            if (authorityEnd == scheme.Length || target[authorityEnd - 1] == ':') return null;
            return Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttp &&
                uri.Host.Length != 0 && uri.UserInfo.Length == 0 ? uri : null;
        }

        private static string? ProxyRequestHost(string headers, int start)
        {
            string? onlineHost = null, host = null;
            while (start < headers.Length)
            {
                int end = headers.IndexOf('\n', start);
                if (end < 0) break;
                string line = headers.Substring(start, end - start).TrimEnd('\r');
                if (line.Length == 0) return onlineHost ?? host;
                start = end + 1;
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string name = line.Substring(0, colon);
                if (name.Equals("X-Online-Host", StringComparison.OrdinalIgnoreCase))
                {
                    if (onlineHost != null) return null;
                    onlineHost = line.Substring(colon + 1).Trim(' ', '\t');
                }
                else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    if (host != null) return null;
                    host = line.Substring(colon + 1).Trim(' ', '\t');
                }
            }
            return null;
        }

        private uint SendSimplifiedWeb(int handle, byte[] bytes, bool uc)
        {
            if (!simplifiedWeb.TryGetValue(handle, out SimplifiedWebExchange? web))
            {
                if (simplifiedWeb.Count >= 4)
                {
                    Record("simplified web request limit reached: 4 live sockets");
                    socketStates[handle] = uint.MaxValue;
                    return uint.MaxValue;
                }
                web = new SimplifiedWebExchange(uc);
                simplifiedWeb.Add(handle, web);
            }
            uint result = web.Send(bytes);
            if (result == uint.MaxValue)
            {
                Record("simplified web request rejected: oversized headers or repeated request");
                socketStates[handle] = uint.MaxValue;
            }
            return result;
        }

        private void CloseNetwork()
        {
            networkOpen = false;
            cmwapMode = false;
            networkGeneration++;
            if (activeNetworkLibrary != null)
            {
                activeNetworkLibrary["state"] = 3;
                activeNetworkLibrary = null;
            }
            pendingDns = null;
            pendingInitCallback = 0;
            pendingInitTable = null;
            pendingDnsCallback = 0;
            pendingDnsTable = null;
            foreach (int handle in new List<int>(networkSockets.Keys)) CloseSocket(handle);
            foreach (ScriptUserdata user in networkUsers)
            {
                Memory.Put32(user.Pointer, uint.MaxValue);
                Memory.Put32(user.Pointer + 4, 3);
            }
            networkUsers.Clear();
        }

        private static IPEndPoint Endpoint(uint ip, ushort port) =>
            new IPEndPoint(new IPAddress(new[] { (byte)(ip >> 24), (byte)(ip >> 16), (byte)(ip >> 8), (byte)ip }), port);

        private static uint HostIp(IPAddress ip)
        {
            byte[] bytes = ip.GetAddressBytes();
            return (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
        }
    }
}
