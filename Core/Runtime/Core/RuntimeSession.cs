using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace CSharpMrp
{
    public enum GuestLanguage
    {
        Chinese = 1000,
        English = 1001
    }

    public sealed partial class RuntimeSession : IDisposable
    {
        private const int VirtualPathByteCapacity = 1024;
        private const string CompatibilityImei = "864086040622841";
        private const string CompatibilityImsi = "460019707327302";
        private const string CompatibilityModel = "vmrp";
        private const uint CompatibilityVersion = 101080182;

        public sealed class Module
        {
            public string Name = "";
            public string Origin = "guest-memory";
            public uint Address, Size, Helper, Context;
            public uint CopiedFrom;
            public long Instructions;
            public bool Active = true;
            private byte[]? executedBytes;

            public bool ExecutionTracked => executedBytes != null;

            public bool WasExecuted(uint address)
            {
                if (executedBytes == null || address < Address || (ulong)address >= (ulong)Address + Size)
                {
                    return false;
                }
                uint offset = address - Address;
                return (executedBytes[offset / 8] & (1 << (int)(offset % 8))) != 0;
            }

            internal void RecordInstruction(uint address, uint size)
            {
                executedBytes ??= new byte[((long)Size + 7) / 8];
                uint first = address - Address;
                uint length = Math.Min(size, Size - first);
                for (uint offset = first; offset < first + length; offset++)
                {
                    executedBytes[offset / 8] |= (byte)(1 << (int)(offset % 8));
                }
            }
        }

        public readonly GuestMemory Memory = new GuestMemory(isolatedVmStorage: true) { EmulateKeypad = true };
        public readonly ScriptVm Script = new ScriptVm();
        public readonly ArmCpu Cpu;
        private readonly Func<uint, ArmCpu, uint> defaultHostCall;
        public readonly List<Module> Modules = new List<Module>();
        public readonly List<string> Trace = new List<string>();
        public readonly Dictionary<int, int> HostCalls = new Dictionary<int, int>();
        public int Width { get; private set; }
        public int Height { get; private set; }
        public bool TrackExecutedBytes { get; set; }
        public uint Framebuffer
        {
            get; private set;
        }
        private uint framebufferCapacity;
        private byte[] displayedPixels;
        private int firstDirtyRow;
        private int endDirtyRow;
        private NativeTextWindow? publishedOverlay;
        private int publishedOverlayRevision;
        internal bool HasDirtyRows => firstDirtyRow < endDirtyRow;
        public int Frames
        {
            get; private set;
        }
        public int Keys
        {
            get; private set;
        }
        public int Timers
        {
            get; private set;
        }
        public int Touches
        {
            get; private set;
        }
        public uint ElapsedMilliseconds => milliseconds;
        public uint NextTimerMilliseconds => timerDelay;
        public bool TimerRunsWhilePaused => Memory.U32(timerFields + 8) != 0;
        public uint TimerGeneration
        {
            get; private set;
        }
        public uint TimerStopGeneration
        {
            get; private set;
        }
        public bool Started
        {
            get; private set;
        }
        private readonly Package package;
        private Package rootPackage;
        private string rootPackageFile = "";
        private byte[] rootPackageFileBytes;
        private readonly string saveRoot;
        private readonly string saveRootPrefix;
        private readonly string[] saveRootAncestors;
        private readonly IFileStorage storage;
        private readonly string packageFile;
        private readonly byte[] packageFileBytes;
        private readonly BitmapFont? font;
        private readonly MythroadFiles? mythroad;
        private readonly Encoding? textEncoding;
        private readonly Encoding filenameEncoding;
        private readonly GuestLanguage language;
        private readonly ISoundOutput? soundOutput;
        private readonly bool hostNetwork;
        private readonly bool trackModuleExecution;
        private readonly CancellationToken cancellation;
        private readonly Dictionary<uint, (uint Address, int Width, int Height)> glyphs =
            new Dictionary<uint, (uint, int, int)>();
        private readonly uint table;
        private readonly uint flags;
        private readonly uint timerFields;
        private readonly uint runtimeState;
        private readonly uint inflateFields;
        private readonly uint pathBuffer;
        private (string RawPrefix, string DecodedPrefix, string GuestPath) workingDirectory = ("", "", "c:/");
        private readonly Dictionary<string, string> resourceNames = new Dictionary<string, string>();
        private readonly Dictionary<string, uint> stringPointers = new Dictionary<string, uint>();
        private Dictionary<uint, (Stream Data, string Name)> files = new Dictionary<uint, (Stream, string)>();
        private readonly Dictionary<uint, (ReadOnlyMemory<string> Names, int Next)> searches =
            new Dictionary<uint, (ReadOnlyMemory<string>, int)>();
        private uint nextHandle = 1;
        private uint milliseconds;
        private int guestClockDepth;
        private uint guestClockTimestamp;
        private uint guestClockBaseline;
        private readonly Func<DateTime> localClock;
        private readonly Func<uint> uptimeClock;
        private bool sleepPolicyRecorded;
        private uint timerDelay;
        private string cachedTimerName = "";
        private byte[]? cachedTimerNameBytes;
        private Module? current;
        private Module? loading;
        private Module? registeredHelper;
        private uint registeredContextSize;
        private uint helperContextPatch;
        private bool disposed;
        private List<Exception>? disposeFailures;
        private bool exitRequested;
        private string? guestExceptionText;

        public RuntimeSession(Package package, string saveDirectory, int width = 240, int height = 320,
            string packageName = "application.mrp", BitmapFont? font = null, Encoding? textEncoding = null,
            GuestLanguage language = GuestLanguage.Chinese, Func<DateTime>? localClock = null,
            MythroadFiles? mythroad = null, uint randomSeed = 0x6d727031, ISoundOutput? soundOutput = null,
            bool hostNetwork = false, Func<uint>? uptimeClock = null, bool trackModuleExecution = true,
            CancellationToken cancellation = default, IFileStorage? storage = null)
        {
            if (language != GuestLanguage.Chinese && language != GuestLanguage.English)
            {
                throw new RuntimeFault($"unsupported guest language {(int)language}");
            }
            this.language = language;
            this.soundOutput = soundOutput;
            this.hostNetwork = hostNetwork;
            this.trackModuleExecution = trackModuleExecution;
            this.cancellation = cancellation;
            Script.Cancellation = cancellation;
            Script.InternString = CanonicalScriptString;
            SeedRandom(randomSeed);
            if (width <= 0 || height <= 0 || width > 1024 || height > 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }
            this.package = package;
            Memory.CompatibilityDiagnostic = Record;
            rootPackage = package;
            this.font = font;
            this.mythroad = mythroad;
            this.storage = storage ?? SystemFileStorage.Instance;
            this.textEncoding = textEncoding;
            filenameEncoding = (Encoding)(textEncoding ?? ScriptChunk.Bytes).Clone();
            filenameEncoding.EncoderFallback = EncoderFallback.ExceptionFallback;
            filenameEncoding.DecoderFallback = DecoderFallback.ExceptionFallback;
            this.localClock = localClock ?? (() => DateTime.Now);
            this.uptimeClock = uptimeClock ?? HostUptime;
            try
            {
                packageFileBytes = filenameEncoding.GetBytes(packageName);
                packageFile = ScriptChunk.Bytes.GetString(packageFileBytes);
            }
            catch (EncoderFallbackException)
            {
                throw new RuntimeFault("package filename is not representable in guest encoding");
            }
            if (packageFile.Length >= 128 || packageFile.IndexOf('\0') >= 0)
            {
                throw new RuntimeFault("package filename exceeds ABI byte capacity or contains NUL");
            }
            rootPackageFile = packageFile;
            rootPackageFileBytes = packageFileBytes;
            saveRoot = Path.GetFullPath(saveDirectory);
            int trimmedLength = saveRoot.AsSpan().TrimEnd(Path.DirectorySeparatorChar).Length;
            if (trimmedLength < saveRoot.Length)
            {
                int rootLength = Path.GetPathRoot(saveRoot.AsSpan()).Length;
                saveRoot = saveRoot.Substring(0, Math.Max(trimmedLength, rootLength));
            }
            saveRootPrefix = saveRoot[saveRoot.Length - 1] == Path.DirectorySeparatorChar
                ? saveRoot : saveRoot + Path.DirectorySeparatorChar;
            saveRootAncestors = GetSaveRootAncestors(saveRoot, saveRootPrefix);
            CheckLinks(saveRoot);
            this.storage.CreateDirectory(saveRoot);
            foreach (string drive in new[] { "a", "b", "x" })
            {
                this.storage.CreateDirectory(SavePath("drives/" + drive));
            }
            Width = width;
            Height = height;
            displayedPixels = new byte[width * height * 2];
            framebufferCapacity = (uint)Math.Max(width * height * 2, 480 * 800 * 2);
            Framebuffer = Memory.NativeAllocate(framebufferCapacity);
            if (Framebuffer == 0)
            {
                throw new RuntimeFault("screen buffer allocation limit");
            }
            Memory.Fill(Framebuffer, (uint)displayedPixels.Length, 0);
            // The reference vmrp Unicorn interpreter transfers at the original
            // byte address. This is an MRP compatibility profile, not ARMv7 hardware alignment.
            defaultHostCall = Dispatch;
            Cpu = new ArmCpu(Memory, lowAddressDataPage: true, allowUnalignedBlockTransfers: true)
            {
                // Real installers and benchmarks perform long synchronous calls.
                // Keep cancellation and finite limits without rejecting those workloads.
                InvocationBudget = int.MaxValue,
                Cancellation = cancellation,
                InvocationTimeLimitMilliseconds = 60000,
                HostCall = defaultHostCall,
                SemihostingCall = HandleSemihosting,
                DescribeAddress = Describe,
                InstructionExecuted = trackModuleExecution ? RecordExecution : (Action<uint>?)null,
                ExecutionEntered = EnterGuestClock,
                ExecutionExited = ExitGuestClock
            };
            if (!trackModuleExecution)
            {
                Record("per-module instruction statistics disabled; CPU instruction count and execution limits remain active");
            }
            Record("compatibility CPU data page: 0x00000000-0x00000fff, session-local and initially zero; host pointers remain strict; no instruction execution");
            Record("compatibility unaligned LDM/STM: reference vmrp byte addresses and original base writeback; memory bounds remain checked");
            Record("VM object storage: separate bounded 8 MiB arena after guest stack; guest heap metadata and bulk frees cannot release VM objects");
            table = Memory.AllocateStatic(150 * 4);
            for (uint i = 0; i < 150; i++)
            {
                Memory.Put32(table + i * 4, 0xf0000000 + i * 4);
            }
            uint internals = Memory.AllocateStatic(78 * 4);
            for (uint i = 0; i < 78; i++)
            {
                Memory.Put32(internals + i * 4, 0xf0010000 + i * 4);
            }
            Memory.Put32(internals + 77 * 4, 0);
            registeredPackages = Memory.AllocateStatic(50 * 4);
            Memory.Put32(internals, registeredPackages);
            inflateFields = Memory.AllocateStatic(16);
            for (uint slot = 7; slot <= 10; slot++)
            {
                Memory.Put32(internals + slot * 4, inflateFields + (slot - 7) * 4);
            }
            smsDirty = Memory.AllocateStatic(4);
            Memory.Put32(internals + 11 * 4, smsDirty);
            scriptState = Memory.AllocateStatic(4);
            uint stateCell = Memory.AllocateStatic(4);
            Memory.Put32(stateCell, scriptState);
            Memory.Put32(internals + 4, stateCell);
            flags = Memory.AllocateStatic(4);
            runtimeState = Memory.AllocateStatic(4);
            Memory.Put32(internals + 2 * 4, runtimeState);
            Memory.Put32(internals + 3 * 4, flags);
            timerFields = Memory.AllocateStatic(12);
            uint timerName = Memory.AllocateStatic(16);
            Memory.Write(timerName, ScriptChunk.Bytes.GetBytes("dealtimer\0"));
            Memory.Put32(timerFields, timerName);
            for (uint i = 0; i < 3; i++)
            {
                Memory.Put32(internals + (4 + i) * 4, timerFields + i * 4);
            }
            Memory.Put32(table + 23 * 4, internals);
            Memory.Put32(table + 24 * 4, Memory.AllocateStatic(16));
            uint display = Memory.AllocateStatic(16);
            uint[] values = { Framebuffer, (uint)width, (uint)height, 16 };
            for (uint i = 0; i < 4; i++)
            {
                Memory.Put32(display + i * 4, values[i]);
                Memory.Put32(table + (91 + i) * 4, display + i * 4);
            }
            int[] resourceSizes = { 31 * 16, 3 * 20, 3 * 4, 5 * 12, 10 * 2 };
            for (int index = 0; index < resourceSizes.Length; index++)
            {
                Memory.Put32(table + (uint)(95 + index) * 4, Memory.AllocateStatic((uint)resourceSizes[index]));
            }
            uint screenBitmap = Memory.U32(table + 95 * 4) + 30 * 16;
            for (uint index = 0; index < 3; index++)
            {
                uint tile = Memory.U32(table + 96 * 4) + index * 20;
                Memory.Put16(tile + 12, (uint)width);
                Memory.Put16(tile + 14, (uint)height);
            }
            Memory.Put16(screenBitmap, (uint)width);
            Memory.Put16(screenBitmap + 2, (uint)height);
            Memory.Put32(screenBitmap + 4, framebufferCapacity);
            Memory.Put32(screenBitmap + 12, Framebuffer);
            Memory.Put32(table + 106 * 4, Memory.AllocateStatic(4));
            Memory.Put32(table + 107 * 4, Memory.AllocateStatic(4));
            for (uint i = 0; i < 4; i++)
            {
                Memory.Put32(table + (108 + i) * 4, GuestMemory.HeapCells + i * 4);
            }
            string[] names = { packageFile, "start.mr", "", "" };
            for (uint i = 0; i < names.Length; i++)
            {
                if (names[i].Length >= 128)
                {
                    throw new RuntimeFault("runtime filename exceeds ABI buffer");
                }
                uint filename = Memory.AllocateStatic(128);
                Memory.Write(filename, ScriptChunk.Bytes.GetBytes(names[i] + "\0"));
                Memory.Put32(table + (100 + i) * 4, filename);
            }
            Memory.Put32(table + 21 * 4, 0);
            Memory.Put32(table + 66 * 4, 0);
            Memory.Put32(table + 149 * 4, 0);
            uint ramFile = Memory.AllocateStatic(8);
            Memory.Put32(table + 104 * 4, ramFile);
            Memory.Put32(table + 105 * 4, ramFile + 4);
            pathBuffer = Memory.AllocateStatic(VirtualPathByteCapacity);
            Memory.Put32(table + 135 * 4, GuestMemory.HeapMinimum);
            Memory.Put32(table + 136 * 4, GuestMemory.HeapTop);
            Memory.Put32(table + 146 * 4, GuestMemory.HeapHead);
            Memory.Put32(table + 112 * 4, Memory.AllocateStatic(120 * 36));
            Memory.Put32(table + 138 * 4, Memory.AllocateStatic(128));
            Memory.Put32(table + 139 * 4, Memory.AllocateStatic(1));
            Memory.Put32(table + 140 * 4, Memory.AllocateStatic(4));
            Memory.Put32(table + 142 * 4, Memory.AllocateStatic(4));
            Memory.Put32(table + 143 * 4, Memory.AllocateStatic(4));
            Memory.Put32(table + 144 * 4, Memory.AllocateStatic(128));
            scriptRegistry = Script.Registry;
            Script.CollectionRequested = () => CheckScriptAllocation(0);
            RegisterGlobals();
        }

        public void Start(string scriptName = "start.mr", string? entry = null, string? parameter = null)
        {
            Live();
            StartGuest(EncodeStartupText(scriptName), EncodeStartupText(entry ?? "_dsm"), EncodeStartupText(parameter ?? ""));
        }

        private void StartGuest(string guestScript, string guestEntry, string guestParameter)
        {
            Live();
            if (exitRequested)
            {
                throw new RuntimeFault("exited session requires a fresh session");
            }
            if (Started)
            {
                throw new RuntimeFault("session already started");
            }
            guestExceptionText = null;
            WriteRuntimeText(101, guestScript);
            WriteRuntimeText(138, guestParameter);
            WriteRuntimeText(144, guestEntry);
            guestScript = Memory.CString(Memory.U32(table + 101 * 4));
            Script.Globals["_mr_entry"] = guestEntry;
            Script.Globals["_mr_param"] = Memory.CString(Memory.U32(table + 138 * 4));
            Memory.Put32(runtimeState, 1);
            try
            {
                byte[]? startup = ReadMissingSdkStartup(guestScript);
                if (startup == null && (guestScript == "start.mr" || guestScript.EndsWith(".ext", StringComparison.OrdinalIgnoreCase)) &&
                    !package.Contains(guestScript) && package.Contains("logo.ext"))
                {
                    byte[] logo = package.Read("logo.ext", diagnostic: Record);
                    if (HasNativeModuleHeader(logo))
                    {
                        Record($"compatibility native startup: missing {guestScript}, using logo.ext");
                        guestScript = "logo.ext";
                        WriteRuntimeText(101, guestScript);
                        startup = logo;
                    }
                }
                startup ??= package.Read(guestScript, diagnostic: Record);
                RunGuest(() => Script.Execute(LoadSdkStartup(startup, guestScript)));
            }
            catch
            {
                Memory.Put32(runtimeState, 5);
                throw;
            }
            Started = !exitRequested && (Memory.U32(runtimeState) == 1 || Memory.U32(runtimeState) == 3);
            Record("start completed");
        }

        public void Key(int type, int key)
        {
            Live();
            if (!Started)
            {
                throw new RuntimeFault("key before startup");
            }
            uint state = Memory.U32(runtimeState);
            if (state == 3 || state == 2 && !TimerRunsWhilePaused)
            {
                return;
            }
            Memory.SetKeyState(type, key);
            if (HandleNativeMenuKey(type, key))
            {
                Keys++;
                if (Trace.Count < TraceLimit) Trace.Add($"menu key {type}:{key}");
                return;
            }
            uint hookStatus = InvokeGuestHook(eventHook, stackalloc uint[] { (uint)type, (uint)key, 0u });
            if (!Started)
            {
                return;
            }
            if (hookStatus == 1 && !HandleNativeKey(type, key) && ScriptVm.IsFunction(Script.Globals["dealevent"]))
            {
                RunScriptInput(type, key, 0);
            }
            Keys++;
            if (Trace.Count < TraceLimit) Trace.Add($"key {type}:{key}");
        }

        public void SetPaused(bool paused)
        {
            Live();
            uint state = Memory.U32(runtimeState);
            if (paused && state == 1)
            {
                Memory.ReleaseKeys();
                Memory.Put32(runtimeState, 2);
                Record("host paused runtime");
                if (InvokeGuestHook(pauseHook) == 1)
                {
                    object? callback = Script.Globals["suspend"];
                    if (ScriptVm.IsFunction(callback))
                    {
                        RunScriptCallback(callback);
                    }
                    if (!TimerRunsWhilePaused && Memory.U32(timerFields + 4) == 1)
                    {
                        timerDelay = 0;
                        Memory.Put32(timerFields + 4, 2);
                        Record("host suspended timer");
                    }
                }
            }
            else if (!paused && state == 2)
            {
                Memory.Put32(runtimeState, 1);
                Record("host resumed runtime");
                if (InvokeGuestHook(resumeHook) == 1)
                {
                    object? callback = Script.Globals["resume"];
                    if (ScriptVm.IsFunction(callback))
                    {
                        RunScriptCallback(callback);
                    }
                    if (Memory.U32(timerFields + 4) == 2)
                    {
                        timerDelay = 300;
                        Memory.Put32(timerFields + 4, 1);
                        TimerGeneration++;
                        Record("host resumed suspended timer for 300 ms");
                    }
                }
            }
            else if (paused && state == 3)
            {
                timerDelay = 0;
                Memory.Put32(timerFields + 4, 0);
                Record("host paused pending restart");
            }
            else if (!paused && state == 3 && timerDelay == 0)
            {
                timerDelay = 100;
                Memory.Put32(timerFields + 4, 1);
                TimerGeneration++;
                Record("host resumed pending restart");
            }
        }

        public void Tick()
        {
            Live();
            if (timerDelay == 0)
            {
                throw new RuntimeFault("no armed timer");
            }
            milliseconds += timerDelay;
            timerDelay = 0;
            Memory.Put32(timerFields + 4, 0);
            if (Memory.U32(runtimeState) == 2 && !TimerRunsWhilePaused)
            {
                Record("timer ignored during pause");
                return;
            }
            if (Memory.U32(runtimeState) == 3)
            {
                Timers++;
                RestartRequestedFile();
                return;
            }
            uint hookStatus = InvokeGuestHook(timerHook);
            if (!Started)
            {
                return;
            }
            if (hookStatus == 1)
            {
                object? handler = Script.Globals[ReadTimerName()];
                if (ScriptVm.IsFunction(handler))
                {
                    RunScriptCallback(handler);
                }
            }
            Timers++;
            Record("timer delivered");
        }

        private string ReadTimerName() =>
            ReadCachedCString(Memory.U32(timerFields), ref cachedTimerName, ref cachedTimerNameBytes);

        private string ReadCachedCString(uint address, ref string cachedName, ref byte[]? cachedBytes)
        {
            ReadOnlySpan<byte> bytes = Memory.CStringBytes(address);
            if (bytes.SequenceEqual(cachedBytes.AsSpan(0, cachedName.Length))) return cachedName;
            string name = ScriptChunk.Bytes.GetString(bytes);
            // Keep only short names; the guest may point at arbitrarily large strings.
            if (bytes.Length <= 128)
            {
                bytes.CopyTo(cachedBytes ??= new byte[128]);
                cachedName = name;
            }
            return name;
        }

        public void Touch(int type, int x, int y)
        {
            Live();
            if (!Started || (type != 2 && type != 3 && type != 12) || x < 0 || x >= Width || y < 0 || y >= Height)
            {
                throw new RuntimeFault("invalid touch event or session state");
            }
            uint state = Memory.U32(runtimeState);
            if (state == 3 || state == 2 && !TimerRunsWhilePaused)
            {
                return;
            }
            if (HandleNativeMenuTouch(type, x, y))
            {
                Touches++;
                if (Trace.Count < TraceLimit) Trace.Add($"menu touch {type}:{x},{y}");
                return;
            }
            uint hookStatus = InvokeGuestHook(eventHook, stackalloc uint[] { (uint)type, (uint)x, (uint)y });
            if (!Started)
            {
                return;
            }
            if (hookStatus == 1 && !HandleNativeTouch(type, x, y) && ScriptVm.IsFunction(Script.Globals["dealevent"]))
            {
                RunScriptInput(type, x, y);
            }
            Touches++;
            if (Trace.Count < TraceLimit) Trace.Add($"touch {type}:{x},{y}");
        }

        private void RunScriptInput(int type, int first, int second) =>
            RunGuest(() => Script.Call(Script.Globals["dealevent"], type, first, second));

        public byte[] Pixels()
        {
            byte[] pixels = new byte[displayedPixels.Length];
            CopyPixelsTo(pixels);
            return pixels;
        }

        public void CopyPixelsTo(byte[] destination)
        {
            if (destination == null || destination.Length != displayedPixels.Length)
            {
                throw new ArgumentException("display buffer must match the current screen size", nameof(destination));
            }
            Buffer.BlockCopy(displayedPixels, 0, destination, 0, destination.Length);
            RenderNativeText(destination);
        }

        // The worker keeps this destination across publications. Standalone snapshots
        // still use CopyPixelsTo and never consume the accumulated rows.
        internal void CopyFrameChangesTo(byte[] destination, bool fullCopy, out int firstRow, out int endRow)
        {
            NativeTextWindow? overlay = NativeText ?? nativeMenuView;
            firstRow = firstDirtyRow;
            endRow = endDirtyRow;
            int revision = overlay?.RenderRevision ?? 0;
            if (fullCopy || overlay != publishedOverlay || revision != publishedOverlayRevision)
            {
                if (overlay != null)
                    RenderNativeText(destination);
                else
                    Buffer.BlockCopy(displayedPixels, 0, destination, 0, displayedPixels.Length);
                firstRow = 0;
                endRow = Height;
            }
            else if (overlay != null)
            {
                // The opaque overlay already occupies the worker's retained frame.
                firstRow = endRow = 0;
            }
            else if (firstRow < endRow)
            {
                int stride = Width * 2;
                int offset = firstRow * stride;
                Buffer.BlockCopy(displayedPixels, offset, destination, offset, (endRow - firstRow) * stride);
            }
            publishedOverlay = overlay;
            publishedOverlayRevision = revision;
            firstDirtyRow = endDirtyRow = 0;
        }

        public uint LoadExt(byte[] code, string name, int argument = 0)
        {
            Live();
            if (code.Length < 12)
            {
                throw new RuntimeFault($"EXT {name}: truncated header");
            }
            var module = new Module { Name = name, Origin = "LoadExt:" + name, Address = AllocateExtCode(code), Size = (uint)code.Length };
            return Initialize(module, argument);
        }

        private uint LoadExtWithPatch(byte[] code, string name, int argument, uint patchAddress)
        {
            if (code.Length < 12)
            {
                throw new RuntimeFault($"EXT {name}: truncated header");
            }
            if (patchAddress == 0)
            {
                throw new RuntimeFault("EXT external patch pointer is null");
            }
            Memory.Check(patchAddress, 8);
            if (Memory.TryGetAllocationRemaining(patchAddress, out uint patchRemaining) && patchRemaining < 8)
            {
                throw new RuntimeFault("EXT external patch crosses allocation");
            }
            var module = new Module { Name = name, Origin = "LoadExt:" + name,
                Address = AllocateExtCode(code), Size = (uint)code.Length };
            helperContextPatch = patchAddress;
            return Initialize(module, argument, patchAddress);
        }

        private uint AllocateExtCode(byte[] code)
        {
            uint address = Memory.NativeAllocate((uint)code.Length);
            if (address == 0)
            {
                throw new RuntimeFault("EXT code allocation limit");
            }
            Memory.Write(address, code);
            return address;
        }

        private uint Initialize(Module module, int argument, uint patchAddress = 0)
        {
            foreach (var existing in Modules)
            {
                if (existing.Active && existing.Address == module.Address && existing.Size == module.Size)
                {
                    module = existing;
                    break;
                }
            }
            Memory.Put32(patchAddress == 0 ? module.Address : patchAddress, table);
            ApplyOriginalSdkCompatibility(module.Address, module.Size, module.Name);
            if (!Modules.Contains(module))
            {
                Modules.Add(module);
            }
            Module? outer = loading;
            loading = module;
            try
            {
                Record($"load {module.Name} @0x{module.Address:x8}");
                uint result = Cpu.Invoke(module.Address + 8, stackalloc uint[] { (uint)argument });
                if (module.Helper == 0)
                {
                    throw new RuntimeFault($"EXT {module.Name}: no helper registered");
                }
                current = module;
                return result;
            }
            finally
            {
                loading = outer;
            }
        }

        private object?[] Helper(int code, byte[] input)
        {
            uint previousStack = Cpu.R[13];
            ulong length = ((ulong)Math.Max(input.Length, 1) + 7) & ~7ul;
            if ((ulong)Memory.HeapEnd + length + 64 <= previousStack)
            {
                uint scratch = previousStack - (uint)length;
                Memory.Write(scratch, input);
                Cpu.R[13] = scratch;
                try
                {
                    return Helper(code, scratch, (uint)input.Length);
                }
                finally
                {
                    Cpu.R[13] = previousStack;
                }
            }
            uint inputAddress = Memory.CopyIn(input);
            try
            {
                return Helper(code, inputAddress, (uint)input.Length);
            }
            finally
            {
                Memory.Free(inputAddress);
            }
        }

        private object?[] Helper(int code, uint inputAddress, uint inputLength)
        {
            Module module = current ?? throw new RuntimeFault("no active EXT helper");
            Memory.Check(inputAddress, inputLength);
            uint previousStack = Cpu.R[13];
            if (previousStack < 8)
            {
                throw new RuntimeFault("EXT helper stack underflow");
            }
            uint output = (previousStack - 8) & ~7u;
            Memory.Check(output, 8);
            Memory.Put32(output, 0);
            Memory.Put32(output + 4, 0);
            Cpu.R[13] = output;
            try
            {
                if (Trace.Count < TraceLimit) Trace.Add($"helper {module.Name} code={code}");
                uint result = Cpu.Invoke(module.Helper, stackalloc uint[]
                {
                    module.Context, (uint)code, inputAddress, inputLength, output, output + 4
                });
                uint pointer = Memory.U32(output);
                uint size = Memory.U32(output + 4);
                string text = size == 0 ? "" : ScriptChunk.Bytes.GetString(Memory.ReadSpan(pointer, size));
                return new object?[] { text, (int)result };
            }
            finally
            {
                Cpu.R[13] = previousStack;
            }
        }

        private void RegisterGlobals()
        {
            RegisterResourceGlobals();
            RegisterTableGlobals();
            RegisterDrawingGlobals();
            RegisterTileGlobals();
            RegisterNetworkGlobals();
            Script.Globals["RunFile"] = new ScriptVm.Native(ScheduleRunFile);
            Script.Globals["_runFile"] = Script.Globals["RunFile"];
            Script.Globals["Exit"] = new ScriptVm.Native(_ =>
            {
                string oldPackage = Memory.CString(Memory.U32(table + 102 * 4));
                if (oldPackage.Length != 0)
                {
                    return ScheduleRunFile(new object?[] { oldPackage,
                        Memory.CString(Memory.U32(table + 103 * 4)), Memory.CString(Memory.U32(table + 138 * 4)) });
                }
                RequestExit();
                return Array.Empty<object?>();
            });
            Script.Globals["print"] = new ScriptVm.Native(args =>
            {
                var line = new StringBuilder();
                bool first = true;
                object? converter = Script.Globals["tostring"];
                foreach (object? value in args)
                {
                    object?[] converted = Script.Call(converter, value);
                    if (converted.Length == 0 || !(ScriptVm.IsString(converted[0]) || converted[0] is int))
                    {
                        throw new RuntimeFault("tostring must return a string to print");
                    }
                    string part = ScriptVm.Text(converted[0]);
                    int terminator = part.IndexOf('\0');
                    if (terminator >= 0)
                    {
                        part = part.Substring(0, terminator);
                    }
                    if (part.Length > 190)
                    {
                        throw new RuntimeFault("print string too long");
                    }
                    if (line.Length + part.Length + (first ? 0 : 1) > 4096)
                    {
                        throw new RuntimeFault("print output limit");
                    }
                    if (!first)
                    {
                        line.Append('\t');
                    }
                    line.Append(part);
                    first = false;
                }
                Record("print: " + line);
                return Array.Empty<object?>();
            });
            Script.Globals["_strCom"] = new ScriptVm.Native(StringCommand);
            Script.Globals["_com"] = new ScriptVm.Native(args =>
            {
                int operation = args.Length > 0 && ScriptInteger(args[0], out int code) ? code : 0;
                int value = args.Length > 1 && ScriptInteger(args[1], out int number) ? number : 0;
                if (operation == 402)
                {
                    string mode = args.Length > 1 && ScriptVm.IsString(args[1]) && !ScriptInteger(args[1], out _)
                        ? ScriptVm.Text(args[1]) : "cmnet";
                    return Script.Call(Script.Globals["_initNet"], mode);
                }
                if (operation == 7)
                {
                    if (value < 0 || value > args.Length)
                    {
                        throw new RuntimeFault("script glyph command invalid result count");
                    }
                    var results = new object?[value];
                    Array.Copy(args, args.Length - value, results, 0, value);
                    return results;
                }
                if (operation == 501 || operation == 502)
                {
                    int length = args.Length > 2 && ScriptInteger(args[2], out int count) ? count : 0;
                    if (operation == 502 && (args.Length < 4 || args[3] == null))
                    {
                        return new object?[] { 0 };
                    }
                    if (value < 0 || length < 0 || (long)value + length >= SmsConfigurationSize)
                    {
                        if (operation == 501)
                        {
                            throw new RuntimeFault("SMS configuration range exceeds buffer");
                        }
                        return new object?[] { -1 };
                    }
                    if (operation == 501)
                    {
                        return new object?[] { ScriptChunk.Bytes.GetString(Memory.ReadSpan(SmsBuffer + (uint)value, (uint)length)) };
                    }
                    string source = ScriptVm.Text(args[3]);
                    if (source.Length < length)
                    {
                        throw new RuntimeFault("SMS configuration source too short");
                    }
                    uint pointer = length == 0 ? 0 : Memory.CopyIn(ScriptChunk.Bytes.GetBytes(source.Substring(0, length)));
                    try
                    {
                        return new object?[] { unchecked((int)SetSmsBytes(value, pointer, length)) };
                    }
                    finally
                    {
                        if (pointer != 0)
                        {
                            Memory.Free(pointer);
                        }
                    }
                }
                return new object?[] { Command(operation, value) };
            });
            Script.Globals["TestCom"] = Script.Globals["_com"];
            Script.Globals["GetNetworkID"] = new ScriptVm.Native(_ => new object?[] { NetworkId() });
            Script.Globals["GetSysInfo"] = new ScriptVm.Native(args =>
            {
                uint selector = unchecked((ushort)Number(args, 0, 1));
                if (selector > 2)
                {
                    throw new RuntimeFault("unsupported font selector");
                }
                var info = new ScriptTable();
                info["vmver"] = 1968;
                var surface = DrawingSurface();
                info["scrw"] = surface.Width;
                info["scrh"] = surface.Height;
                info["ScreenW"] = surface.Width;
                info["ScreenH"] = surface.Height;
                info["packname"] = Memory.CString(Memory.U32(table + 100 * 4));
                info["PackName"] = info["packname"];
                info["IMEI"] = CompatibilityImei + "\0";
                info["IMSI"] = CompatibilityImsi + "\0";
                info["hsman"] = CompatibilityModel;
                info["hstype"] = CompatibilityModel;
                info["hsver"] = (int)CompatibilityVersion;
                if (font != null)
                {
                    byte[] metrics = FontMetrics(selector);
                    info["chw"] = (int)metrics[0];
                    info["ChineseWidth"] = info["chw"];
                    info["chh"] = (int)metrics[1];
                    info["ChineseHigh"] = info["chh"];
                    info["ascw"] = (int)metrics[2];
                    info["EnglishWidth"] = info["ascw"];
                    info["asch"] = (int)metrics[3];
                    info["EnglishHigh"] = info["asch"];
                }
                return new object?[] { info };
            });
            var system = new ScriptTable();
            system["getInfo"] = Script.Globals["GetSysInfo"];
            RegisterSystemFiles(system);
            Script.Globals["sys"] = system;
            Script.Globals["_gc"] = new ScriptVm.Native(args =>
            {
                SetGcThreshold(Number(args, 0));
                return Array.Empty<object?>();
            });
            var strings = new ScriptTable();
            strings["subV"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 1);
                if (args[0] is ScriptMutableString mutable)
                {
                    return new object?[] { unchecked((int)mutable.Pointer), mutable.Length };
                }
                ScriptMutableString value = MutableString(args[0]);
                return new object?[] { (int)value.Pointer, value.Length };
            });
            strings["sub"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 2);
                string value = ScriptVm.Text(args[0]);
                long first = ScriptVm.Number(args[1]);
                long last = args.Length > 2 && args[2] != null ? ScriptVm.Number(args[2]) : -1;
                first = Math.Max(1, first < 0 ? value.Length + first + 1 : first);
                last = Math.Min(value.Length, last < 0 ? value.Length + last + 1 : last);
                return new object?[] { first > last ? "" : value.Substring((int)first - 1, (int)(last - first + 1)) };
            });
            strings["byte"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 1);
                string value = ScriptVm.Text(args[0]);
                long index = args.Length > 1 && args[1] != null ? ScriptVm.Number(args[1]) : 1;
                index = index < 0 ? value.Length + index + 1 : index;
                return index < 1 || index > value.Length ? Array.Empty<object?>() : new object?[] { (int)value[(int)index - 1] };
            });
            strings["pack"] = new ScriptVm.Native(ScriptPack);
            strings["unpack"] = new ScriptVm.Native(ScriptUnpack);
            strings["packLen"] = new ScriptVm.Native(ScriptPackLength);
            RegisterStringPrimitives(strings);
            Script.Globals["string"] = strings;
            RegisterFileGlobals();
            RegisterGuiGlobals();
            var store = new ScriptTable();
            void CheckStoreArguments(object?[] args)
            {
                if (args.Length < 2 || !(args[0] is ScriptTable permanents) || permanents.TryNext(null, out _, out _))
                {
                    throw new RuntimeFault("store requires an empty permanents table and a value");
                }
            }
            store["store"] = new ScriptVm.Native(args =>
            {
                CheckStoreArguments(args);
                using var encoded = ScriptStore.EncodeBuffer(args[1]);
                return new object?[] { ScriptChunk.Bytes.GetString(encoded.Bytes.AsSpan(0, encoded.Length)) };
            });
            store["load"] = new ScriptVm.Native(args =>
            {
                CheckStoreArguments(args);
                return new[] { ScriptStore.DecodeScriptValue(args[1]) };
            });
            Script.Globals["_store"] = store;
            var tables = (ScriptTable)Script.Globals["table"]!;
            tables["save"] = new ScriptVm.Native(args =>
            {
                CheckStoreArguments(args);
                if (!(args[1] is ScriptTable))
                {
                    throw new RuntimeFault("table.save requires a table root");
                }
                return Script.Call(store["store"], args);
            });
            tables["load"] = store["load"];
            Script.Globals["SaveTable"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 3);
                CheckStoreArguments(args);
                string name = ScriptFileName(args, 2);
                using var encoded = ScriptStore.EncodeBuffer(args[1]);
                return SaveScriptTableFile(name, encoded.Bytes, encoded.Length) ? new object?[] { 0 } : Array.Empty<object?>();
            });
            Script.Globals["LoadTable"] = new ScriptVm.Native(args =>
            {
                RequireScriptArguments(args, 2);
                if (!(args[0] is ScriptTable permanents) || permanents.TryNext(null, out _, out _))
                {
                    throw new RuntimeFault("store requires an empty permanents table");
                }
                return new[] { LoadScriptTableFile(ScriptFileName(args, 1)) };
            });
            RegisterBaseGlobals();
            RegisterServiceGlobals();
        }

        private static int ParseDecimal(string value)
        {
            int index = 0;
            while (index < value.Length && (value[index] == ' ' || value[index] >= '\t' && value[index] <= '\r'))
            {
                index++;
            }
            bool negative = index < value.Length && value[index] == '-';
            if (index < value.Length && (value[index] == '+' || value[index] == '-'))
            {
                index++;
            }
            long result = 0;
            long limit = negative ? 2147483648L : int.MaxValue;
            while (index < value.Length && value[index] >= '0' && value[index] <= '9')
            {
                result = result * 10 + value[index++] - '0';
                if (result > limit)
                {
                    throw new RuntimeFault("atoi integer overflow");
                }
            }
            return (int)(negative ? -result : result);
        }

        private int NetworkId()
        {
            Record("fixed emulated network ID: Mobile=0; no host carrier queried");
            return 0;
        }

        private byte[] FontMetrics(uint selector)
        {
            if (font == null)
            {
                throw new RuntimeFault("font metrics require an explicit bitmap font");
            }
            font.Measure('点', selector, out int chineseWidth, out int chineseHeight);
            font.Measure('2', selector, out int asciiWidth, out int asciiHeight);
            return new[] { (byte)chineseWidth, (byte)chineseHeight, (byte)asciiWidth, (byte)asciiHeight };
        }

        private object?[] StringCommand(object?[] args)
        {
            int operation = ScriptVm.Number(args[0]);
            int code = (operation == 800 || operation == 801) && args.Length > 2 ? ScriptVm.Number(args[2]) : 0;
            switch (operation)
            {
                case 2:
                    byte[] ramBytes = ScriptChunk.Bytes.GetBytes(TextArgument(args, 1));
                    if (ramBytes.Length > Package.MaxBytes)
                    {
                        throw new RuntimeFault("RAM package exceeds size limit");
                    }
                    uint ramPointer = ramBytes.Length == 0 ? 0 : Memory.NativeAllocate((uint)ramBytes.Length);
                    if (ramBytes.Length != 0 && ramPointer == 0)
                    {
                        throw new RuntimeFault("RAM package allocation limit");
                    }
                    try
                    {
                        if (ramPointer != 0)
                        {
                            Memory.Write(ramPointer, ramBytes);
                        }
                        ReplaceRamPackage(ramPointer, (uint)ramBytes.Length);
                    }
                    catch
                    {
                        Memory.NativeFree(ramPointer, (uint)ramBytes.Length);
                        throw;
                    }
                    return new object?[] { 0 };
                case 9:
                    return Array.Empty<object?>();
                case 5:
                    guestExceptionText = TextArgument(args, 1);
                    return new object?[] { 0 };
                case 6:
                    TextArgument(args, 1);
                    guestExceptionText = null;
                    return new object?[] { 0 };
                case 100:
                    if (!(Script.Globals["socket"] is ScriptTable socket) || socket["getHost"] == null)
                    {
                        throw new RuntimeFault("socket table unavailable for DNS command");
                    }
                    return Script.Call(socket["getHost"], ScriptVm.Text(args[1]));
                case 3:
                    WriteRuntimeText(102, args.Length > 1 && args[1] != null ? ScriptVm.Text(args[1]) : "");
                    WriteRuntimeText(103, args.Length > 2 && args[2] != null ? ScriptVm.Text(args[2]) : "start.mr");
                    return new object?[] { 0 };
                case 4:
                    WriteRuntimeText(138, args.Length > 1 && args[1] != null ? ScriptVm.Text(args[1]) : "");
                    return new object?[] { 0 };
                case 200:
                    byte[] checksumInput = ScriptChunk.Bytes.GetBytes(ScriptVm.Text(args[1]));
                    UpdateCrc(0, 0);
                    foreach (byte value in checksumInput)
                    {
                        AppendCrcByte(value);
                    }
                    return new object?[] { unchecked((int)(nativeCrc ^ uint.MaxValue)) };
                case 300:
                    byte[] packedInput = ScriptChunk.Bytes.GetBytes(TextArgument(args, 1));
                    return new object?[] { ScriptChunk.Bytes.GetString(Package.ReadPackedBytes(packedInput, Record)) };
                case 500:
                    return new object?[] { ScriptChunk.Bytes.GetString(GuestMd5.Digest(ScriptChunk.Bytes.GetBytes(ScriptVm.Text(args[1])))) };
                case 501:
                case 502:
                    string encodedInput = ScriptVm.Text(args[1]);
                    string? transformed = TransformStringCommand(operation, (uint)encodedInput.Length,
                        () => ScriptChunk.Bytes.GetBytes(encodedInput));
                    return transformed == null ? Array.Empty<object?>() : new object?[] { transformed };
                case 600:
                    string selector = ScriptVm.Text(args[1]);
                    uint first = selector.Length == 0 ? 0u : selector[0];
                    uint index = selector.Length < 2 ? uint.MaxValue : unchecked((uint)(selector[1] - 'A'));
                    ScriptInteger(args.Length > 2 ? args[2] : null, out int offset);
                    ScriptInteger(args.Length > 3 ? args[3] : null, out int count);
                    string? resident = ReadResidentBytes(first, index, offset, count);
                    return resident == null ? Array.Empty<object?>() : new object?[] { resident };
                case 601:
                case 602:
                case 603:
                    return ResourceStringCommand(operation, ScriptVm.Text(args[1]));
                case 700:
                    byte[] oldImsi = new byte[16];
                    byte[] suppliedImsi = ScriptChunk.Bytes.GetBytes(ScriptVm.Text(args[1]));
                    Array.Copy(suppliedImsi, oldImsi, Math.Min(oldImsi.Length, suppliedImsi.Length));
                    uint imsiPointer = Memory.CopyIn(oldImsi);
                    try
                    {
                        int simType = args.Length > 2 && ScriptInteger(args[2], out int parsedType) ? parsedType : 0;
                        return new object?[] { unchecked((int)NotifySimChange(unchecked((short)simType), imsiPointer)) };
                    }
                    finally
                    {
                        Memory.Free(imsiPointer);
                    }
                case 701:
                    string smsContent = TextArgument(args, 1);
                    string smsSender = TextArgument(args, 2);
                    int smsEncoding = args.Length > 3 && ScriptInteger(args[3], out int parsedEncoding)
                        ? parsedEncoding : 0;
                    return new object?[] { ReceiveSms(smsContent, smsSender, smsEncoding) };
                case 900:
                    TextArgument(args, 1);
                    return new object?[] { unchecked((int)Cpu.Invoke(0xf0000000 + 38 * 4,
                        stackalloc uint[] { 200001u, Memory.U32(table + 24 * 4), 16u, 0u, 0u, 0u })) };
                case 802:
                    byte[] externalExt = ScriptChunk.Bytes.GetBytes(TextArgument(args, 1));
                    int externalCode = args.Length > 2 && ScriptInteger(args[2], out int parsedCode)
                        ? parsedCode : 0;
                    if (args.Length <= 3 || !ScriptInteger(args[3], out int parsedPatch))
                    {
                        throw new RuntimeFault("EXT external patch pointer is required");
                    }
                    return new object?[] { unchecked((int)LoadExtWithPatch(externalExt,
                        "script-external.ext", externalCode, unchecked((uint)parsedPatch))) };
                case 800:
                    if (args[1] is ScriptTable addressPair)
                    {
                        uint address = (uint)ScriptVm.Number(addressPair[1]);
                        uint size = (uint)ScriptVm.Number(addressPair[2]);
                        Memory.Check(address, size);
                        if (size < 12)
                        {
                            throw new RuntimeFault("EXT address pair has invalid size");
                        }
                        return new object?[] { (int)Initialize(new Module
                        {
                            Name = "guest-loaded.ext", Address = address, Size = size
                        }, code) };
                    }
                    string bytes = ScriptVm.Text(args[1]);
                    byte[] image = ScriptChunk.Bytes.GetBytes(bytes);
                    return new object?[] { (int)LoadExt(image, ScriptModuleName(bytes, image), code) };
                case 801:
                    byte[] input;
                    if (args[1] is ScriptTable integers)
                    {
                        using var stream = new MemoryStream();
                        using var writer = new BinaryWriter(stream);
                        for (int i = 1; integers[i] != null; i++)
                        {
                            writer.Write(ScriptVm.Number(integers[i]));
                        }
                        input = stream.ToArray();
                    }
                    else
                    {
                        input = ScriptChunk.Bytes.GetBytes(ScriptVm.Text(args[1]));
                    }
                    return Helper(code, input);
                default:
                    throw new RuntimeFault($"unsupported _strCom {operation}");
            }
        }

        private int Command(int operation, int value)
        {
            if (operation == 1)
            {
                return unchecked((int)ReadRuntimeClock());
            }
            if (operation == 300 || operation == 301)
            {
                Memory.Put8(Memory.U32(table + (uint)(operation == 300 ? 106 : 107) * 4), (byte)value);
                return 0;
            }
            if (operation == 200)
            {
                if (Memory.U32(runtimeState) == 1 && Memory.U8(Memory.U32(table + 107 * 4)) != 0)
                {
                    Record($"fixed emulated vibration: {value} ms, MR_SUCCESS=0; no host vibration");
                }
                return 0;
            }
            if (operation == 201)
            {
                // The reference command leaves its initialized success result unchanged.
                return 0;
            }
            if (operation == 400)
            {
                // Match the existing Unity host policy: never block its main thread.
                return 0;
            }
            if (operation >= 302 && operation <= 305)
            {
                uint mask = operation <= 303 ? 4u : 8u;
                uint bits = Memory.U32(flags);
                Memory.Put32(flags, (operation & 1) == 0 ? bits | mask : bits & ~mask);
                return 0;
            }
            if (operation == 306 || operation == 307)
            {
                Memory.Put8(Memory.U32(table + 139 * 4), operation == 306 ? 1u : 0u);
                if (operation == 306)
                {
                    Memory.Put32(Memory.U32(table + 140 * 4), unchecked((uint)value));
                }
                return 0;
            }
            if (operation == 401 || operation == 406)
            {
                uint cell = Memory.U32(table + (operation == 401 ? 92u : 93u) * 4);
                int previous = unchecked((int)Memory.U32(cell));
                Memory.Put32(cell, unchecked((uint)value));
                return previous;
            }
            if (operation == 407)
            {
                Memory.Put32(timerFields + 8, unchecked((uint)value));
                return 0;
            }
            if (operation == 408)
            {
                uint screenBitmap = Memory.U32(table + 95 * 4) + 30 * 16;
                uint type = Memory.U32(screenBitmap + 8);
                if (type == 1)
                {
                    return value >= 0 && Memory.U32(screenBitmap + 4) >= (uint)value ? 0 : -1;
                }
                if (type != 0 || value < displayedPixels.Length)
                {
                    return -1;
                }
                uint capacity = Math.Max((uint)value, framebufferCapacity);
                uint replacement = Memory.NativeAllocate(capacity);
                if (replacement == 0)
                {
                    return -1;
                }
                try
                {
                    Memory.NativeFree(Framebuffer, framebufferCapacity);
                }
                catch
                {
                    Memory.NativeFree(replacement, capacity);
                    throw;
                }
                Framebuffer = replacement;
                framebufferCapacity = capacity;
                Memory.Put32(Memory.U32(table + 91 * 4), replacement);
                Memory.Put32(screenBitmap + 4, capacity);
                Memory.Put32(screenBitmap + 12, replacement);
                return 0;
            }
            if (operation == 500)
            {
                return unchecked((int)LoadSmsConfiguration());
            }
            if (operation == 503)
            {
                return (int)Memory.U8(SmsBuffer + 5);
            }
            if (operation == 504)
            {
                return unchecked((int)SaveSmsConfiguration());
            }
            if (operation == 404)
            {
                return unchecked((int)NotifySimChange(unchecked((short)value), 0));
            }
            if (operation == 405)
            {
                return unchecked((int)NetworkDispatch(82, Cpu));
            }
            if (operation >= 2 && operation <= 6)
            {
                if (value != 0)
                {
                    Memory.Check((uint)value & ~1u, 2);
                }
                if (operation == 2)
                {
                    eventHook = (uint)value;
                }
                else if (operation == 3)
                {
                    timerHook = (uint)value;
                }
                else if (operation == 4)
                {
                    stopHook = (uint)value;
                }
                else if (operation == 5)
                {
                    pauseHook = (uint)value;
                }
                else
                {
                    resumeHook = (uint)value;
                }
                return 0;
            }
            if (operation >= 100 && operation <= 102)
            {
                uint result = operation == 100 ? Memory.MinimumAvailable :
                    operation == 101 ? Memory.HighWater : Memory.Available;
                Record($"heap query {operation} = {result}");
                return (int)result;
            }
            if (operation == 403)
            {
                return unchecked((int)SetGcThreshold(value));
            }
            if (operation == 7)
            {
                return value;
            }
            if (operation == 3629 || operation == 3921 || operation == 3251)
            {
                uint bits = Memory.U32(flags);
                if (operation == 3629 && value == 2913)
                {
                    Memory.Put32(flags, bits | 1);
                }
                if (operation == 3921 && value == 98352)
                {
                    Memory.Put32(flags, bits | 2);
                }
                if (operation == 3251 && value == 648826)
                {
                    Memory.Put32(flags, bits & ~2u);
                }
                return 0;
            }
            throw new RuntimeFault($"unsupported _com {operation}, {value}");
        }

        private uint Dispatch(uint address, ArmCpu cpu)
        {
            Live();
            if (address >= 0xf0010000 && address < 0xf0010000 + 78 * 4 && (address & 3) == 0)
            {
                return DispatchInternal(address, cpu);
            }
            if (address < 0xf0000000 || address >= 0xf0000000 + 150 * 4 || (address & 3) != 0)
            {
                throw new RuntimeFault($"unsupported internal/host address 0x{address:x8}");
            }
            int slot = (int)(address - 0xf0000000) / 4;
            HostCalls[slot] = HostCalls.TryGetValue(slot, out int calls) ? calls + 1 : 1;
            if (slot == 33)
            {
                return ReadRuntimeClock();
            }
            if (slot == 36)
            {
                bool installer = AdvanceInstallerSleep(cpu.R[0]);
                if (!sleepPolicyRecorded)
                {
                    sleepPolicyRecorded = true;
                    Record(installer ? $"sleep({cpu.R[0]}): verified installer virtual wait; no host blocking" :
                        $"sleep({cpu.R[0]}): authorized Unity policy, MR_SUCCESS immediately; no blocking or clock advance");
                }
                return 0;
            }
            return DispatchService(slot, cpu);
        }

        private uint DispatchService(int slot, ArmCpu cpu)
        {
            uint A(int index) => cpu.ReadArgument(index);
            switch (slot)
            {
                case 22:
                    return StopNative((short)A(0) != 0);
                case 0:
                    return Memory.NativeAllocate(A(0));
                case 1:
                    uint freeLength = (A(1) + 7) & ~7u;
                    if (A(0) == 0 || freeLength == 0 || A(0) < Memory.HeapBase ||
                        A(0) >= Memory.HeapEnd || (ulong)A(0) + freeLength > Memory.HeapEnd)
                    {
                        Record($"mr_free invalid: pointer={A(0):x8}, size={A(1)}; reference returns without modifying heap");
                        return 0;
                    }
                    if ((A(0) & 3) == 0 && A(0) >= Framebuffer &&
                        (ulong)A(0) < (ulong)Framebuffer + framebufferCapacity)
                    {
                        Record($"compatibility mr_free borrowed framebuffer: pointer={A(0):x8}, size={A(1)}; " +
                            "shared display storage remains owned by the session");
                        return 0;
                    }
                    if ((A(0) & 3) == 0 && Memory.ExceedsNativeAllocation(A(0), freeLength))
                    {
                        Record($"compatibility mr_free invalid allocation extent: pointer={A(0):x8}, size={A(1)}; " +
                            "known live allocation preserved");
                        return 0;
                    }
                    if (!Memory.NativeFree(A(0), A(1), true))
                    {
                        Record($"mr_free already free: pointer={A(0):x8}, size={A(1)}; reference returns without modifying heap");
                        return 0;
                    }
                    RetireModules(A(0), A(1));
                    return 0;
                case 2:
                    uint replacement = Memory.NativeReallocate(A(0), A(1), A(2));
                    if (A(0) != 0 && (replacement != 0 || A(2) == 0))
                    {
                        if (replacement != 0)
                        {
                            CopyModuleRecords(A(0), replacement, Math.Min(A(1), A(2)));
                        }
                        RetireModules(A(0), A(1));
                    }
                    return replacement;
                case 3:
                case 4:
                    Memory.Copy(A(0), A(1), A(2), slot == 4);
                    return A(0);
                case 5:
                    Memory.Copy(A(0), A(1), (uint)Memory.CStringBytes(A(1)).Length + 1, preserveOverlap: true);
                    return A(0);
                case 6:
                    if (A(2) == 0)
                    {
                        return A(0);
                    }
                    Memory.Check(A(0), A(2));
                    bool terminated = false;
                    for (uint i = 0; i < A(2); i++)
                    {
                        uint value = terminated ? 0 : Memory.U8(A(1) + i);
                        terminated |= value == 0;
                        Memory.Put8(A(0) + i, value);
                    }
                    return A(0);
                case 7:
                    uint appendAt = A(0) + (uint)Memory.CStringBytes(A(0)).Length;
                    Memory.Copy(appendAt, A(1), (uint)Memory.CStringBytes(A(1)).Length + 1, preserveOverlap: true);
                    return A(0);
                case 8:
                    return AppendBounded(A(0), A(1), A(2));
                case 12:
                case 10:
                    return CompareNativeStrings(A(0), A(1), uint.MaxValue);
                case 11:
                    return CompareNativeStrings(A(0), A(1), A(2));
                case 9:
                    if (A(2) == 0)
                    {
                        return 0;
                    }
                    Memory.Check(A(0), A(2));
                    Memory.Check(A(1), A(2));
                    for (uint i = 0; i < A(2); i++)
                    {
                        uint left = Memory.U8(A(0) + i);
                        uint right = Memory.U8(A(1) + i);
                        if (left != right)
                        {
                            return left - right;
                        }
                    }
                    return 0;
                case 13:
                    return FindByte(A(0), A(1), A(2));
                case 14:
                    Memory.Fill(A(0), A(2), (byte)A(1));
                    return A(0);
                case 15:
                    return (uint)Memory.CStringBytes(A(0)).Length;
                case 16:
                    int match = Memory.CStringBytes(A(0)).IndexOf(Memory.CStringBytes(A(1)));
                    return match < 0 ? 0 : A(0) + (uint)match;
                case 17:
                    return Sprintf(A(0), A(1), cpu);
                case 18:
                    return unchecked((uint)ParseDecimal(Memory.CString(A(0))));
                case 19:
                    return ParseUnsigned(A(0), A(1), A(2));
                case 20:
                    return NextRandom() & 0x7fffffff;
                case 25:
                    Module module = loading ?? throw new RuntimeFault("helper registration outside EXT load");
                    Memory.Check(A(0) & ~1u, 2);
                    uint contextCell = helperContextPatch == 0
                        ? module.Address + 4 : helperContextPatch + 4;
                    Memory.Check(contextCell, 4);
                    // Mythroad has one registered C helper. Re-registration releases its RW context.
                    if (registeredHelper != null)
                    {
                        Memory.NativeFree(registeredHelper.Context, registeredContextSize, true);
                        registeredHelper.Context = 0;
                        registeredHelper.Helper = 0;
                        registeredHelper = null;
                    }
                    uint contextSize = A(1);
                    uint context = Memory.NativeAllocate(contextSize);
                    if (context == 0)
                    {
                        return uint.MaxValue;
                    }
                    Memory.Fill(context, contextSize, 0);
                    registeredHelper = module;
                    registeredContextSize = contextSize;
                    module.Helper = A(0);
                    module.Context = context;
                    Memory.Put32(contextCell, module.Context);
                    return 0;
                case 26:
                    string message = Printf(Memory.CString(A(0)), cpu.ReadArgument, 1, diagnosticStrings: true).ToString();
                    Record("printf: " + message);
                    return (uint)message.Length;
                case 27:
                    return ProvideMemory(A(0), A(1));
                case 28:
                    return ReleaseProvidedMemory(A(0));
                case 29:
                    Blit(A(0), (short)A(1), (short)A(2), (ushort)A(3), (ushort)A(4));
                    Frames++;
                    return 0;
                case 31:
                    // Zero means disarmed to the host scheduler. An immediate guest
                    // timer is delivered on its next millisecond turn, as with SDL.
                    timerDelay = Math.Max(1u, (ushort)A(0));
                    Memory.Put32(timerFields + 4, 1);
                    TimerGeneration++;
                    if (Trace.Count < TraceLimit) Trace.Add($"timer armed for {timerDelay} ms");
                    return 0;
                case 32:
                    timerDelay = 0;
                    TimerStopGeneration++;
                    Memory.Put32(timerFields + 4, 0);
                    return 0;
                case 34:
                    if (A(0) == 0)
                    {
                        return uint.MaxValue;
                    }
                    Memory.Check(A(0), 7);
                    DateTime date = localClock();
                    Memory.Put16(A(0), (uint)date.Year);
                    Memory.Put8(A(0) + 2, (uint)date.Month);
                    Memory.Put8(A(0) + 3, (uint)date.Day);
                    Memory.Put8(A(0) + 4, (uint)date.Hour);
                    Memory.Put8(A(0) + 5, (uint)date.Minute);
                    Memory.Put8(A(0) + 6, (uint)date.Second);
                    Record($"local date queried: {date:yyyy-MM-dd HH:mm:ss}");
                    return 0;
                case 35:
                    if (A(0) == 0)
                    {
                        Record("fixed emulated user-info: null output, MR_FAILED=-1");
                        return uint.MaxValue;
                    }
                    Memory.Fill(A(0), 64, 0);
                    Memory.Write(A(0), Encoding.ASCII.GetBytes(CompatibilityImei));
                    Memory.Write(A(0) + 16, Encoding.ASCII.GetBytes(CompatibilityImsi));
                    Memory.Write(A(0) + 32, Encoding.ASCII.GetBytes(CompatibilityModel));
                    Memory.Write(A(0) + 40, Encoding.ASCII.GetBytes(CompatibilityModel));
                    Memory.Put32(A(0) + 48, CompatibilityVersion);
                    Record("fixed emulated user-info: vmrp compatibility profile, MR_SUCCESS=0; no host identity read");
                    return 0;
                case 30:
                    if (font == null)
                    {
                        throw new RuntimeFault("mr_getCharBitmap requires an explicit bitmap font input");
                    }
                    if (A(1) > 2)
                    {
                        throw new RuntimeFault($"unsupported font selector {A(1)}");
                    }
                    ushort character = (ushort)A(0);
                    uint glyphKey = A(1) << 16 | character;
                    if (!glyphs.TryGetValue(glyphKey, out var glyph))
                    {
                        byte[] bitmap = font.Glyph(character, A(1), out int glyphWidth, out int glyphHeight);
                        glyph = (Memory.CopyVm(bitmap), glyphWidth, glyphHeight);
                        glyphs.Add(glyphKey, glyph);
                    }
                    Memory.Put32(A(2), (uint)glyph.Width);
                    Memory.Put32(A(3), (uint)glyph.Height);
                    return glyph.Address;
                case 61:
                    return (uint)NetworkId();
                case 59:
                    Memory.CString(A(0));
                    return SubmitSimulatedSms(A(2));
                case 55:
                case 56:
                    Record($"fixed emulated vibration service {slot}: MR_SUCCESS=0; no host vibration");
                    return 0;
                case 60:
                case 62:
                    Memory.CString(A(0));
                    Record($"fixed emulated external service {slot}: no phone call or browser launch");
                    return 0;
                case 63:
                    return CreateNativeMenu(A(0), unchecked((short)A(1)));
                case 64:
                    return SetNativeMenuItem(A(0), A(1), (int)A(2));
                case 75:
                    return CreateNativeEdit(A(0), A(1), (int)A(2), (int)A(3));
                case 76:
                    return ReleaseNativeEdit(A(0));
                case 77:
                    return NativeEdit != null && NativeEdit.Handle == A(0) ? NativeEdit.Buffer : 0;
                case 65:
                    return ShowNativeMenu(A(0));
                case 67:
                    return ReleaseNativeMenu(A(0));
                case 68:
                    return RefreshNativeMenu(A(0));
                case 54:
                    RequestExit();
                    return 0;
                case 57:
                    if (A(2) != 0)
                    {
                        Memory.Check(A(1), A(2));
                    }
                    if (A(0) > 6)
                    {
                        if (Trace.Count < TraceLimit)
                            Trace.Add($"audio unavailable: unsupported play type={A(0)}, MR_FAILED=-1");
                        return uint.MaxValue;
                    }
                    return PlaySound((int)A(0), A(1), A(2), A(3) != 0);
                case 58:
                    if (A(0) > 6)
                    {
                        if (Trace.Count < TraceLimit)
                            Trace.Add($"audio unavailable: unsupported stop type={A(0)}, MR_FAILED=-1");
                        return uint.MaxValue;
                    }
                    return StopSound((int)A(0));
                case 40:
                    return NativeOpen(Memory.CString(A(0)), A(1));
                case 38:
                    if (A(0) == 200001)
                    {
                        Record("fixed mr_platEx(200001): MR_IGNORE=1; C port table unchanged");
                        return 1;
                    }
                    if (A(0) == 1207)
                    {
                        return UnicodeToGbk(A(1), A(3), A(4));
                    }
                    if (A(0) == 1017 || A(0) == 1116 || A(0) == 1201 || A(0) == 1305)
                    {
                        return PlatformData(A(0), A(1), A(3), A(4));
                    }
                    if (A(0) == 1014) return AllocateExternalRam(A(2), A(3), A(4));
                    if (A(0) == 1015) return ReleaseExternalRam(A(1), A(2));
                    if (A(0) == 1012 || A(0) == 1013 || A(0) == 1224 || A(0) == 1307)
                    {
                        Record($"fixed mr_platEx({A(0)}): MR_IGNORE=1; no device query");
                        return 1;
                    }
                    uint mediaCommand = A(0) / 10;
                    if ((mediaCommand >= 201 && mediaCommand <= 213) || mediaCommand == 215 ||
                        mediaCommand == 216 || (mediaCommand >= 220 && mediaCommand <= 225))
                    {
                        Record($"unavailable mr_platEx({A(0)}) media service: MR_IGNORE=1; no playback or callback");
                        return 1;
                    }
                    if (A(0) == 1222 || A(0) == 1223)
                    {
                        Record($"fixed mr_platEx({A(0)}) backlight control: MR_SUCCESS=0; no host backlight change");
                        return 0;
                    }
                    if (A(0) != 1204)
                    {
                        Record($"unavailable mr_platEx({A(0)}): MR_IGNORE=1; outputs unchanged; no callback or device service");
                        return 1;
                    }
                    return SwitchPath(Memory.CStringBytes(A(1)), A(3), A(4), A(5));
                case 37:
                    if (A(0) == 101)
                    {
                        return SetLcdOrientation(A(1));
                    }
                    if (A(0) == 1231)
                    {
                        uint position = NativeTell(A(1));
                        Record($"file position handle={A(1)}: {unchecked((int)position)}");
                        return position;
                    }
                    if (A(0) == 1001)
                    {
                        return SocketState((int)A(1));
                    }
                    if (A(0) == 1205 || A(0) == 1218)
                    {
                        Record($"fixed mr_plat({A(0)}): 1001");
                        return 1001;
                    }
                    if (A(0) == 1211)
                    {
                        if ((int)A(1) <= 0)
                        {
                            throw new RuntimeFault("platform random range must be positive");
                        }
                        SeedRandom(ReadRuntimeClock());
                        return unchecked(1000u + (NextRandom() & 0x7fffffff) % A(1));
                    }
                    if (A(0) == 1206)
                    {
                        Record($"device language: {language}={(int)language}");
                        return (uint)language;
                    }
                    if (A(0) == 1106)
                    {
                        Record($"unavailable mr_plat(1106) SMS center: MR_IGNORE=1, caller={Describe(cpu.R[14] & ~1u)}");
                        return 1;
                    }
                    if (A(0) == 1215 || A(0) == 1216 || A(0) == 1224)
                    {
                        Record($"unavailable mr_plat({A(0)}) cell information: MR_IGNORE=1; no device query or callback");
                        return 1;
                    }
                    Record($"unavailable mr_plat({A(0)}), param={A(1)}: MR_IGNORE=1; no device query or callback");
                    return 1;
                case 41:
                    return NativeClose(A(0));
                case 39:
                    return uint.MaxValue;
                case 43:
                    return NativeWrite(A(0), A(1), A(2));
                case 42:
                    return NativeInfo(Memory.CString(A(0)));
                case 44:
                    return NativeRead(A(0), A(1), A(2));
                case 45:
                    return NativeSeek(A(0), (int)A(1), A(2));
                case 46:
                    return NativeLength(Memory.CString(A(0)));
                case 47:
                    if (A(0) == 0)
                    {
                        Record("remove failed: null filename, MR_FAILED=-1; no filesystem access");
                        return uint.MaxValue;
                    }
                    return NativeRemove(Memory.CString(A(0)));
                case 48:
                    return NativeRename(Memory.CString(A(0)), Memory.CString(A(1)));
                case 49:
                    return NativeDirectory(Memory.CString(A(0)), false);
                case 50:
                    return NativeDirectory(Memory.CString(A(0)), true);
                case 51:
                    return NativeFindStart(Memory.CString(A(0)), A(1), A(2));
                case 52:
                    return NativeFindNext(A(0), A(1), A(2));
                case 53:
                    return NativeFindStop(A(0));
                case 80:
                    if (A(0) == 0)
                    {
                        return 0;
                    }
                    Memory.Check(A(0), 12);
                    Memory.Put32(A(0), (uint)Width);
                    Memory.Put32(A(0) + 4, (uint)Height);
                    Memory.Put32(A(0) + 8, 16);
                    return 0;
                case 81:
                case 82:
                case 83:
                case 84:
                case 85:
                case 86:
                case 87:
                case 88:
                case 89:
                case 90:
                    return NetworkDispatch(slot, cpu);
                case 78:
                case 79:
                    Record($"fixed emulated window service {slot}: MR_IGNORE=1");
                    return 1;
                case 69:
                case 72:
                    return CreateNativeText(A(0), A(1), (int)A(2), slot == 72);
                case 70:
                case 73:
                    return ReleaseNativeText(A(0), slot == 73);
                case 71:
                case 74:
                    NativeTextWindow window = RequireNativeText(A(0), slot == 74);
                    string title = ReadUnicode(A(1));
                    string body = ReadUnicode(A(2));
                    int dialogType = slot == 74 || A(3) == uint.MaxValue ? window.Type : (int)A(3);
                    CheckDialogType(dialogType);
                    NativeText = new NativeTextWindow(window.Handle, title, body, dialogType, window.IsText, WrapNativeText(body));
                    Record($"native text refreshed: {title}: {body}");
                    Frames++;
                    return 0;
                case 113:
                    GuestMd5.Init(Memory, A(0));
                    return 0;
                case 114:
                    GuestMd5.Append(Memory, A(0), A(1), (int)A(2));
                    return 0;
                case 115:
                    GuestMd5.Finish(Memory, A(0), A(1));
                    return 0;
                case 116:
                    return LoadSmsConfiguration();
                case 117:
                    return SaveSmsConfiguration();
                case 118:
                    RefreshDrawingSurface((short)A(0), (short)A(1), (short)A(2), (short)A(3));
                    Frames++;
                    return 0;
                case 119:
                    Pixel((short)A(0), (short)A(1), (ushort)A(2));
                    return 0;
                case 120:
                    DrawBitmap(A(0), (short)A(1), (short)A(2), (ushort)A(3), (ushort)A(4),
                        (ushort)A(5), (ushort)A(6), (short)A(7), (short)A(8), (short)A(9));
                    return 0;
                case 122:
                    FillRectangle((short)A(0), (short)A(1), (short)A(2), (short)A(3),
                        Rgb((int)A(4), (int)A(5), (int)A(6)));
                    return 0;
                case 123:
                    DrawText(A(0), (short)A(1), (short)A(2),
                        (ushort)(((A(3) & 248) << 8) | ((A(4) & 252) << 3) | ((A(5) & 255) >> 3)),
                        A(6) != 0, A(7));
                    return 0;
                case 125:
                    return ReadNativeResource(ReadCachedCString(A(0), ref cachedResourceName, ref cachedResourceNameBytes),
                        A(1), A(2), out _, out _);
                case 141:
                    return UnzipNative();
                case 124:
                    return CheckBitmap(A(0), (short)A(1), (short)A(2), (ushort)A(3), (ushort)A(4), (ushort)A(5), (ushort)A(6));
                case 128:
                    return DrawTextExtended(A(0), (short)A(1), (short)A(2), (ushort)A(4), (ushort)(A(4) >> 16),
                        Rgb((byte)A(5), (byte)(A(5) >> 8), (byte)(A(5) >> 16)), (int)A(6), (ushort)A(7));
                case 129:
                    ApplyContrast((short)A(0), (short)A(1), (short)A(2), (short)A(3), (short)A(4), (short)A(5), (short)A(6));
                    return 0;
                case 121:
                    DrawTransformedBitmap(A(0), A(1), (ushort)A(2), (ushort)A(3), A(4), (ushort)A(5));
                    return 0;
                case 145:
                    DrawCharacter((char)A(0), (int)A(1), (int)A(2), (ushort)A(3), 2);
                    return 0;
                case 148:
                    DrawRegion(A(0), A(1), (int)A(2), (int)A(3), (int)A(4), (int)A(5),
                        (int)A(6), (int)A(7), (int)A(8), (int)A(9));
                    return 0;
                case 147:
                    return DrawMarkedBitmap(A(0), A(1), (int)A(2), (int)A(3), (int)A(4), (int)A(5),
                        (int)A(6), (int)A(7), (int)A(8), (int)A(9));
                case 130:
                    return InvokeScriptCommand(A(0), (int)A(1), (int)A(2));
                case 131:
                    return InvokeNativeStringCommand(A(0), (int)A(1), A(2), A(3));
                case 126:
                    return WideByteLength(A(0));
                case 127:
                    return RegisterPackage(A(0), A(1), A(2));
                case 132:
                    return ConvertNativeText(A(0), A(1), A(2));
                case 133:
                    if (A(1) == 0)
                    {
                        throw new RuntimeFault("integer division by zero");
                    }
                    return (uint)((long)(int)A(0) / (int)A(1));
                case 134:
                    if (A(1) == 0)
                    {
                        throw new RuntimeFault("integer modulo by zero");
                    }
                    return (uint)((long)(int)A(0) % (int)A(1));
                case 137:
                    return UpdateCrc(A(0), A(1));
                default:
                    throw new RuntimeFault($"unsupported host slot {slot}, args=" +
                        $"{A(0):x8},{A(1):x8},{A(2):x8},{A(3):x8}, caller={Describe(cpu.R[14] & ~1u)}");
            }
        }

        private void Blit(uint source, int x, int y, int width, int height, int sourceWidth = -1, int sourceHeight = -1)
        {
            if (Trace.Count < TraceLimit)
                Trace.Add($"refresh source=0x{source:x8} framebuffer=0x{Framebuffer:x8} rect={x},{y},{width},{height}");
            if (width > 4096 || height > 4096)
            {
                throw new RuntimeFault("bitmap dimensions exceed limit");
            }
            int left = Math.Max(0, x);
            int top = Math.Max(0, y);
            sourceWidth = sourceWidth < 0 ? Width : sourceWidth;
            sourceHeight = sourceHeight < 0 ? Height : sourceHeight;
            int right = Math.Min(Math.Min(Width, sourceWidth), x + width);
            int bottom = Math.Min(Math.Min(Height, sourceHeight), y + height);
            if (right <= left || bottom <= top)
            {
                return;
            }
            uint end = (uint)(((bottom - 1) * sourceWidth + right) * 2);
            ReadOnlySpan<byte> pixels = Memory.ReadSpan(source, end);
            firstDirtyRow = firstDirtyRow == endDirtyRow ? top : Math.Min(firstDirtyRow, top);
            endDirtyRow = Math.Max(endDirtyRow, bottom);
            if (left == 0 && right == Width && sourceWidth == Width)
            {
                int offset = top * Width * 2;
                pixels.Slice(offset).CopyTo(displayedPixels.AsSpan(offset));
                return;
            }
            for (int row = top; row < bottom; row++)
            {
                int offset = (row * Width + left) * 2;
                pixels.Slice((row * sourceWidth + left) * 2, (right - left) * 2)
                    .CopyTo(displayedPixels.AsSpan(offset, (right - left) * 2));
            }
        }

        private void Pixel(int x, int y, ushort color)
        {
            var surface = DrawingSurface();
            if (x >= 0 && x < surface.Width && y >= 0 && y < surface.Height)
            {
                Memory.Put16(surface.Pointer + (uint)(y * surface.Width + x) * 2, color);
            }
        }

        private uint Open(string name, string path, uint mode)
        {
            if (mode != 1 && !NativeWritable(name, path)) return 0;
            if (IsPackage(name))
            {
                uint inputHandle = nextHandle++;
                files.Add(inputHandle, ((name == packageFile ? package : rootPackage).OpenImage(), name));
                return inputHandle;
            }
            uint accessMode = mode & 7;
            if (mythroad != null)
            {
                string resourcePath = RootRelative(path);
                Stream? resource = mythroad.Open(resourcePath);
                if (resource != null)
                {
                    uint resourceHandle = nextHandle++;
                    files.Add(resourceHandle, (resource, name));
                    if (Trace.Count < TraceLimit) Trace.Add("open mythroad/" + resourcePath);
                    return resourceHandle;
                }
            }
            if ((mode & 8) == 0 && !storage.FileExists(path))
            {
                return 0;
            }
            FileAccess access = accessMode == 1 ? FileAccess.Read : accessMode == 2 ? FileAccess.Write : FileAccess.ReadWrite;
            if ((mode & 8) != 0)
            {
                EnsureVirtualDirectoryBacking(Path.GetDirectoryName(path)!);
            }
            var file = storage.Open(path, (mode & 8) != 0 ? FileMode.OpenOrCreate : FileMode.Open, access);
            uint handle = nextHandle++;
            files.Add(handle, (file, name));
            return handle;
        }

        private void EnsureVirtualDirectoryBacking(string directory)
        {
            if (mythroad == null) return;
            string resourcePath = RootRelative(directory);
            if (mythroad.Info(resourcePath) == 2 && !storage.DirectoryExists(directory))
            {
                // Materialize the virtual resource directory only inside the save root.
                // Mounted resources still take precedence and cannot be overwritten.
                storage.CreateDirectory(directory);
                if (Trace.Count < TraceLimit) Trace.Add("save backing for virtual directory " + resourcePath);
            }
        }

        private bool IsPackage(string name)
        {
            return workingDirectory.RawPrefix.Length == 0 && (string.Equals(name, packageFile, StringComparison.Ordinal) ||
                string.Equals(name, rootPackageFile, StringComparison.Ordinal)) || IsLegacySelfPackage(name);
        }

        private string RootRelative(string validatedPath)
        {
            ReadOnlySpan<char> relative = validatedPath.AsSpan(saveRoot.Length).TrimStart(Path.DirectorySeparatorChar);
            if (relative.IndexOf('\\') < 0) return relative.ToString();
            int offset = validatedPath.Length - relative.Length;
            return string.Create(relative.Length, (Path: validatedPath, Offset: offset), static (destination, state) =>
            {
                ReadOnlySpan<char> source = state.Path.AsSpan(state.Offset);
                for (int i = 0; i < source.Length; i++)
                {
                    destination[i] = source[i] == '\\' ? '/' : source[i];
                }
            });
        }

        private Stream File(uint handle)
        {
            return files.TryGetValue(handle, out var file) ? file.Data
                : throw new RuntimeFault($"invalid file handle {handle}");
        }

        private string SavePath(string name, bool allowRootDirectory = false) =>
            SavePath(name, workingDirectory.RawPrefix, workingDirectory.DecodedPrefix, allowRootDirectory);

        private string SavePath(string name, string prefix, string decodedPrefix, bool allowRootDirectory)
        {
            string decodedName = DecodeFilename(name);
            string path = ResolveSavePath(decodedName, decodedPrefix, allowRootDirectory);
            CheckLinks(path);
            if ((decodedName != name || decodedPrefix != prefix) &&
                !storage.FileExists(path) && !storage.DirectoryExists(path) &&
                (mythroad?.Info(RootRelative(path)) ?? 8u) == 8)
            {
                // Older builds used the raw guest bytes as Latin-1 host names.
                // Keep an existing save accessible while using decoded names for new files.
                string legacyPath;
                try
                {
                    legacyPath = ResolveSavePath(name, prefix, allowRootDirectory);
                }
                catch (RuntimeFault)
                {
                    return path;
                }
                if (storage.FileExists(legacyPath) || storage.DirectoryExists(legacyPath))
                {
                    CheckLinks(legacyPath);
                    return legacyPath;
                }
            }
            return path;
        }

        private string ResolveSavePath(string name, string prefix, bool allowRootDirectory)
        {
            string relative = name.Replace('\\', '/').TrimEnd('/');
            if (relative.StartsWith("//", StringComparison.Ordinal))
            {
                throw new RuntimeFault("save path must be relative");
            }
            // dsm.c appends the guest filename to its working directory before
            // normalizing separators, so one leading separator stays virtual.
            if (relative.StartsWith("/", StringComparison.Ordinal))
            {
                relative = relative.Substring(1);
            }
            if (relative.Length == 0)
            {
                throw new RuntimeFault("save path must be relative");
            }
            relative = prefix + relative;
            int compactLength = 0;
            ReadOnlySpan<char> remaining = relative.AsSpan();
            while (!remaining.IsEmpty)
            {
                ReadOnlySpan<char> component = ReadSaveComponent(ref remaining);
                if (component.IsEmpty || component.SequenceEqual(".".AsSpan())) continue;
                ValidateSaveComponent(component, name);
                if (compactLength != 0) compactLength++;
                compactLength += component.Length;
            }
            if (compactLength == 0)
            {
                if (allowRootDirectory) return saveRoot;
                throw new RuntimeFault("save path escaped session directory");
            }
            if (compactLength != relative.Length)
            {
                relative = string.Create(compactLength, relative, static (destination, source) =>
                {
                    ReadOnlySpan<char> remaining = source.AsSpan();
                    int written = 0;
                    while (!remaining.IsEmpty)
                    {
                        ReadOnlySpan<char> component = ReadSaveComponent(ref remaining);
                        if (component.IsEmpty || component.SequenceEqual(".".AsSpan())) continue;
                        if (written != 0) destination[written++] = '/';
                        component.CopyTo(destination.Slice(written));
                        written += component.Length;
                    }
                });
            }
            string path = Path.GetFullPath(Path.Combine(saveRoot, relative));
            if (!path.StartsWith(saveRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new RuntimeFault("save path escaped session directory");
            }
            if (storage.FileExists(path) || storage.DirectoryExists(path))
            {
                return path;
            }

            // Guest files use case-insensitive names. Some games unpack a resource
            // with one spelling and later open the same file with another.
            string parent = saveRoot;
            bool correctedCase = false;
            remaining = relative.AsSpan();
            while (!remaining.IsEmpty)
            {
                ReadOnlySpan<char> component = ReadSaveComponent(ref remaining);
                string componentName = component.Length == relative.Length ? relative : component.ToString();
                string child = Path.Combine(parent, componentName);
                if (storage.FileExists(child) || storage.DirectoryExists(child))
                {
                    parent = child;
                    continue;
                }
                if (storage.DirectoryExists(parent))
                {
                    CheckLinks(parent);
                    string? match = null;
                    foreach (string entry in storage.EnumerateEntries(parent))
                    {
                        ReadOnlySpan<char> basename = Path.GetFileName(entry.AsSpan());
                        if (!basename.Equals(component, StringComparison.OrdinalIgnoreCase)) continue;
                        if (match != null) throw new RuntimeFault("ambiguous guest filename: " + componentName);
                        match = Path.Combine(parent, basename.ToString());
                        if (storage.IsLink(match)) throw new RuntimeFault("save path contains link: " + match);
                    }
                    if (match != null)
                    {
                        parent = match;
                        correctedCase = true;
                        continue;
                    }
                }
                // Once a component is missing, the remaining names cannot be matched on disk.
                if (!correctedCase && !remaining.IsEmpty) return path;
                parent = child;
            }
            return parent;
        }

        private static ReadOnlySpan<char> ReadSaveComponent(ref ReadOnlySpan<char> remaining)
        {
            int separator = remaining.IndexOf('/');
            ReadOnlySpan<char> component = separator < 0 ? remaining : remaining.Slice(0, separator);
            remaining = separator < 0 ? default : remaining.Slice(separator + 1);
            return component;
        }

        private static void ValidateSaveComponent(ReadOnlySpan<char> component, string name)
        {
            if (component[component.Length - 1] is '.' or ' ' || component.IndexOfAny(":\0<>\"|?*".AsSpan()) >= 0)
            {
                throw new RuntimeFault($"unsafe save path: {name}");
            }
            int extension = component.IndexOf('.');
            ReadOnlySpan<char> stem = (extension < 0 ? component : component.Slice(0, extension)).TrimEnd(' ');
            if (stem.Length is 3 or 4)
            {
                Span<char> upper = stackalloc char[4];
                int written = stem.ToUpperInvariant(upper);
                if (written == 3 && (upper.Slice(0, 3).SequenceEqual("CON".AsSpan()) ||
                    upper.Slice(0, 3).SequenceEqual("PRN".AsSpan()) || upper.Slice(0, 3).SequenceEqual("AUX".AsSpan()) ||
                    upper.Slice(0, 3).SequenceEqual("NUL".AsSpan())) || written == 4 &&
                    (upper.Slice(0, 3).SequenceEqual("COM".AsSpan()) || upper.Slice(0, 3).SequenceEqual("LPT".AsSpan())) &&
                    (upper[3] >= '1' && upper[3] <= '9' || upper[3] is '\u00b9' or '\u00b2' or '\u00b3'))
                {
                    throw new RuntimeFault($"reserved save path: {name}");
                }
            }
            foreach (char character in component)
            {
                if (character < 32) throw new RuntimeFault("control character in save path");
            }
        }

        private uint SwitchPath(ReadOnlySpan<byte> request, uint output, uint length, uint callback)
        {
            if (request.IsEmpty)
            {
                Record("switch path ignored: empty request, MR_IGNORE=1; working directory and outputs unchanged");
                return 1;
            }
            char drive = char.ToLowerInvariant((char)request[0]);
            if (drive == 'y')
            {
                string guestPath = workingDirectory.GuestPath;
                Span<byte> path = stackalloc byte[guestPath.Length + 1];
                int written = ScriptChunk.Bytes.GetBytes(guestPath.AsSpan(), path);
                path[written] = 0;
                Memory.Write(pathBuffer, path.Slice(0, written + 1));
                Memory.Put32(output, pathBuffer);
                Memory.Put32(length, (uint)written);
                if (callback != 0)
                {
                    Memory.Put32(callback, 0);
                }
                return 0;
            }
            if (drive != 'a' && drive != 'b' && drive != 'c' && drive != 'x' && drive != 'z')
            {
                if (Trace.Count < TraceLimit)
                {
                    string unsupported = ScriptChunk.Bytes.GetString(request);
                    Trace.Add($"switch path ignored: unsupported virtual drive {unsupported}, MR_IGNORE=1; working directory and outputs unchanged");
                }
                return 1;
            }
            string guestRequest = drive == 'z' ? "c:/" : ScriptChunk.Bytes.GetString(request);
            if (drive == 'z')
            {
                drive = 'c';
            }
            string normalized = DecodeFilename(guestRequest).Replace('\\', '/');
            if (normalized.Length > 2 && (normalized[1] != ':' || normalized[2] != '/'))
            {
                throw new RuntimeFault($"invalid virtual path: {guestRequest}");
            }
            string subdirectory = normalized.Length > 3
                ? ScriptChunk.Bytes.GetString(filenameEncoding.GetBytes(normalized.Substring(3).TrimEnd('/') + "/")) : "";
            if (subdirectory.Length > VirtualPathByteCapacity - 4) // drive, colon, separator and NUL
            {
                throw new RuntimeFault("virtual path exceeds ABI byte capacity");
            }
            string prefix = (drive == 'c' ? "" : "drives/" + drive + "/") + subdirectory;
            string decodedPrefix = DecodeFilename(prefix);
            SavePath(".", prefix, decodedPrefix, allowRootDirectory: true);
            workingDirectory = (prefix, decodedPrefix, drive + ":/" + subdirectory);
            Record("switch path " + workingDirectory.GuestPath);
            return 0;
        }

        private static string[] GetSaveRootAncestors(string root, string rootPrefix)
        {
            var ancestors = new List<string>();
            for (string? part = Path.GetDirectoryName(rootPrefix) ?? root;
                !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            {
                ancestors.Add(part);
            }
            return ancestors.ToArray();
        }

        private void CheckLinks(string path)
        {
            for (string? part = path; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
            {
                if (string.Equals(part, saveRootAncestors[0], StringComparison.Ordinal))
                {
                    // Only the path names are cached; a directory can become a link between opens.
                    foreach (string ancestor in saveRootAncestors) CheckLink(ancestor);
                    return;
                }
                CheckLink(part);
            }
        }

        private void CheckLink(string path)
        {
            if ((storage.FileExists(path) || storage.DirectoryExists(path)) && storage.IsLink(path))
            {
                throw new RuntimeFault($"save path contains link: {path}");
            }
        }

        private void TrackResourceModule(string name, string origin, uint address, uint size)
        {
            foreach (var module in Modules)
            {
                if (module.Active && module.Address == address && module.Size == size && module.Origin == origin)
                {
                    return;
                }
            }
            Modules.Add(new Module { Name = name, Origin = origin, Address = address, Size = size });
        }

        private void CopyModuleRecords(uint source, uint destination, uint size)
        {
            int originalCount = Modules.Count;
            for (int index = 0; index < originalCount; index++)
            {
                var module = Modules[index];
                if (module.Active && module.Address >= source &&
                    (ulong)module.Address + module.Size <= (ulong)source + size)
                {
                    Modules.Add(new Module
                    {
                        Name = module.Name,
                        Origin = module.Origin,
                        CopiedFrom = module.Address,
                        Address = destination + (module.Address - source),
                        Size = module.Size
                    });
                }
            }
        }

        private void RetireModules(uint address, uint size)
        {
            if (address == 0 || size == 0)
            {
                return;
            }
            foreach (var module in Modules)
            {
                if (module.Active && address < (ulong)module.Address + module.Size &&
                    module.Address < (ulong)address + size)
                {
                    module.Active = false;
                }
            }
        }

        private void RecordExecution(uint address)
        {
            foreach (var module in Modules)
            {
                if (module.Active && address >= module.Address && (ulong)address < (ulong)module.Address + module.Size)
                {
                    if (TrackExecutedBytes)
                    {
                        module.RecordInstruction(address, Cpu.LastInstructionSize);
                    }
                    if (module.Instructions++ == 0)
                    {
                        Record($"executed {module.Name} at +0x{address - module.Address:x}");
                    }
                    return;
                }
            }
        }

        private string Describe(uint address)
        {
            foreach (var module in Modules)
            {
                if (module.Active && address >= module.Address && (ulong)address < (ulong)module.Address + module.Size)
                {
                    return $"{module.Name}+0x{address - module.Address:x}";
                }
            }
            return "host/guest";
        }

        private void HandleSemihosting(uint operation, uint parameter)
        {
            if (operation == 0x18)
            {
                // AArch32 SYS_EXIT carries the ADP reason directly in r1.
                // Only ApplicationExit is a normal termination; report other traps.
                if (parameter != 0x20026)
                    throw new RuntimeFault($"semihosting exception reason 0x{parameter:x8}");
                Record("semihosting application exit");
                RequestExit();
                return;
            }
            if (operation != 3)
            {
                throw new RuntimeFault($"unsupported semihosting operation 0x{operation:x8}");
            }
            uint character = Memory.U8(parameter);
            Record($"semihosting diagnostic character U+{character:x4}");
        }

        private const int TraceLimit = 4096;

        private void Record(string value)
        {
            if (Trace.Count < TraceLimit)
            {
                Trace.Add(value);
            }
        }

        private void Live()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(RuntimeSession));
            }
        }

        private sealed class GuestExit : Exception
        {
        }

        private void RunGuest(Action action) => RunGuest(action, 0, default);

        private void RunScriptCallback(object? function) => RunGuest(function, 0, default);

        private uint RunGuest(object? callable, uint nativeEntry, ReadOnlySpan<uint> arguments)
        {
            EnterGuestClock();
            try
            {
                if (nativeEntry != 0) return Cpu.Invoke(nativeEntry, arguments);
                if (callable is Action action) action();
                else Script.Call(callable);
            }
            catch (GuestExit)
            {
                CompleteRequestedExit();
            }
            catch (Exception error) when (guestExceptionText != null &&
                (error is RuntimeFault || error is IScriptRaisedError))
            {
                string text = guestExceptionText!;
                guestExceptionText = null;
                Record($"guest error text override: {error.Message}");
                throw new RuntimeFault(text);
            }
            finally
            {
                ExitGuestClock();
            }
            return 0;
        }

        private void EnterGuestClock()
        {
            if (guestClockDepth == 0)
            {
                guestClockTimestamp = uptimeClock();
                guestClockBaseline = milliseconds;
            }
            guestClockDepth++;
        }

        private void ExitGuestClock()
        {
            guestClockDepth--;
        }

        private uint ReadRuntimeClock()
        {
            if (guestClockDepth != 0)
            {
                uint elapsed = unchecked(uptimeClock() - guestClockTimestamp);
                // Timer delivery and nested calls share the outer baseline. Never add the same interval twice.
                if (elapsed > unchecked(milliseconds - guestClockBaseline))
                {
                    milliseconds = unchecked(guestClockBaseline + elapsed);
                }
            }
            return milliseconds;
        }

        private static uint HostUptime()
        {
            long ticks = Stopwatch.GetTimestamp();
            return unchecked((uint)(ticks / Stopwatch.Frequency * 1000 +
                ticks % Stopwatch.Frequency * 1000 / Stopwatch.Frequency));
        }

        private void RequestExit()
        {
            exitRequested = true;
            DisarmSession();
            Memory.Put32(runtimeState, 4);
            Record("guest requested exit");
            throw new GuestExit();
        }

        public void Stop()
        {
            if (disposed || stopping || hostStopping)
            {
                return;
            }
            hostStopping = true;
            List<Exception>? failures = null;
            try
            {
                if (InvokeStopHook() == 1)
                {
                    bool deliver = Started && ScriptVm.IsFunction(Script.Globals["dealevent"]);
                    StopNative(true);
                    if (deliver)
                    {
                        Record("exit event delivered");
                    }
                }
            }
            catch (Exception error)
            {
                failures = new List<Exception> { error };
            }
            finally
            {
                hostStopping = false;
                RunCleanupStep(static session => session.Dispose(), ref failures);
            }
            ThrowCleanupFailures(failures);
        }

        public void Dispose()
        {
            // Resource buffers may still need to invoke the guest's deallocator.
            if (resourceReadDepth != 0 && !disposed && !closingScript)
            {
                disposeAfterResourceRead = true;
                return;
            }
            TakeSuccessor()?.Dispose();
            if (disposed || closingScript)
            {
                return;
            }
            closingScript = true;
            try
            {
                if (!cancellation.IsCancellationRequested)
                {
                    RunCleanupStep(static session => session.FinalizeScriptObjects(), ref disposeFailures);
                }
                CloseSessionFiles(ref disposeFailures);
                RunCleanupStep(static session => session.CloseNetwork(), ref disposeFailures);
                RunCleanupStep(static session => session.DisarmSession(), ref disposeFailures);
                RunCleanupStep(static session => session.ClosePatternIterators(), ref disposeFailures);
                ReleaseScriptAllocationTracking();
                nativeMenus.Clear();
                visibleMenu = null;
                nativeMenuView = null;
                publishedOverlay = null;
                disposed = true;
                ReleaseNativeScriptStateWhenIdle();
                Started = false;
                Memory.Put32(runtimeState, 0);
                Record("stopped");
                RunCleanupStep(static session => session.soundOutput?.StopAll(), ref disposeFailures);
                ThrowCleanupFailures(disposeFailures);
            }
            finally
            {
                disposeFailures = null;
            }
        }
    }
}
