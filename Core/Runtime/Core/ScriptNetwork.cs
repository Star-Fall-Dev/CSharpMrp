using System;
using System.Collections.Generic;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private readonly List<ScriptUserdata> networkUsers = new List<ScriptUserdata>();
        private ScriptTable? activeNetworkLibrary;
        private ScriptTable? tcpMethods;
        private ScriptTable? udpMethods;

        private void RegisterNetworkGlobals()
        {
            tcpMethods = SocketMethods(false);
            udpMethods = SocketMethods(true);
            scriptRegistry[tcpMethods] = "tcp";
            scriptRegistry[udpMethods] = "udp";
            Script.Globals["_initNet"] = new ScriptVm.Native(args =>
            {
                string mode = args.Length == 0 || args[0] == null ? "cmnet" : ScriptVm.Text(args[0]);
                if (networkOpen) CloseNetwork();
                int generation = networkGeneration;
                var library = new ScriptTable();
                library["tcp"] = new ScriptVm.Native(_ => generation == networkGeneration && networkOpen
                    ? NewScriptSocket(false) : new object?[] { null });
                library["udp"] = new ScriptVm.Native(_ => generation == networkGeneration && networkOpen
                    ? NewScriptSocket(true) : new object?[] { null });
                library["getHost"] = new ScriptVm.Native(values =>
                {
                    string name = TextArgument(values, 0);
                    uint ip = generation == networkGeneration && networkOpen
                        ? ResolveHost(name, 0, library) : uint.MaxValue;
                    library["ip"] = unchecked((int)ip);
                    return new object?[] { unchecked((int)ip) };
                });
                library["__gc"] = new ScriptVm.Native(_ =>
                {
                    if (generation == networkGeneration && ReferenceEquals(activeNetworkLibrary, library))
                    {
                        CloseNetwork();
                    }
                    library["state"] = 3;
                    return Array.Empty<object?>();
                });
                Script.RetainNativeValues((ScriptVm.Native)library["getHost"]!, library);
                Script.RetainNativeValues((ScriptVm.Native)library["__gc"]!, library);
                Script.Globals["socket"] = library;
                uint result = BeginNetwork(0, library, mode);
                library["state"] = result == 2 ? 1 : result == 0 ? 2 : 5;
                activeNetworkLibrary = result == uint.MaxValue ? null : library;
                return new object?[] { library };
            });
            Script.Globals["_closeNet"] = new ScriptVm.Native(_ =>
            {
                bool active = networkOpen;
                CloseNetwork();
                return new object?[] { active ? 0 : -1 };
            });
        }

        private object?[] NewScriptSocket(bool udp)
        {
            uint handle = networkOpen ? CreateSocket(udp ? 1 : 0, udp ? 1 : 0) : uint.MaxValue;
            if (handle == uint.MaxValue) return new object?[] { null };
            try
            {
                CheckScriptAllocation(8);
                uint pointer = Memory.AllocateVm(8);
                Memory.Put32(pointer, handle);
                Memory.Put32(pointer + 4, 0);
                var user = NewScriptUserdata(pointer, 8, udp ? udpMethods : tcpMethods);
                networkUsers.Add(user);
                return new object?[] { user };
            }
            catch
            {
                CloseSocket((int)handle);
                throw;
            }
        }

        private ScriptUserdata NetworkReceiver(object?[] args)
        {
            if (args.Length == 0 || !(args[0] is ScriptUserdata user) ||
                (user.Metatable != tcpMethods && user.Metatable != udpMethods))
            {
                throw new RuntimeFault("invalid script tcp/udp receiver");
            }
            return user;
        }

        private ScriptTable SocketMethods(bool udp)
        {
            var methods = new ScriptTable();
            methods["__index"] = methods;
            methods["close"] = new ScriptVm.Native(args =>
            {
                ScriptUserdata user = NetworkReceiver(args);
                uint handle = Memory.U32(user.Pointer);
                if (handle != uint.MaxValue) CloseSocket((int)handle);
                Memory.Put32(user.Pointer, uint.MaxValue);
                Memory.Put32(user.Pointer + 4, 3);
                return Array.Empty<object?>();
            });
            methods["__gc"] = methods["close"];
            methods["__str"] = new ScriptVm.Native(args =>
            {
                ScriptUserdata user = NetworkReceiver(args);
                return new object?[] { (udp ? "udp" : "tcp") + " (" + user.Pointer.ToString("X8") + ")" };
            });
            methods["getsock"] = new ScriptVm.Native(args =>
                new object?[] { unchecked((int)Memory.U32(NetworkReceiver(args).Pointer)) });
            methods["getstate"] = new ScriptVm.Native(args =>
            {
                ScriptUserdata user = NetworkReceiver(args);
                int state = (int)Memory.U32(user.Pointer + 4);
                if (state == 1)
                {
                    uint status = SocketState((int)Memory.U32(user.Pointer));
                    state = status == 0 ? 2 : status == uint.MaxValue ? 5 : 1;
                    Memory.Put32(user.Pointer + 4, (uint)state);
                }
                return new object?[] { state };
            });
            methods["getinfo"] = new ScriptVm.Native(args => new object?[]
            {
                new ScriptTable { ["state"] = (int)Memory.U32(NetworkReceiver(args).Pointer + 4) }
            });
            methods["bind"] = new ScriptVm.Native(args =>
            {
                ScriptUserdata user = NetworkReceiver(args);
                int port = Number(args, 1);
                return new object?[] { port < 0 || port > 65535 ? -1 :
                    unchecked((int)BindSocket((int)Memory.U32(user.Pointer), (ushort)port)) };
            });
            methods["connect"] = new ScriptVm.Native(args =>
            {
                ScriptUserdata user = NetworkReceiver(args);
                int port = Number(args, 2);
                if (port < 0 || port > 65535) return new object?[] { null };
                uint result = ConnectSocket((int)Memory.U32(user.Pointer), unchecked((uint)Number(args, 1)),
                    (ushort)port, Number(args, 3));
                int state = result == 0 ? 2 : result == 2 ? 1 : 5;
                Memory.Put32(user.Pointer + 4, (uint)state);
                return result == uint.MaxValue ? new object?[] { null } : new object?[] { (int)result };
            });
            methods["send"] = new ScriptVm.Native(args => SendScriptSocket(args, false));
            methods["receive"] = new ScriptVm.Native(args => ReceiveScriptSocket(args, false));
            if (udp)
            {
                methods["sendto"] = new ScriptVm.Native(args => SendScriptSocket(args, true));
                methods["receivefrom"] = new ScriptVm.Native(args => ReceiveScriptSocket(args, true));
            }
            return methods;
        }

        private object?[] SendScriptSocket(object?[] args, bool to)
        {
            ScriptUserdata user = NetworkReceiver(args);
            byte[] data = ScriptChunk.Bytes.GetBytes(TextArgument(args, 1));
            int first = Number(args, to ? 4 : 2, 1);
            int last = Number(args, to ? 5 : 3, -1);
            if (first < 0) first = data.Length + first + 1;
            if (last < 0) last = data.Length + last + 1;
            first = Math.Max(1, first);
            last = Math.Min(data.Length, last);
            if (first > last) return new object?[] { 0, null, null };
            int length = last - first + 1;
            if (length > 1024 * 1024) throw new RuntimeFault("socket send length exceeds limit");
            byte[] selected = new byte[length];
            Array.Copy(data, first - 1, selected, 0, length);
            uint pointer = Memory.CopyVm(selected);
            try
            {
                int port = to ? Number(args, 3) : 0;
                uint sent = to && (port < 0 || port > 65535) ? uint.MaxValue :
                    SendSocket((int)Memory.U32(user.Pointer), pointer, (uint)length, to,
                        to ? unchecked((uint)Number(args, 2)) : 0, (ushort)port);
                if (sent != uint.MaxValue) return new object?[] { (int)sent, null, null };
                Memory.Put32(user.Pointer + 4, 5);
                return new object?[] { null, to ? "udp sendto:err!" : "tcp send:err!", -1 };
            }
            finally
            {
                Memory.Free(pointer);
            }
        }

        private object?[] ReceiveScriptSocket(object?[] args, bool from)
        {
            ScriptUserdata user = NetworkReceiver(args);
            int requested = Number(args, 1);
            if (requested < 0) throw new RuntimeFault("negative socket receive length");
            int length = Math.Min(1500, requested);
            if (length == 0) return new object?[] { "", null, null };
            uint pointer = Memory.AllocateVm((uint)length);
            uint metadata = 0;
            try
            {
                metadata = from ? Memory.AllocateVm(6) : 0;
                uint ip = metadata;
                uint port = metadata + 4;
                uint got = ReceiveSocket((int)Memory.U32(user.Pointer), pointer, (uint)length, from, ip, port);
                if (got != uint.MaxValue)
                {
                    string text = ScriptChunk.Bytes.GetString(Memory.ReadSpan(pointer, got));
                    return from ? new object?[] { text, got == 0 ? null : unchecked((int)Memory.U32(ip)),
                        got == 0 ? null : (int)Memory.U16(port) } : new object?[] { text, null, null };
                }
                Memory.Put32(user.Pointer + 4, 5);
                return new object?[] { null, from ? "udp recvfrom:err!" : "tcp recv:err!", null };
            }
            finally
            {
                Memory.Free(pointer);
                if (metadata != 0) Memory.Free(metadata);
            }
        }
    }
}
