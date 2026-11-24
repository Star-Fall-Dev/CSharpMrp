using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private const long SaveFileLimit = 4 * 1024 * 1024;
        private bool legacySelfPackageMountReported;

        private bool IsLegacySelfPackage(string name)
        {
            if (workingDirectory.RawPrefix != "drives/b/mythroad/" ||
                !string.Equals(name, packageFile, StringComparison.Ordinal))
            {
                return false;
            }
            if (!legacySelfPackageMountReported)
            {
                legacySelfPackageMountReported = true;
                Record("current package read-only mount: b:/mythroad/; exact current filename only");
            }
            return true;
        }

        private string NativeSavePath(string name, bool allowRootDirectory = false)
        {
            try
            {
                return SavePath(name, allowRootDirectory);
            }
            catch (RuntimeFault error)
            {
                // A rejected native filename is an unavailable file, not a CPU fault.
                // Keep the rejection visible and let FileOperation return its ABI failure.
                Record("native path denied: " + error.Message);
                throw new IOException("Native filename rejected by sandbox", error);
            }
        }

        private string NativeDirectoryPath(string name)
        {
            return NativeSavePath(name is "" or "/" or "\\" ? "." : name, allowRootDirectory: true);
        }

        // Script strings and native C strings carry bytes in a Latin-1 container.
        // Interpret them in the guest code page before handling path separators.
        private string DecodeFilename(string name)
        {
            foreach (char character in name)
            {
                if (character > 0x7f)
                {
                    if (name.Length <= 256)
                    {
                        Span<byte> bytes = stackalloc byte[name.Length];
                        int index = 0;
                        for (; index < name.Length && name[index] <= 0xff; index++)
                            bytes[index] = (byte)name[index];
                        if (index == name.Length) return filenameEncoding.GetString(bytes);
                    }
                    return filenameEncoding.GetString(ScriptChunk.Bytes.GetBytes(name));
                }
            }
            return name;
        }

        private uint FileOperation(string operation, Func<uint> action, uint failure = uint.MaxValue) =>
            FileOperation(operation, action, static callback => callback(), failure);

        private uint FileOperation<T>(string operation, T state, Func<T, uint> action, uint failure = uint.MaxValue) =>
            FileOperation<T, uint>(operation, state, action, failure);

        private TResult FileOperation<T, TResult>(string operation, T state, Func<T, TResult> action, TResult failure)
        {
            try
            {
                return action(state);
            }
            catch (IOException error)
            {
                Record($"{operation} failed: {error.GetType().Name}");
                return failure;
            }
            catch (UnauthorizedAccessException)
            {
                Record($"{operation} failed: access denied");
                return failure;
            }
            catch (DecoderFallbackException)
            {
                Record($"{operation} failed: invalid guest filename encoding");
                return failure;
            }
        }

        private bool NativeWritable(string name, string path)
        {
            bool allowed = !IsPackage(name) && mythroad?.Protected(RootRelative(path)) != true;
            if (!allowed)
            {
                Record($"file mutation denied: read-only {name}");
            }
            return allowed;
        }

        private static ReadOnlySpan<char> LastGuestPathComponent(string decodedName)
        {
            ReadOnlySpan<char> path = decodedName.AsSpan().TrimEnd("/\\".AsSpan());
            return path.Slice(path.LastIndexOfAny('/', '\\') + 1);
        }

        private static bool IsDotDirectory(string decodedName)
        {
            ReadOnlySpan<char> last = LastGuestPathComponent(decodedName);
            return last.SequenceEqual(".".AsSpan()) || last.SequenceEqual("..".AsSpan());
        }

        private bool AcceptNativeFileName(string name)
        {
            if (name.Length == 0 || name.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                Record("open failed: empty filename or unavailable file URI; no filesystem access");
                return false;
            }
            return true;
        }

        private uint NativeOpen(string name, uint mode)
        {
            if (closingFiles)
            {
                Record("open failed: session files are closing");
                return 0;
            }
            if (Trace.Count < TraceLimit) Trace.Add($"open {name}, mode={mode}");
            if (!AcceptNativeFileName(name)) return 0;
            // The enabled reference backend checks only the four access/create bits.
            uint access = (mode & 4) != 0 ? 4u : (mode & 2) != 0 ? 2u : 1u;
            uint normalized = access | (mode & 8);
            if (normalized != mode && Trace.Count < TraceLimit)
            {
                Trace.Add($"open reference flags: {mode} -> {normalized}; ignored flags do not truncate");
            }
            return FileOperation("open", (Session: this, Name: name, Mode: normalized), static request =>
            {
                string path = request.Session.NativeSavePath(request.Name);
                return request.Session.Open(request.Name, path, request.Mode);
            }, 0);
        }

        private uint NativeInfo(string name)
        {
            if (Trace.Count < TraceLimit) Trace.Add("info " + name);
            return FileOperation("info", (Session: this, Name: name), static request =>
            {
                var (session, guestName) = request;
                if (session.IsPackage(guestName))
                {
                    return 1;
                }
                string path;
                if (LastGuestPathComponent(session.DecodeFilename(guestName)).SequenceEqual("..".AsSpan()))
                {
                    string guest = guestName.TrimEnd('/', '\\');
                    string directory = session.NativeDirectoryPath(guest.Substring(0, guest.Length - 2));
                    if (string.Equals(directory, session.saveRoot, StringComparison.OrdinalIgnoreCase) ||
                        !directory.StartsWith(session.saveRootPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        session.Record("info failed: parent directory is outside the virtual sandbox, MR_IS_INVALID=8");
                        return 8;
                    }
                    if (!session.storage.DirectoryExists(directory) &&
                        session.mythroad?.Info(session.RootRelative(directory)) != 2)
                    {
                        return 8;
                    }
                    path = Path.GetDirectoryName(directory)!;
                    session.CheckLinks(path);
                }
                else
                {
                    path = session.NativeDirectoryPath(guestName);
                }
                uint resource = session.mythroad?.Info(session.RootRelative(path)) ?? 8u;
                return resource != 8 ? resource : session.storage.FileExists(path) ? 1u : session.storage.DirectoryExists(path) ? 2u : 8u;
            }, 8);
        }

        private uint NativeLength(string name)
        {
            return FileOperation("length", (Session: this, Name: name), static request =>
            {
                RuntimeSession session = request.Session;
                if (session.IsPackage(request.Name))
                {
                    return (uint)(request.Name == session.packageFile ? session.package : session.rootPackage).Length;
                }
                string path = session.NativeSavePath(request.Name);
                uint resource = session.mythroad?.Length(session.RootRelative(path)) ?? uint.MaxValue;
                if (resource != uint.MaxValue)
                {
                    return resource;
                }
                long length = session.storage.FileExists(path) ? session.storage.GetLength(path) : -1;
                return length >= 0 && length <= int.MaxValue ? (uint)length : uint.MaxValue;
            });
        }

        private uint NativeRemove(string name)
        {
            return FileOperation("remove", () =>
            {
                if (IsDotDirectory(DecodeFilename(name)))
                {
                    Record("remove denied: dot directory entry, MR_FAILED=-1; no filesystem access");
                    return uint.MaxValue;
                }
                string path = NativeSavePath(name);
                if (!NativeWritable(name, path))
                {
                    Record($"remove denied: read-only {name}, MR_FAILED=-1");
                    return uint.MaxValue;
                }
                if (!storage.FileExists(path))
                {
                    return uint.MaxValue;
                }
                storage.DeleteFile(path);
                Record($"remove {name}");
                return 0;
            });
        }

        private uint NativeFindStart(string searchName, uint buffer, uint size)
        {
            if (closingFiles)
            {
                Record("find failed: session files are closing");
                return uint.MaxValue;
            }
            if (searchName.Length != 0 && searchName.Trim(' ').Length == 0)
            {
                Record("find failed: blank directory name, MR_FAILED=-1; no filesystem access");
                return uint.MaxValue;
            }
            return FileOperation("find", () =>
            {
                string searchPath = NativeDirectoryPath(searchName);
                string resourceDirectory = mythroad == null ? "" : RootRelative(searchPath);
                bool existsOnDisk = storage.DirectoryExists(searchPath);
                if (!existsOnDisk && mythroad?.Info(resourceDirectory) != 2)
                {
                    return uint.MaxValue;
                }
                ReadOnlyMemory<string> names = mythroad?.DirectoryEntries(resourceDirectory) ?? default;
                if (existsOnDisk)
                {
                    // The reference POSIX readdir backend exposes these entries even in an empty directory.
                    var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".", ".." };
                    foreach (string name in names.Span) unique.Add(name);
                    AddDiskFindEntries(searchPath, unique);
                    var sorted = new string[unique.Count];
                    unique.CopyTo(sorted);
                    Array.Sort(sorted, StringComparer.Ordinal);
                    names = sorted;
                }
                if (Trace.Count < TraceLimit) Trace.Add($"find {searchName} entries={names.Length}");
                int next = WriteFindEntry(names.Span, 0, buffer, size);
                uint searchHandle = nextHandle++;
                searches.Add(searchHandle, next < 0 || next == names.Length ? default : (names, next));
                return searchHandle;
            });
        }

        private void AddDiskFindEntries(string searchPath, HashSet<string> names)
        {
            List<string>? parents = null;
            foreach (string entry in storage.EnumerateEntries(searchPath))
            {
                if (Path.GetDirectoryName(entry.AsSpan()).SequenceEqual(searchPath.AsSpan()))
                {
                    CheckLink(entry);
                    if (parents == null)
                    {
                        parents = new List<string>();
                        for (string? parent = searchPath; !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                        {
                            if (string.Equals(parent, saveRootAncestors[0], StringComparison.Ordinal))
                            {
                                parents.AddRange(saveRootAncestors);
                                break;
                            }
                            parents.Add(parent);
                        }
                    }
                    // Reuse path strings and query the filesystem afresh for each entry.
                    foreach (string parent in parents) CheckLink(parent);
                }
                else
                {
                    CheckLinks(entry);
                }
                names.Add(Path.GetFileName(entry));
                if (names.Count > 4096)
                {
                    throw new RuntimeFault("directory entry limit");
                }
            }
        }

        private uint NativeFindNext(uint handle, uint buffer, uint size)
        {
            if (!searches.TryGetValue(handle, out var search) || search.Names.IsEmpty)
            {
                return uint.MaxValue;
            }
            int next = WriteFindEntry(search.Names.Span, search.Next, buffer, size);
            // Keep the guest handle closable without retaining the exhausted snapshot.
            searches[handle] = next < 0 || next == search.Names.Length ? default : (search.Names, next);
            return next < 0 ? uint.MaxValue : 0u;
        }

        private int WriteFindEntry(ReadOnlySpan<string> names, int index, uint buffer, uint size)
        {
            Span<byte> scratch = stackalloc byte[256];
            for (; index < names.Length; index++)
            {
                string name = names[index];
                int length;
                try
                {
                    length = filenameEncoding.GetByteCount(name);
                }
                catch (EncoderFallbackException)
                {
                    continue;
                }
                if (size == 0) return index + 1;

                byte[]? rented = null;
                try
                {
                    Span<byte> encoded = length <= scratch.Length ? scratch :
                        (rented = ArrayPool<byte>.Shared.Rent(length));
                    int written = filenameEncoding.GetBytes(name.AsSpan(), encoded);
                    Memory.WritePadded(buffer, size, encoded.Slice(0, written));
                }
                finally
                {
                    if (rented != null) ArrayPool<byte>.Shared.Return(rented);
                }
                return index + 1;
            }
            return -1;
        }

        private uint NativeFindStop(uint handle)
        {
            return searches.Remove(handle) ? 0u : uint.MaxValue;
        }

        private uint NativeClose(uint handle)
        {
            if (!files.Remove(handle, out var file))
            {
                return uint.MaxValue;
            }
            return FileOperation("close", (Session: this, Stream: file.Data), static request =>
            {
                try
                {
                    request.Stream.Dispose();
                }
                catch (Exception error) when (request.Session.closingScript && !request.Session.disposed &&
                    error is IOException or UnauthorizedAccessException or DecoderFallbackException)
                {
                    (request.Session.disposeFailures ??= new List<Exception>()).Add(error);
                    throw;
                }
                return 0;
            });
        }

        private uint NativeRead(uint handle, uint pointer, uint count)
        {
            if (!files.TryGetValue(handle, out var file) || !file.Data.CanRead)
            {
                return uint.MaxValue;
            }
            if (count == 0)
            {
                return 0;
            }
            // The reference read/write backend reports an invalid buffer as an
            // I/O failure (EFAULT); it does not turn it into a CPU exception.
            if (!Memory.ContainsRange(pointer, count))
            {
                Record($"read failed: invalid guest buffer {pointer:x8}+{count}, MR_FAILED=-1");
                return uint.MaxValue;
            }
            return FileOperation("read", (Session: this, Stream: file.Data, Name: file.Name, Pointer: pointer, Count: count), static request =>
            {
                Type streamType = request.Stream.GetType();
                int length;
                if (streamType == typeof(MemoryStream))
                {
                    length = request.Session.Memory.ReadMemoryFile((MemoryStream)request.Stream, request.Pointer, request.Count);
                }
                else
                {
                    int capacity = (int)request.Count;
                    if (capacity >= 1024 * 1024 && streamType == typeof(FileStream) && request.Stream.CanSeek)
                    {
                        // Probe even at apparent EOF in case the file has grown.
                        long remaining = request.Stream.Length - request.Stream.Position;
                        capacity = (int)Math.Min(capacity, Math.Max(4096, remaining));
                    }
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
                    try
                    {
                        length = request.Stream.Read(buffer, 0, capacity);
                        request.Session.Memory.Write(request.Pointer, buffer, 0, length);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                if (request.Session.Trace.Count < TraceLimit)
                    request.Session.Trace.Add($"read {request.Name} bytes={length}");
                return (uint)length;
            });
        }

        private uint NativeWrite(uint handle, uint pointer, uint count)
        {
            if (!files.TryGetValue(handle, out var file) || !file.Data.CanWrite)
            {
                return uint.MaxValue;
            }
            if (count == 0)
            {
                return 0;
            }
            if (!Memory.ContainsRange(pointer, count))
            {
                Record($"write failed: invalid guest buffer {pointer:x8}+{count}, MR_FAILED=-1");
                return uint.MaxValue;
            }
            return FileOperation("write", (Session: this, Stream: file.Data, Name: file.Name, Pointer: pointer, Count: count), static request =>
            {
                long position = request.Stream.Position;
                if (position > SaveFileLimit - request.Count)
                {
                    if (request.Session.Trace.Count < TraceLimit)
                        request.Session.Trace.Add($"write failed: save size limit, {request.Name}, offset={position}, count={request.Count}, MR_FAILED=-1");
                    return uint.MaxValue;
                }
                request.Stream.Write(request.Session.Memory.ReadSpan(request.Pointer, request.Count));
                request.Stream.Flush();
                if (request.Session.Trace.Count < TraceLimit)
                    request.Session.Trace.Add($"write {request.Name} bytes={request.Count}");
                return request.Count;
            });
        }

        private uint NativeSeek(uint handle, int offset, uint origin)
        {
            if (origin > 2 || !files.TryGetValue(handle, out var file))
            {
                return uint.MaxValue;
            }
            return FileOperation("seek", (Stream: file.Data, Offset: offset, Origin: origin), static request =>
            {
                long basis = request.Origin == 0 ? 0 : request.Origin == 1 ? request.Stream.Position : request.Stream.Length;
                if (basis + request.Offset < 0)
                {
                    return uint.MaxValue;
                }
                request.Stream.Seek(request.Offset, (SeekOrigin)request.Origin);
                return 0;
            });
        }

        private uint NativeTell(uint handle)
        {
            if (!files.TryGetValue(handle, out var file) || !file.Data.CanSeek)
            {
                return uint.MaxValue;
            }
            return FileOperation("file position", file.Data, static stream =>
            {
                long position = stream.Position;
                return position >= 0 && position <= int.MaxValue ? (uint)position : uint.MaxValue;
            });
        }

        private uint NativeRename(string from, string to)
        {
            return FileOperation("rename", () =>
            {
                string source = NativeSavePath(from);
                string destination = NativeSavePath(to);
                if (!NativeWritable(from, source) || !NativeWritable(to, destination))
                {
                    return uint.MaxValue;
                }
                if (string.Equals(source, destination, StringComparison.Ordinal))
                {
                    return storage.FileExists(source) || storage.DirectoryExists(source) ? 0u : uint.MaxValue;
                }
                if (storage.DirectoryExists(source))
                {
                    storage.MoveDirectory(source, destination);
                }
                else
                {
                    storage.MoveFile(source, destination);
                }
                Record($"rename {from} to {to}");
                return 0;
            });
        }

        private uint NativeDirectory(string name, bool remove)
        {
            if (name.Length != 0 && name.Trim(' ').Length == 0)
            {
                Record($"{(remove ? "rmdir" : "mkdir")} failed: blank directory name, MR_FAILED=-1; no filesystem access");
                return uint.MaxValue;
            }
            return FileOperation(remove ? "rmdir" : "mkdir", () =>
            {
                if (remove && IsDotDirectory(DecodeFilename(name)))
                {
                    Record("rmdir denied: dot directory entry, MR_FAILED=-1; no filesystem access");
                    return uint.MaxValue;
                }
                string path = NativeSavePath(name);
                if (!NativeWritable(name, path))
                {
                    return uint.MaxValue;
                }
                if (remove)
                {
                    storage.DeleteDirectory(path);
                }
                else
                {
                    if (storage.DirectoryExists(path) || storage.FileExists(path))
                    {
                        return 0;
                    }
                    string parent = Path.GetDirectoryName(path)!;
                    EnsureVirtualDirectoryBacking(parent);
                    if (!storage.DirectoryExists(parent))
                    {
                        return uint.MaxValue;
                    }
                    storage.CreateDirectory(path);
                }
                Record($"{(remove ? "rmdir" : "mkdir")} {name}");
                return 0;
            });
        }
    }
}
