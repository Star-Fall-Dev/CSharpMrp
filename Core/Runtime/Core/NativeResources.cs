using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace CSharpMrp
{
    public sealed partial class RuntimeSession
    {
        private uint registeredPackages;
        private string cachedResourceName = "";
        private byte[]? cachedResourceNameBytes;
        private int resourceReadDepth;
        private bool disposeAfterResourceRead;
        private uint smsDirty;
        private readonly Dictionary<uint, uint> providedMemory = new Dictionary<uint, uint>();
        private readonly List<(string Name, Package? Archive, int Size, byte[] Digest, WeakReference<string>? OverrideText)>
            archivedScriptModules = new List<(string, Package?, int, byte[], WeakReference<string>?)>();
        private static readonly char[] ResourceWildcards = { '*', '?' };
        private static readonly char[] PathSeparators = { '/', '\\' };
        private const uint SmsConfigurationSize = 4320;
        private const string SmsAlphabet = "abcdefgDijkABChEFGHIJKLMNOPQRSTUVWXYZ0123456789lmnopqrstuvw/yz+x";
        private uint SmsBuffer => Memory.U32(table + 112 * 4);

        private byte[] ReadResidentPackage(uint pointer, uint length) => ReadResidentPackageSpan(pointer, length).ToArray();

        private ReadOnlySpan<byte> ReadResidentPackageSpan(uint pointer, uint length)
        {
            if (length > Package.MaxBytes)
            {
                throw new RuntimeFault("resident package exceeds size limit");
            }
            if (Memory.TryGetAllocationRemaining(pointer, out uint remaining) && length > remaining)
            {
                throw new RuntimeFault("resident package exceeds allocation");
            }
            return Memory.ReadSpan(pointer, length);
        }

        private byte[] ReadRegisteredPackage(uint pointer) => ReadResidentPackage(pointer, ReadRegisteredPackageLength(pointer));

        private uint ReadRegisteredPackageLength(uint pointer)
        {
            if (Memory.TryGetAllocationRemaining(pointer, out uint remaining) && remaining < 12)
            {
                throw new RuntimeFault("resident package header exceeds allocation");
            }
            Memory.Check(pointer, 12);
            return Memory.U32(pointer + 8);
        }

        private uint ReadNativeResource(string resource, uint lengthOutput, uint mode, out bool owned,
            out Package? sourceArchive, bool exposeAddress = true)
        {
            owned = false;
            sourceArchive = null;
            if (Trace.Count < TraceLimit) Trace.Add($"readFile {resource}, mode={mode}");
            Package selected = package;
            byte[]? contents = null;
            bool fileResource = false;
            uint allocated = 0;
            uint residentPackage = 0;
            uint packageSelector = Memory.U8(Memory.U32(table + 100 * 4));
            if (packageSelector == '*' || packageSelector == '$')
            {
                uint length;
                if (packageSelector == '*')
                {
                    uint packageIndex = Memory.U8(Memory.U32(table + 100 * 4) + 1) - 'A';
                    if (packageIndex >= 50)
                    {
                        return 0;
                    }
                    residentPackage = Memory.U32(registeredPackages + packageIndex * 4);
                    if (residentPackage == 0)
                    {
                        return 0;
                    }
                    length = ReadRegisteredPackageLength(residentPackage);
                }
                else
                {
                    residentPackage = Memory.U32(Memory.U32(table + 104 * 4));
                    length = Memory.U32(Memory.U32(table + 105 * 4));
                    if (Trace.Count < TraceLimit) Trace.Add($"RAM package @{residentPackage:x8}, size={length}");
                }
                ReadOnlySpan<byte> image = ReadResidentPackageSpan(residentPackage, length);
                if (mode == 1 || mode == 2)
                {
                    if (!Package.TryFindSequentialEntry(image, resource, out var entry))
                    {
                        return 0;
                    }
                    if (mode == 1)
                    {
                        return 1;
                    }
                    Memory.Put32(lengthOutput, (uint)entry.Size);
                    return FinishNativeResource(resource, lengthOutput, residentPackage + (uint)entry.Offset,
                        fromFile: false, exposeAddress: exposeAddress);
                }
                selected = Package.FromOwnedBytes(image.ToArray(), sequential: true);
            }
            else
            {
                if (mode == 0)
                {
                    // MRP names are literal byte keys; wildcard names cannot name a host file.
                    bool packageOnly = resource.IndexOfAny(ResourceWildcards) >= 0 &&
                        resource.IndexOfAny(PathSeparators) < 0;
                    if (!packageOnly)
                    {
                        fileResource = TryReadNativeResourceFile(resource, lengthOutput, out allocated);
                    }
                }
                if (!fileResource)
                {
                    string selectedName = ReadResourcePackageName();
                    if (IsPackage(selectedName))
                    {
                        selected = selectedName == packageFile ? package : rootPackage;
                    }
                    else
                    {
                        byte[]? image = ReadRuntimeFile(selectedName);
                        if (image == null)
                        {
                            return 0;
                        }
                        selected = Package.FromOwnedBytes(image);
                    }
                }
            }
            (int Offset, int Size) indexed = default;
            if (!fileResource && !selected.TryGetIndexedEntry(resource, out indexed))
            {
                contents = ReferenceEquals(selected, package) ? ReadMissingSdkStartup(resource) : null;
                if (contents == null)
                {
                    return 0;
                }
            }
            if (mode == 1)
            {
                return 1;
            }
            if (fileResource)
            {
                owned = allocated != 0;
            }
            else if (contents == null)
            {
                allocated = ReadPackageResource(selected, resource, indexed, residentPackage, lengthOutput, out owned);
                sourceArchive = selected;
            }
            else
            {
                Memory.Put32(lengthOutput, (uint)contents.Length);
                allocated = AllocateResource(contents);
                owned = allocated != 0;
            }
            return FinishNativeResource(resource, lengthOutput, allocated, fileResource || contents != null, exposeAddress);
        }

        private uint FinishNativeResource(string resource, uint lengthOutput, uint address, bool fromFile, bool exposeAddress)
        {
            if (address == 0)
            {
                return 0;
            }
            uint resourceLength = Memory.U32(lengthOutput);
            if (Trace.Count < TraceLimit) Trace.Add($"resource bytes @{address:x8}, size={resourceLength}");
            if (exposeAddress && resource.EndsWith(".ext", StringComparison.OrdinalIgnoreCase))
            {
                string resourceOrigin = fromFile ? "file:" + resource :
                    ReadResourcePackageName() + ":" + resource;
                TrackResourceModule(resource, resourceOrigin, address, resourceLength);
                Record($"resource EXT {resource} @{address:x8}; execution not yet observed");
            }
            return address;
        }

        private string ReadResourcePackageName()
        {
            ReadOnlySpan<byte> name = Memory.CStringBytes(Memory.U32(table + 100 * 4));
            if (name.SequenceEqual(packageFileBytes)) return packageFile;
            if (!ReferenceEquals(rootPackageFileBytes, packageFileBytes) && name.SequenceEqual(rootPackageFileBytes))
                return rootPackageFile;
            return ScriptChunk.Bytes.GetString(name);
        }

        private bool TryReadNativeResourceFile(string name, uint lengthOutput, out uint address)
        {
            using var file = ReadRuntimeFile(name, RuntimeFilePoolLimit, largeBuffer: LargeResourceFileBuffer);
            address = 0;
            if (file.Length == 0) return false;

            Memory.Put32(lengthOutput, (uint)file.Length);
            address = AllocateResource(file.Bytes);
            return true;
        }

        private const int RuntimeFilePoolLimit = 256 * 1024;
        private static readonly WeakByteBuffer LargeResourceFileBuffer = new WeakByteBuffer();

        private byte[]? ReadRuntimeFile(string name) => ReadRuntimeFile(name, poolLimit: 0).Data;

        private RuntimeFileBuffer ReadRuntimeFile(string name, int poolLimit,
            int sizeLimit = Package.MaxBytes, string sizeLimitError = "resource file exceeds size limit",
            WeakByteBuffer? largeBuffer = null, bool clearAfterUse = false)
        {
            uint handle = NativeOpen(name, 1);
            if (handle == 0) return default;
            RuntimeFileBuffer file = default;
            bool readComplete = false;
            try
            {
                Stream input = File(handle);
                int? length = FileOperation("read resource", (Input: input, Limit: sizeLimit, Error: sizeLimitError), static request =>
                {
                    long size = request.Input.Length;
                    if (size > request.Limit)
                    {
                        throw new RuntimeFault(request.Error);
                    }
                    return checked((int)size);
                }, (int?)null);
                if (!length.HasValue) return default;

                file = new RuntimeFileBuffer(length.Value, poolLimit, largeBuffer, clearAfterUse);
                readComplete = FileOperation("read resource", (Input: input, File: file), static request =>
                {
                    int offset = 0;
                    while (offset < request.File.Length)
                    {
                        int count = request.Input.Read(request.File.Data!, offset, request.File.Length - offset);
                        if (count == 0)
                        {
                            throw new IOException("resource file shortened during read");
                        }
                        offset += count;
                    }
                    return 0;
                }) == 0;
            }
            finally
            {
                bool closeSucceeded = false;
                try
                {
                    closeSucceeded = NativeClose(handle) == 0;
                }
                finally
                {
                    if (!readComplete || !closeSucceeded)
                    {
                        file.Return(clear: true);
                        file = default;
                    }
                }
            }
            return file;
        }

        private readonly struct RuntimeFileBuffer : IDisposable
        {
            private readonly bool sharedPool;
            private readonly WeakByteBuffer? largeBuffer;
            private readonly bool clearAfterUse;
            internal readonly byte[]? Data;
            internal readonly int Length;

            internal RuntimeFileBuffer(int length, int poolLimit, WeakByteBuffer? largeBuffer, bool clearAfterUse)
            {
                Length = length;
                sharedPool = length > 0 && length <= poolLimit;
                this.largeBuffer = length > poolLimit ? largeBuffer : null;
                this.clearAfterUse = clearAfterUse;
                Data = sharedPool ? ArrayPool<byte>.Shared.Rent(length) : this.largeBuffer?.Rent(length) ?? new byte[length];
            }

            internal ReadOnlySpan<byte> Bytes => Data.AsSpan(0, Length);

            internal void Return(bool clear)
            {
                if (!sharedPool && largeBuffer == null) return;
                if (clear) Data.AsSpan(0, Length).Clear();
                if (sharedPool) ArrayPool<byte>.Shared.Return(Data!);
                else largeBuffer!.Return(Data!);
            }

            public void Dispose() => Return(clearAfterUse);
        }

        private delegate T ResourceDecoder<T>(RuntimeSession session, uint address, uint size, ref bool owned);

        private byte[]? ReadResourceBytes(string name) =>
            ReadResource(name, static (RuntimeSession session, uint address, uint size, ref bool owned) =>
                session.Memory.Read(address, size));

        private T? ReadResource<T>(string name, ResourceDecoder<T> decode, bool rememberScriptModule = false)
            where T : class =>
            TryReadResource(name, decode, out T result, rememberScriptModule) ? result : null;

        private bool TryReadResource<T>(string name, ResourceDecoder<T> decode, out T result, bool rememberScriptModule = false)
        {
            resourceReadDepth++;
            Exception? readFailure = null;
            try
            {
                return TryReadResourceCore(name, decode, out result, rememberScriptModule);
            }
            catch (Exception error) when (disposeAfterResourceRead)
            {
                readFailure = error;
                throw;
            }
            finally
            {
                if (--resourceReadDepth == 0 && disposeAfterResourceRead)
                {
                    disposeAfterResourceRead = false;
                    try { Dispose(); }
                    catch (Exception closeError) when (readFailure != null)
                    {
                        throw new AggregateException(readFailure, closeError);
                    }
                }
            }
        }

        private bool TryReadResourceCore<T>(string name, ResourceDecoder<T> decode, out T result, bool rememberScriptModule)
        {
            uint previousStack = Cpu.R[13];
            if (previousStack < Memory.HeapEnd + 64)
            {
                throw new RuntimeFault("resource length scratch stack underflow");
            }
            uint lengthOutput = (previousStack - 8) & ~7u;
            Cpu.R[13] = lengthOutput;
            uint data = 0;
            uint length = 0;
            bool owned = false;
            try
            {
                Memory.Put32(lengthOutput, 0);
                data = ReadNativeResource(name, lengthOutput, 0, out owned, out Package? archive, exposeAddress: false);
                if (data == 0)
                {
                    result = default!;
                    return false;
                }
                length = Memory.U32(lengthOutput);
                if (owned && disposeAfterResourceRead)
                {
                    throw new ObjectDisposedException(nameof(RuntimeSession));
                }
                result = decode(this, data, length, ref owned);
                if (rememberScriptModule && !disposed && !disposeAfterResourceRead && result is string decoded)
                    RememberArchivedModule(name, archive, Memory.ReadSpan(data, length), decoded);
                return true;
            }
            finally
            {
                try
                {
                    if (owned && data != 0)
                    {
                        Cpu.Invoke(Memory.U32(table + 4), data, length);
                    }
                }
                finally
                {
                    Cpu.R[13] = previousStack;
                }
            }
        }

        private bool TryAdoptResource(uint address, uint size, ref bool owned)
        {
            if (!owned || Memory.HasWriteObservers || !ReferenceEquals(Cpu.HostCall, defaultHostCall) ||
                Memory.U32(table) != 0xf0000000u || Memory.U32(table + 4) != 0xf0000004u ||
                !Memory.TryAdoptNativeAllocation(address, size))
            {
                return false;
            }
            owned = false;
            return true;
        }

        private object?[] ResourceStringCommand(int operation, string name)
        {
            int terminator = name.IndexOf('\0');
            if (terminator >= 0)
            {
                name = name.Substring(0, terminator);
            }
            if (operation == 602)
            {
                return new object?[] { ReadNativeResource(name, 0, 1, out _, out _) == 0 ? null : (object)0 };
            }
            if (operation == 601)
            {
                string? content = ReadResource(name, static (RuntimeSession session, uint address, uint size, ref bool owned) =>
                    session.DecodeScriptBytes(session.Memory.ReadSpan(address, size)),
                    rememberScriptModule: name.EndsWith(".ext", StringComparison.OrdinalIgnoreCase));
                if (content != null && !disposed)
                {
                    resourceNames[content] = name;
                }
                return new object?[] { content };
            }
            uint previousStack = Cpu.R[13];
            if (previousStack < Memory.HeapEnd + 64)
            {
                throw new RuntimeFault("resource length scratch stack underflow");
            }
            uint lengthOutput = (previousStack - 8) & ~7u;
            Cpu.R[13] = lengthOutput;
            try
            {
                Memory.Put32(lengthOutput, 0);
                uint data = ReadNativeResource(name, lengthOutput, 2, out _, out _);
                return data == 0 ? new object?[] { null } :
                    new object?[] { unchecked((int)data), unchecked((int)Memory.U32(lengthOutput)) };
            }
            finally
            {
                // The mode-2 caller owns disk allocations; resident addresses remain borrowed.
                Cpu.R[13] = previousStack;
            }
        }

        // A script can discard an EXT string, run GC, and reconstruct its bytes later.
        // Each declared name has at most one fingerprint per immutable archive and one
        // for its latest writable override. Reading a new override preserves the original.
        private void RememberArchivedModule(string name, Package? archive, ReadOnlySpan<byte> content, string decoded)
        {
            if (archive != package && archive != rootPackage && archive != mythroad?.FlashPlugin)
                archive = null; // A temporary RAM or nested package can change on the next read.
            if (archive == null && !package.HasIndexedEntry(name) && !rootPackage.HasIndexedEntry(name) &&
                mythroad?.FlashPlugin.HasIndexedEntry(name) != true) return;
            int newest = archivedScriptModules.Count - 1;
            for (int i = newest; i >= 0; i--)
            {
                var entry = archivedScriptModules[i];
                if (entry.Name != name || entry.Archive != archive) continue;
                if (archive == null)
                {
                    WeakReference<string> previous = entry.OverrideText!;
                    // DecodeScriptBytes reuses a string only after checking the complete contents.
                    if (!previous.TryGetTarget(out string? lastText) || !ReferenceEquals(lastText, decoded))
                    {
                        entry.Digest = ResourceDigest(content);
                        entry.Size = content.Length;
                        previous.SetTarget(decoded);
                        archivedScriptModules[i] = entry;
                    }
                }
                if (i != newest)
                {
                    archivedScriptModules.RemoveAt(i);
                    archivedScriptModules.Add(entry);
                }
                return;
            }
            archivedScriptModules.Add((name, archive, content.Length, ResourceDigest(content),
                archive == null ? new WeakReference<string>(decoded) : null));
        }

        private static byte[] ResourceDigest(ReadOnlySpan<byte> content)
        {
            using var sha = SHA256.Create();
            var digest = new byte[32];
            sha.TryComputeHash(content, digest, out _);
            return digest;
        }

        private string ScriptModuleName(string content, byte[] image)
        {
            if (resourceNames.TryGetValue(content, out string? name)) return name;
            byte[]? digest = null;
            for (int i = archivedScriptModules.Count - 1; i >= 0; i--)
            {
                var entry = archivedScriptModules[i];
                if (entry.Size != content.Length) continue;
                digest ??= ResourceDigest(image);
                if (digest.AsSpan().SequenceEqual(entry.Digest)) return entry.Name;
            }
            return "script.ext";
        }

        private uint ProvideMemory(uint addressOutput, uint sizeOutput)
        {
            Memory.Check(addressOutput, 4);
            Memory.Check(sizeOutput, 4);
            const uint size = 4 * 1024 * 1024;
            uint allocation = Memory.NativeAllocate(size + 4);
            if (allocation == 0)
            {
                Memory.Put32(addressOutput, 0);
                Memory.Put32(sizeOutput, 0);
                Record("memory provider exhausted: MR_FAILED=-1");
                return uint.MaxValue;
            }
            Memory.Put32(allocation, size);
            providedMemory.Add(allocation + 4, size);
            Memory.Put32(addressOutput, allocation + 4);
            Memory.Put32(sizeOutput, size);
            return 0;
        }

        private uint ReleaseProvidedMemory(uint pointer)
        {
            if (!providedMemory.TryGetValue(pointer, out uint length))
            {
                throw new RuntimeFault("memory provider unknown or released allocation");
            }
            Memory.NativeFree(pointer - 4, length + 4);
            providedMemory.Remove(pointer);
            return 0;
        }

        private uint RegisterPackage(uint pointer, uint length, uint index)
        {
            if (index >= 50)
            {
                return uint.MaxValue;
            }
            if (pointer != 0)
            {
                Memory.Check(pointer, Math.Max(12u, length));
            }
            Memory.Put32(registeredPackages + index * 4, pointer);
            return 0;
        }

        private void ReplaceRamPackage(uint pointer, uint length)
        {
            uint addressCell = Memory.U32(table + 104 * 4);
            uint lengthCell = Memory.U32(table + 105 * 4);
            Memory.Check(addressCell, 4);
            Memory.Check(lengthCell, 4);
            if (pointer != 0 && length != 0)
            {
                Memory.Check(pointer, length);
            }
            uint previous = Memory.U32(addressCell);
            if (previous != 0)
            {
                Cpu.Invoke(Memory.U32(table + 4), previous, Memory.U32(lengthCell));
            }
            Memory.Put32(addressCell, pointer);
            Memory.Put32(lengthCell, length);
        }

        private string? ReadResidentBytes(uint first, uint index, int offset, int count)
        {
            uint pointer;
            if (first == '*')
            {
                pointer = index < 50 ? Memory.U32(registeredPackages + index * 4) : 0;
            }
            else
            {
                pointer = Memory.U32(Memory.U32(table + 104 * 4));
                if (first == '$' && count == -1)
                {
                    count = unchecked((int)Memory.U32(Memory.U32(table + 105 * 4)));
                }
            }
            if (pointer == 0)
            {
                return null;
            }
            if (count == 0)
            {
                return "";
            }
            long address = (long)pointer + offset;
            if (address < 0 || address > uint.MaxValue || count < 0)
            {
                throw new RuntimeFault("resident byte range overflow");
            }
            uint readAddress = (uint)address;
            Memory.Check(readAddress, (uint)count);
            if (Memory.TryGetAllocationRemaining(pointer, out uint sourceRemaining))
            {
                ulong sourceEnd = (ulong)pointer + sourceRemaining;
                if (!Memory.TryGetAllocationRemaining(readAddress, out uint readRemaining) ||
                    (ulong)readAddress + readRemaining != sourceEnd ||
                    (ulong)readAddress + (uint)count > sourceEnd)
                {
                    throw new RuntimeFault("resident byte window exceeds allocation");
                }
            }
            else if (Memory.TryGetAllocationRemaining(readAddress, out uint readRemaining) &&
                (uint)count > readRemaining)
            {
                throw new RuntimeFault("resident byte window exceeds allocation");
            }
            return ScriptChunk.Bytes.GetString(Memory.ReadSpan(readAddress, (uint)count));
        }

        private uint SetSmsBytes(int position, uint source, int length)
        {
            if (position < 0 || length < 0 || position >= SmsConfigurationSize || (long)position + length >= SmsConfigurationSize)
            {
                return uint.MaxValue;
            }
            if (length != 0)
            {
                byte[] bytes = Memory.Read(source, (uint)length);
                Memory.Write(SmsBuffer + (uint)position, bytes);
            }
            Memory.Put32(smsDirty, 1);
            return 0;
        }

        private uint AddSmsNumber(int index, string number)
        {
            if (number.Length > 21)
            {
                return uint.MaxValue;
            }
            string base64 = EncodeRuntimeBytes(ScriptChunk.Bytes.GetBytes(number));
            byte[] bytes = new byte[32];
            for (int offset = 0; offset < base64.Length; offset++)
            {
                bytes[offset] = (byte)base64[offset];
            }
            long position = 120L + 32L * index;
            if (position >= 0 && position + bytes.Length < SmsConfigurationSize)
            {
                Memory.Write(SmsBuffer + (uint)position, bytes);
                Memory.Put32(smsDirty, 1);
            }
            // The reference ignores smsSetBytes' failure here; keep the buffer untouched for an invalid range.
            return 0;
        }

        private uint LoadSmsConfiguration()
        {
            Memory.Put32(smsDirty, 0);
            Memory.Write(SmsBuffer, new byte[SmsConfigurationSize]);
            string path = SavePath("dsm.cfg");
            return FileOperation("load SMS configuration", () =>
            {
                int length = 0;
                if (storage.FileExists(path))
                {
                    using var stream = storage.Open(path, FileMode.Open, FileAccess.Read);
                    byte[] bytes = new byte[SmsConfigurationSize];
                    while (length < bytes.Length)
                    {
                        int count = stream.Read(bytes, length, bytes.Length - length);
                        if (count == 0)
                        {
                            break;
                        }
                        length += count;
                    }
                    Memory.Write(SmsBuffer, bytes);
                }
                if (length != SmsConfigurationSize)
                {
                    if (storage.FileExists(path))
                    {
                        if (!NativeWritable("dsm.cfg", path))
                        {
                            return uint.MaxValue;
                        }
                        using var writable = storage.Open(path, FileMode.OpenOrCreate, FileAccess.Write);
                    }
                    AddSmsNumber(0, "518869058");
                    AddSmsNumber(1, "918869058");
                    AddSmsNumber(3, "aa");
                }
                Record($"SMS configuration loaded {length} bytes; no SMS sent");
                return 0;
            });
        }

        private uint SaveSmsConfiguration()
        {
            if (Memory.U32(smsDirty) == 0)
            {
                return 0;
            }
            Memory.Put32(smsDirty, 0);
            string path = SavePath("dsm.cfg");
            if (!NativeWritable("dsm.cfg", path))
            {
                return uint.MaxValue;
            }
            byte[] bytes = Memory.Read(SmsBuffer, SmsConfigurationSize);
            return FileOperation("save SMS configuration", () =>
            {
                using var stream = storage.Open(path, FileMode.OpenOrCreate, FileAccess.Write);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                Record("SMS configuration saved 4320 bytes; no SMS sent");
                return 0;
            });
        }

        private bool SmsNumberAvailable(int index)
        {
            byte[] encoded = Memory.Read(SmsBuffer + 120 + (uint)index * 32, 32);
            int length = 0;
            while (length < 31 && encoded[length] != 0)
            {
                length++;
            }
            if (length < 4)
            {
                return false;
            }
            // The reference decoder consumes complete quartets only. At this
            // bound every successful decode has 1..21 bytes. Under simulated
            // SMS policy only its success is observable; no message is emitted.
            for (int position = 0; position < (length & ~3); position++)
            {
                if (encoded[position] != '=' && SmsAlphabet.IndexOf((char)encoded[position]) < 0)
                {
                    return false;
                }
            }
            return true;
        }

        private uint NotifySimChange(short type, uint oldImsi)
        {
            int network = NetworkId();
            if (type != 0 && type != 1)
            {
                return 0;
            }
            LoadSmsConfiguration();
            SaveSmsConfiguration();
            bool dedicatedNumber = Memory.U8(SmsBuffer + 5) >= 128;
            if (!dedicatedNumber && (network < 0 || network > 2 || !SmsNumberAvailable(network == 0 ? 0 : 1)))
            {
                Record("SIM notification: unavailable configured SMS number, MR_FAILED=-1");
                return uint.MaxValue;
            }
            if (oldImsi != 0)
            {
                Memory.Check(oldImsi, 16);
            }
            Record("SIM notification: simulated SMS success; no SMS sent");
            return 0;
        }

        private int ReceiveSms(string content, string sender, int encoding)
        {
            Memory.Put8(Memory.U32(table + 139 * 4), 0);
            uint state = Memory.U32(runtimeState);
            if ((state == 1 || state == 2 && TimerRunsWhilePaused) &&
                ScriptVm.IsFunction(Script.Globals["dealevent"]))
            {
                RunGuest(() => Script.Call(Script.Globals["dealevent"], 7, content, sender, encoding));
            }
            if (exitRequested || disposed)
            {
                return 1;
            }
            int result = 1;
            byte[]? decoded = DecodeIncomingSms(content, encoding);
            if (decoded != null)
            {
                result = ApplyIncomingSms(decoded, sender);
            }
            return Memory.U8(Memory.U32(table + 139 * 4)) != 0
                ? unchecked((int)Memory.U32(Memory.U32(table + 140 * 4))) : result;
        }

        private static byte[]? DecodeIncomingSms(string content, int encoding)
        {
            if (content.Length < 12 || content.Length > 160)
            {
                return null;
            }
            string encoded;
            if (encoding == 0 && content.StartsWith("MRPG", StringComparison.Ordinal))
            {
                encoded = content.Substring(4);
            }
            else
            {
                string start = encoding == 0 ? "~#^~" : "\0~\0#\0^\0~";
                string end = encoding == 0 ? "&^" : "\0&\0^";
                if (encoding != 0 && encoding != 1)
                {
                    return null;
                }
                int first = content.IndexOf(start, StringComparison.Ordinal);
                int last = first < 0 ? -1 : content.IndexOf(end, first + start.Length, StringComparison.Ordinal);
                if (last < 0)
                {
                    return null;
                }
                encoded = content.Substring(first + start.Length, last - first - start.Length);
                if (encoding == 1)
                {
                    var ascii = new char[Math.Min(encoded.Length / 2, 70)];
                    int count = 0;
                    for (int index = 0; index + 1 < encoded.Length && count < ascii.Length; index += 2)
                    {
                        if (encoded[index] != 0 || encoded[index + 1] == 0)
                        {
                            break;
                        }
                        ascii[count++] = encoded[index + 1];
                    }
                    encoded = new string(ascii, 0, count);
                }
            }
            if (encoded.Length < 4)
            {
                return null;
            }
            return DecodeRuntimeBytes(ScriptChunk.Bytes.GetBytes(encoded));
        }

        private int ApplyIncomingSms(byte[] decoded, string sender)
        {
            if (LoadSmsConfiguration() != 0)
            {
                return -1;
            }
            int result = 0;
            if (decoded.Length >= 2 && decoded[0] == 0xfc && decoded[1] == 0xfc)
            {
                int position = 2;
                while (position + 1 < decoded.Length)
                {
                    int length = decoded[position++];
                    if (length == 0 || length > decoded.Length - position)
                    {
                        result = -1;
                        break;
                    }
                    if (!ApplySmsChunk(decoded.AsSpan(position, length), sender))
                    {
                        result = -1;
                        break;
                    }
                    position += length;
                }
            }
            else if (decoded.Length >= 3 && decoded[0] == 0xfa && decoded[1] == 0xf2)
            {
                int index = decoded[2];
                if (index > 31 || decoded.Length > 120)
                {
                    result = -1;
                }
                else
                {
                    Memory.Put8(SmsBuffer + 4, 128);
                    Memory.Put8(SmsBuffer + 32 + (uint)index, 128);
                    var message = new byte[120];
                    decoded.CopyTo(message, 0);
                    Memory.Write(SmsBuffer + (uint)(120 * (index + 4)), message);
                    Memory.Put32(smsDirty, 1);
                }
            }
            if (SaveSmsConfiguration() != 0)
            {
                return -1;
            }
            return result;
        }

        private bool ApplySmsChunk(ReadOnlySpan<byte> chunk, string sender)
        {
            int code = chunk[0];
            switch (code)
            {
                case 1:
                    if (chunk.Length < 3 || chunk.Slice(2).IndexOf((byte)0) < 0)
                    {
                        return false;
                    }
                    int numberLength = chunk.Slice(2).IndexOf((byte)0);
                    if (numberLength > 21)
                    {
                        return false;
                    }
                    return AddSmsNumber(chunk[1], ScriptChunk.Bytes.GetString(chunk.Slice(2, numberLength).ToArray())) == 0;
                case 2:
                    if (chunk.Length < 2)
                    {
                        return false;
                    }
                    return WriteSmsChunk(120 + 32 * chunk[1], new byte[32]);
                case 3:
                    if (chunk.Length - 1 > 89)
                    {
                        return false;
                    }
                    byte[] url = new byte[120];
                    byte[] encoded = ScriptChunk.Bytes.GetBytes(EncodeRuntimeBytes(chunk.Slice(1).ToArray()));
                    encoded.CopyTo(url, 0);
                    if (!WriteSmsChunk(6, new byte[] { 128 }))
                    {
                        return false;
                    }
                    return WriteSmsChunk(360, url);
                case 4:
                    Record($"SMS reply to {sender}: simulated success; no SMS sent");
                    return true;
                case 5:
                    return chunk.Length >= 2 && WriteSmsChunk(5, chunk.Slice(1, 1).ToArray());
                case 6:
                    if (chunk.Length < 4 || chunk[3] > chunk.Length - 4)
                    {
                        return false;
                    }
                    return WriteSmsChunk(chunk[1] * 256 + chunk[2], chunk.Slice(4, chunk[3]).ToArray());
                case 7:
                case 17:
                case 27:
                case 37:
                    return true;
                default:
                    return false;
            }
        }

        private bool WriteSmsChunk(int position, byte[] bytes)
        {
            if (position < 0 || position + bytes.Length >= SmsConfigurationSize)
            {
                return false;
            }
            Memory.Write(SmsBuffer + (uint)position, bytes);
            Memory.Put32(smsDirty, 1);
            return true;
        }

        private static string EncodeRuntimeBytes(byte[] bytes)
        {
            const string standard = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
            char[] encoded = Convert.ToBase64String(bytes).ToCharArray();
            for (int i = 0; i < encoded.Length; i++)
            {
                if (encoded[i] != '=')
                {
                    encoded[i] = SmsAlphabet[standard.IndexOf(encoded[i])];
                }
            }
            return new string(encoded);
        }

        private static byte[]? DecodeRuntimeBytes(byte[] input)
        {
            if (input.Length == 0)
            {
                return Array.Empty<byte>();
            }
            if (input.Length < 4)
            {
                throw new RuntimeFault("base64 input shorter than four bytes");
            }
            int groups = input.Length / 4;
            int padding = input[input.Length - 2] == '=' ? 2 : input[input.Length - 1] == '=' ? 1 : 0;
            var output = new byte[groups * 3 - padding];
            Span<int> values = stackalloc int[4];
            Span<byte> decoded = stackalloc byte[3];
            for (int group = 0; group < groups; group++)
            {
                for (int index = 0; index < values.Length; index++)
                {
                    byte value = input[group * 4 + index];
                    values[index] = value == '=' ? 64 : SmsAlphabet.IndexOf((char)value);
                    if (values[index] < 0)
                    {
                        return null;
                    }
                }
                if (group == groups - 1)
                {
                    values.Slice(4 - padding, padding).Clear();
                }
                // '=' is accepted as value 64 even inside a group; its high bit does not
                // contribute to the preceding byte in the reference's separate byte writes.
                decoded[0] = (byte)(values[0] << 2 | (values[1] & 48) >> 4);
                decoded[1] = (byte)(values[1] << 4 | (values[2] & 60) >> 2);
                decoded[2] = (byte)(values[2] << 6 | (values[3] & 63));
                int count = Math.Min(3, output.Length - group * 3);
                decoded.Slice(0, count).CopyTo(output.AsSpan(group * 3));
            }
            return output;
        }

        private string? TransformStringCommand(int operation, uint length, Func<byte[]> readInput)
        {
            ulong capacity = operation == 501 ? (ulong)length * 4 / 3 + 8 : length;
            if (capacity > int.MaxValue)
            {
                throw new RuntimeFault("base64 allocation length overflow");
            }
            uint temporary = Cpu.Invoke(Memory.U32(table), stackalloc uint[] { (uint)capacity });
            if (temporary == 0)
            {
                return null;
            }
            try
            {
                byte[]? output = operation == 501 ? ScriptChunk.Bytes.GetBytes(EncodeRuntimeBytes(readInput()) + "\0") :
                    DecodeRuntimeBytes(readInput());
                if (output == null)
                {
                    return null;
                }
                Memory.Write(temporary, output);
                return ScriptChunk.Bytes.GetString(output, 0, output.Length - (operation == 501 ? 1 : 0));
            }
            finally
            {
                Cpu.Invoke(Memory.U32(table + 4), temporary, (uint)capacity);
            }
        }
    }
}
