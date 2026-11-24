using System;
using System.Buffers;
using System.IO;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private static readonly object TableSaveCommitLock = new object();
        private static readonly WeakByteBuffer LargeTableFileBuffer = new WeakByteBuffer();

        private object? LoadScriptTableFile(string name)
        {
            RuntimeFileBuffer file;
            lock (TableSaveCommitLock)
            {
                file = ReadRuntimeFile(name, ScriptStore.MaxPooledBytes, ScriptStore.MaxBytes, "store size limit",
                    LargeTableFileBuffer, clearAfterUse: true);
            }
            using (file)
            {
                return file.Data == null ? null : ScriptStore.Decode(file.Bytes);
            }
        }

        private bool SaveScriptTableFile(string name, byte[] bytes, int length)
        {
            if (Trace.Count < TraceLimit) Trace.Add($"open {name}, mode=10");
            return AcceptNativeFileName(name) && FileOperation("write",
                (Session: this, Name: name, Bytes: bytes, Length: length), static request =>
                    request.Session.WriteTableSave(request.Name, request.Bytes, request.Length), false);
        }

        private bool WriteTableSave(string name, byte[] bytes, int length)
        {
            string path = NativeSavePath(name);
            if (!NativeWritable(name, path)) return false;
            bool systemStorage = storage.GetType() == typeof(SystemFileStorage);
            if (systemStorage)
            {
                lock (TableSaveCommitLock)
                {
                    if (storage.FileExists(path) && FileOperation("compare table save",
                        (Path: path, Bytes: bytes, Length: length), static request =>
                            TableSaveMatches(request.Path, request.Bytes, request.Length), false))
                    {
                        if (Trace.Count < TraceLimit) Trace.Add($"save unchanged {name} bytes={length}");
                        return true;
                    }
                }
            }
            string directory = Path.GetDirectoryName(path)!;
            EnsureVirtualDirectoryBacking(directory);
            string staging = Path.Combine(directory, ".mrp-save-" + Guid.NewGuid().ToString("N"));
            string temporary = staging + ".tmp";
            string backup = staging + ".bak";
            bool ownsTemporary = false;
            try
            {
                using (Stream output = storage.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporary = true;
                    output.Write(bytes, 0, length);
                    output.Flush();
                }

                bool replaced;
                lock (TableSaveCommitLock)
                {
                    CheckLinks(path);
                    try
                    {
                        replaced = storage.FileExists(path);
                        if (!replaced)
                        {
                            storage.MoveFile(temporary, path);
                        }
                        else if (systemStorage)
                        {
                            System.IO.File.Replace(temporary, path, backup);
                        }
                        else
                        {
                            storage.MoveFile(path, backup);
                            storage.MoveFile(temporary, path);
                        }
                    }
                    catch
                    {
                        RestoreTableSave(path, backup);
                        throw;
                    }
                }
                ownsTemporary = false;
                if (replaced) RemoveTableSaveFile(backup);
                if (Trace.Count < TraceLimit) Trace.Add($"write {name} bytes={length}");
                return true;
            }
            finally
            {
                if (ownsTemporary) RemoveTableSaveFile(temporary);
            }
        }

        private static bool TableSaveMatches(string path, byte[] bytes, int length)
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1);
            if (input.Length != length) return false;

            int chunkSize = Math.Min(length, 32 * 1024);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
            try
            {
                int offset = 0;
                while (offset < length)
                {
                    int count = input.Read(buffer, 0, Math.Min(chunkSize, length - offset));
                    if (count == 0 || !buffer.AsSpan(0, count).SequenceEqual(bytes.AsSpan(offset, count))) return false;
                    offset += count;
                }
                return input.Length == length;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void RestoreTableSave(string path, string backup)
        {
            uint status = FileOperation("restore table save", () =>
            {
                if (!storage.FileExists(backup)) return 0u;
                if (storage.FileExists(path)) return uint.MaxValue;
                storage.MoveFile(backup, path);
                return 0;
            });
            if (status != 0)
            {
                // Retain the recovery location even after the regular trace fills up.
                if (Trace.Count >= TraceLimit) Trace.RemoveAt(0);
                Trace.Add($"table save recovery path: {backup} (target: {path})");
            }
        }

        private void RemoveTableSaveFile(string path)
        {
            FileOperation("remove table save staging file", (Storage: storage, Path: path), static request =>
            {
                if (request.Storage.FileExists(request.Path)) request.Storage.DeleteFile(request.Path);
                return 0u;
            });
        }

        private static string ScriptFileName(object?[] args, int index)
        {
            string name = TextArgument(args, index);
            int zero = name.IndexOf('\0');
            return zero < 0 ? name : name.Substring(0, zero);
        }

        private static object?[] ScriptFileResult(bool success, string? name = null)
        {
            return success ? new object?[] { true } :
                new object?[] { null, name == null ? "file err:-1" : "file err: " + name + ": -1", -1 };
        }

        private ScriptUserdata ScriptFileReceiver(object?[] args, bool requireOpen)
        {
            RequireScriptArguments(args, 1);
            ScriptUserdata? file = ScriptFileObject(args[0]);
            if (file == null)
            {
                throw new RuntimeFault("invalid script file receiver");
            }
            if (requireOpen && Memory.U32(file.Pointer) == 0)
            {
                throw new RuntimeFault("attempt to use a closed file");
            }
            return file;
        }

        private ScriptUserdata? ScriptFileObject(object? value)
        {
            if (!(value is ScriptUserdata file) || file.Metatable == null ||
                !(scriptRegistry[file.Metatable] is string name))
            {
                return null;
            }
            int zero = name.IndexOf('\0');
            return (zero < 0 ? name : name.Substring(0, zero)) == "file*" ? file : null;
        }

        private void RegisterFileGlobals()
        {
            var methods = new ScriptTable();
            methods["__index"] = methods;
            methods["read"] = new ScriptVm.Native(ReadScriptHandle);
            methods["write"] = new ScriptVm.Native(WriteScriptHandle);
            methods["seek"] = new ScriptVm.Native(args =>
            {
                ScriptUserdata file = ScriptFileReceiver(args, true);
                int origin = Number(args, 1);
                int offset = Number(args, 2);
                return NativeSeek(Memory.U32(file.Pointer), offset, unchecked((uint)origin)) == 0 ?
                    new object?[] { offset } : ScriptFileResult(false);
            });
            methods["close"] = new ScriptVm.Native(args =>
            {
                ScriptUserdata file = ScriptFileReceiver(args, true);
                bool success;
                try
                {
                    success = NativeClose(Memory.U32(file.Pointer)) == 0;
                }
                finally
                {
                    Memory.Put32(file.Pointer, 0);
                }
                return ScriptFileResult(success);
            });
            methods["__gc"] = new ScriptVm.Native(args =>
            {
                ScriptUserdata file = ScriptFileReceiver(args, false);
                if (Memory.U32(file.Pointer) != 0)
                {
                    Script.Call(methods["close"], file);
                }
                return Array.Empty<object?>();
            });
            methods["__str"] = new ScriptVm.Native(args =>
            {
                ScriptUserdata file = ScriptFileReceiver(args, false);
                return new object?[] { "file (" + (Memory.U32(file.Pointer) == 0 ? "closed" : file.Pointer.ToString("X8")) + ")" };
            });
            scriptRegistry["file*"] = methods;
            scriptRegistry[methods] = "file*";
            var library = new ScriptTable();
            library["open"] = new ScriptVm.Native(args =>
            {
                return OpenScriptFile(ScriptFileName(args, 0), unchecked((uint)Number(args, 1, 1)));
            });
            library["close"] = methods["close"];
            library["state"] = new ScriptVm.Native(args =>
            {
                ScriptUserdata? file = ScriptFileObject(args.Length == 0 ? null : args[0]);
                return new object?[] { file == null ? 2 : Memory.U32(file.Pointer) == 0 ? 1 : 0 };
            });
            library["readAll"] = new ScriptVm.Native(args =>
            {
                ScriptMutableString? value = ReadWholeScriptFile(ScriptFileName(args, 0));
                return value == null ? Array.Empty<object?>() : new object?[] { value };
            });
            Script.Globals["file"] = library;
        }

        private ScriptMutableString? ReadWholeScriptFile(string name)
        {
            if (Memory.HasWriteObservers)
            {
                // Preserve the read-before-allocation order when guest writes can run callbacks.
                using var file = ReadRuntimeFile(name, RuntimeFilePoolLimit, largeBuffer: LargeResourceFileBuffer);
                return file.Length == 0 ? null : NewMutableScriptString(file.Length, file.Bytes);
            }

            uint handle = NativeOpen(name, 1);
            if (handle == 0) return null;
            uint pending = 0;
            int length = 0;
            try
            {
                uint status, closeStatus;
                try
                {
                    status = FileOperation("read resource", () =>
                    {
                        Stream input = File(handle);
                        long size = input.Length;
                        if (size > Package.MaxBytes) throw new RuntimeFault("resource file exceeds size limit");
                        length = checked((int)size);
                        if (length == 0) return 0;
                        PrepareMutableStringAllocation(length);
                        pending = Memory.ReadWholeVmFile(input, length);
                        return 0;
                    });
                }
                finally { closeStatus = NativeClose(handle); }

                if (status != 0 || closeStatus != 0 || pending == 0) return null;
                var value = new ScriptMutableString(Memory, pending, length);
                mutableScriptStrings.Add(value);
                pending = 0;
                Script.Account(value);
                return value;
            }
            finally { Memory.Free(pending); }
        }

        private void RegisterSystemFiles(ScriptTable system)
        {
            void PathOperation(string name, Func<string, uint> operation)
            {
                system[name] = new ScriptVm.Native(args =>
                {
                    string path = ScriptFileName(args, 0);
                    return ScriptFileResult(operation(path) == 0, path);
                });
            }
            PathOperation("remove", NativeRemove);
            PathOperation("mkdir", name => NativeDirectory(name, false));
            PathOperation("rmdir", name => NativeDirectory(name, true));
            system["rename"] = new ScriptVm.Native(args =>
            {
                string name = ScriptFileName(args, 0);
                return ScriptFileResult(NativeRename(name, ScriptFileName(args, 1)) == 0, name);
            });
            system["getfileinfo"] = new ScriptVm.Native(args => new object?[] { (int)NativeInfo(ScriptFileName(args, 0)) });
            system["getfilelen"] = new ScriptVm.Native(args => new object?[] { unchecked((int)NativeLength(ScriptFileName(args, 0))) });
            system["getuptime"] = new ScriptVm.Native(_ => new object?[] { unchecked((int)ReadRuntimeClock()) });
            system["findstart"] = new ScriptVm.Native(args => ScriptFind(args, true));
            system["findnext"] = new ScriptVm.Native(args => ScriptFind(args, false));
            system["findstop"] = new ScriptVm.Native(args => new object?[] { unchecked((int)NativeFindStop((uint)Number(args, 0))) });
            foreach (var alias in new (string Name, string Target)[]
            {
                ("getUptime", "getuptime"), ("rm", "remove"), ("mkDir", "mkdir"), ("rmDir", "rmdir"),
                ("getFileInfo", "getfileinfo"), ("getFileLen", "getfilelen"),
                ("findStart", "findstart"), ("findNext", "findnext"), ("findStop", "findstop")
            })
            {
                system[alias.Name] = system[alias.Target];
            }
            system["datetime"] = new ScriptVm.Native(_ =>
            {
                DateTime date = localClock();
                return new object?[] { new ScriptTable
                {
                    ["year"] = date.Year, ["mon"] = date.Month, ["day"] = date.Day,
                    ["hour"] = date.Hour, ["min"] = date.Minute, ["sec"] = date.Second
                } };
            });
            Script.Globals["GetDatetime"] = system["datetime"];
        }

        private object?[] ScriptFind(object?[] args, bool start)
        {
            string name = start ? ScriptFileName(args, 0) : "";
            uint handle = start ? 0 : unchecked((uint)Number(args, 0));
            uint buffer = Memory.AllocateVm(128);
            try
            {
                uint status = start ? NativeFindStart(name, buffer, 128) : NativeFindNext(handle, buffer, 128);
                ReadOnlySpan<byte> bytes = Memory.ReadSpan(buffer, 128);
                int zero = bytes.IndexOf((byte)0);
                if (zero >= 0)
                {
                    bytes = bytes.Slice(0, zero);
                }
                string entry = ScriptChunk.Bytes.GetString(bytes);
                return start ? new object?[] { unchecked((int)status), entry } :
                    new object?[] { status == 0 ? entry : null };
            }
            finally
            {
                Memory.Free(buffer);
            }
        }

        private object?[] OpenScriptFile(string name, uint mode)
        {
            // Reserve userdata storage before opening an OS handle, matching failure ownership.
            CheckScriptAllocation(4);
            uint pointer = Memory.AllocateVm(4);
            bool retained = false;
            try
            {
                Memory.Put32(pointer, 0);
                object? metatable = scriptRegistry["file*"];
                if (metatable != null && !(metatable is ScriptTable))
                {
                    throw new RuntimeFault("file metatable is not a table");
                }
                uint handle = NativeOpen(name, mode);
                if (handle == 0)
                {
                    return ScriptFileResult(false, name);
                }
                Memory.Put32(pointer, handle);
                retained = true;
                return new object?[] { NewScriptUserdata(pointer, 4, (ScriptTable?)metatable) };
            }
            finally
            {
                if (!retained)
                {
                    Memory.Free(pointer);
                }
            }
        }

        private object?[] ReadScriptHandle(object?[] args)
        {
            ScriptUserdata file = ScriptFileReceiver(args, true);
            uint handle = Memory.U32(file.Pointer);
            Stream input = File(handle);
            string name = files[handle].Name;
            if (!input.CanRead)
            {
                return ScriptFileResult(false);
            }
            if (args.Length == 1) return Array.Empty<object?>();

            var values = new object?[args.Length - 1];
            int processed = 0;
            int pushed = 0;
            for (int index = 1; index < args.Length; index++)
            {
                processed++;
                // The reference skips nonnumeric arguments but includes them in its result count.
                if (!(args[index] is int count))
                {
                    continue;
                }
                if (count < 0 || count > Package.MaxBytes)
                {
                    throw new RuntimeFault("script file read size limit");
                }
                var read = FileOperation("script read", (Input: input, Count: count),
                    static request => (Success: true, Value: ReadScriptFileText(request.Input, request.Count)),
                    (Success: false, Value: (string?)null));
                if (!read.Success)
                {
                    return ScriptFileResult(false);
                }
                if (Trace.Count < TraceLimit) Trace.Add($"script read {name} bytes={read.Value?.Length ?? 0}");
                values[pushed++] = read.Value;
                if (read.Value == null)
                {
                    break;
                }
            }
            int carried = processed - pushed;
            if (carried != 0)
            {
                // The returned stack tail includes original arguments where no value was pushed.
                Array.Copy(values, 0, values, carried, pushed);
                Array.Copy(args, args.Length - carried, values, 0, carried);
            }
            if (processed != values.Length) Array.Resize(ref values, processed);
            return values;
        }

        private static string? ReadScriptFileText(Stream input, int count)
        {
            if (count == 0) return "";

            const int stackCapacity = 512;
            const int poolLimit = 512 * 1024;
            Span<byte> data = stackalloc byte[Math.Min(count, stackCapacity)];
            Span<byte> nextByte = stackalloc byte[1];
            byte[]? rented = null;
            int read = 0;
            try
            {
                while (read < count)
                {
                    if (read == data.Length)
                    {
                        // Confirm there is more data before growing a full buffer at EOF.
                        if (input.Read(nextByte) == 0) break;
                        int capacity = Math.Min(count, data.Length * 2);
                        if (data.Length == stackCapacity)
                        {
                            Type streamType = input.GetType();
                            if (streamType == typeof(MemoryStream) || (streamType == typeof(FileStream) && input.CanSeek))
                            {
                                int alreadyRead = read + 1;
                                int limit = Math.Min(count, poolLimit) - alreadyRead;
                                long remaining = Math.Max(0, input.Length - input.Position);
                                capacity = Math.Max(capacity, alreadyRead + (int)Math.Min(limit, remaining));
                            }
                        }
                        bool pool = capacity <= poolLimit;
                        byte[] larger = pool ? ArrayPool<byte>.Shared.Rent(capacity) : new byte[capacity];
                        data.CopyTo(larger);
                        if (rented != null) ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                        rented = pool ? larger : null;
                        data = larger.AsSpan(0, Math.Min(count, larger.Length));
                        data[read++] = nextByte[0];
                        continue;
                    }
                    int length = input.Read(data.Slice(read, Math.Min(data.Length - read, 64 * 1024)));
                    if (length == 0) break;
                    read += length;
                }
                return read == 0 ? null : ScriptChunk.Bytes.GetString(data.Slice(0, read));
            }
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

        private object?[] WriteScriptHandle(object?[] args)
        {
            ScriptUserdata file = ScriptFileReceiver(args, true);
            uint handle = Memory.U32(file.Pointer);
            Stream output = File(handle);
            string name = files[handle].Name;
            bool directFile = output.GetType() == typeof(FileStream);
            bool success = true;
            for (int index = 1; index < args.Length; index++)
            {
                var mutable = directFile ? args[index] as ScriptMutableString : null;
                string? text = mutable == null ? ScriptVm.Text(args[index]) : null;
                if (!success)
                {
                    continue;
                }
                if (!output.CanWrite)
                {
                    success = false;
                    continue;
                }
                int byteCount = mutable?.Length ?? ScriptChunk.Bytes.GetByteCount(text!);
                success = FileOperation("script write", (Output: output, Text: text, Mutable: mutable, ByteCount: byteCount), static request =>
                {
                    if (request.Output.Position > SaveFileLimit - request.ByteCount)
                    {
                        throw new RuntimeFault("save size limit");
                    }
                    if (request.Mutable is ScriptMutableString source)
                        request.Output.Write(source.Memory.ReadSpan(source.Pointer, (uint)source.Length));
                    else
                        WriteScriptFileText(request.Output, request.Text!, request.ByteCount);
                    request.Output.Flush();
                    return 0u;
                }) == 0;
                if (Trace.Count < TraceLimit) Trace.Add($"script write {name} bytes={byteCount}, success={success}");
            }
            return ScriptFileResult(success);
        }

        private static void WriteScriptFileText(Stream output, string text, int byteCount)
        {
            int capacity = Math.Min(byteCount, 64 * 1024);
            byte[]? rented = null;
            Span<byte> buffer = capacity <= 512 ? stackalloc byte[capacity] :
                (rented = ArrayPool<byte>.Shared.Rent(capacity)).AsSpan(0, capacity);
            try
            {
                if (byteCount <= capacity)
                {
                    int written = ScriptChunk.Bytes.GetBytes(text.AsSpan(), buffer);
                    output.Write(buffer.Slice(0, written));
                }
                else
                {
                    // Use one encoder so a surrogate pair can cross a buffer boundary.
                    Encoder encoder = ScriptChunk.Bytes.GetEncoder();
                    ReadOnlySpan<char> remaining = text.AsSpan();
                    bool completed;
                    do
                    {
                        encoder.Convert(remaining, buffer, flush: true, out int used, out int written, out completed);
                        output.Write(buffer.Slice(0, written));
                        remaining = remaining.Slice(used);
                    }
                    while (!completed);
                }
            }
            finally
            {
                if (rented != null) ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }
}
