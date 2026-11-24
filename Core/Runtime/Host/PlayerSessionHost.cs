#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using CSharpMrp;

[Serializable]
public sealed class PlayerGame
{
    public string id = "";
    public string title = "";
    public string resource = "";
    public string filename = "";
    public string sha256 = "";
    public int width;
    public int height;
    public bool diagnostic;
    public string note = "";

    public void Validate()
    {
        if (string.IsNullOrEmpty(id) || id.Length > 255 || id.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0 ||
            resource != "games/" + id || string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(filename) || filename.IndexOfAny(new[] { '/', '\\', ':', '\0' }) >= 0 ||
            (width != 0 || height != 0) && !MrpScreenResolution.IsValid(width, height) ||
            diagnostic && string.IsNullOrWhiteSpace(note))
        {
            throw new RuntimeFault("invalid player catalog entry: " + id);
        }
    }

    public static string Hash(byte[] bytes)
    {
        using var hash = SHA256.Create();
        return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
    }
}

[Serializable]
public sealed class PlayerCatalog
{
    public PlayerGame[] games = Array.Empty<PlayerGame>();
    public bool mythroad;

    public void Validate()
    {
        if (games == null || games.Length == 0 || games.Length > 32)
        {
            throw new RuntimeFault("player catalog must contain 1..32 games");
        }
        foreach (var game in games)
        {
            if (game == null)
            {
                throw new RuntimeFault("null player catalog entry");
            }
            game.Validate();
        }
        if (games.Select(game => game.id).Distinct(StringComparer.Ordinal).Count() != games.Length)
        {
            throw new RuntimeFault("duplicate player game identity");
        }
    }
}

// Compiled unchanged by the console verifier and the isolated Unity project.
public sealed class PlayerSessionHost : IDisposable
{
    public const string SharedSaveDirectoryName = "shared-saves";
    private readonly string root;
    private readonly bool useRootAsSaveDirectory;
    private readonly IFileStorage storage;
    private readonly bool hostNetwork;
    private readonly BitmapFont? font;
    private readonly Encoding encoding;
    private readonly MythroadFiles? mythroad;
    private readonly ISoundOutput? soundOutput;
    private readonly double minimumTimerSeconds;
    private readonly bool trackModuleExecution;
    private readonly CancellationToken cancellation;
    private readonly Func<bool> cheatAccess;
    private readonly RuntimeSession.WindowHandleSequence windowHandles = new RuntimeSession.WindowHandleSequence();
    public GameCheats? Cheats { get; private set; }
    private double timerInterval;
    private double lastTimerPump;
    private double speedClockMilliseconds;
    private long speedClockTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    public double Speed { get; private set; } = 1;
    private readonly bool[] held = new bool[28];
    private const double DirectionRetrySeconds = .1;
    private double actionReleaseDeadline = double.NegativeInfinity;
    private int restoredDirection = -1;
    private int directionRetries;
    private double directionRetryDeadline;
    private byte[]? packageBytes;
    private bool pointerDown;
    private int pointerX;
    private int pointerY;
    private int lastArms = -1;
    private bool paused;
    private bool applicationPaused;
    private bool menuPaused;
    public RuntimeSession? Session
    {
        get; private set;
    }
    public PlayerGame? Selected
    {
        get; private set;
    }
    public string? Failure
    {
        get; private set;
    }
    public string? ReportPath
    {
        get; private set;
    }
    public string? SaveDirectory
    {
        get; private set;
    }
    public double Deadline { get; private set; } = double.PositiveInfinity;
    internal bool MenuPaused => menuPaused;
    public double WakeDeadline => menuPaused ? double.PositiveInfinity
        : restoredDirection < 0 ? Deadline : Math.Min(Deadline, directionRetryDeadline);

    public PlayerSessionHost(string root, BitmapFont? font, Encoding encoding, MythroadFiles? mythroad = null,
        ISoundOutput? soundOutput = null, int targetFps = 0, bool trackModuleExecution = true,
        CancellationToken cancellation = default, Func<bool>? cheatAccess = null,
        IFileStorage? storage = null, bool hostNetwork = true, bool useRootAsSaveDirectory = false)
    {
        if (targetFps < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetFps));
        }
        this.root = Path.GetFullPath(root);
        this.useRootAsSaveDirectory = useRootAsSaveDirectory;
        this.storage = storage ?? SystemFileStorage.Instance;
        this.hostNetwork = hostNetwork;
        this.font = font;
        this.encoding = encoding;
        this.mythroad = mythroad;
        this.soundOutput = soundOutput;
        this.trackModuleExecution = trackModuleExecution;
        this.cancellation = cancellation;
        this.cheatAccess = cheatAccess ?? (() => false);
        minimumTimerSeconds = targetFps == 0 ? 0 : 1.0 / targetFps;
        Directory.CreateDirectory(Path.Combine(this.root, "reports"));
    }

    public bool Open(PlayerGame game, byte[] bytes, double now)
    {
        if (!Close(now))
        {
            return false;
        }
        Selected = game;
        packageBytes = bytes;
        Failure = null;
        ReportPath = null;
        SaveDirectory = null;
        return Execute("start", now, () =>
        {
            game.Validate();
            cancellation.ThrowIfCancellationRequested();
            SaveDirectory = useRootAsSaveDirectory ? root : Path.Combine(root, SharedSaveDirectoryName);
            var package = new Package(bytes);
            int width = game.width;
            int height = game.height;
            if (width == 0)
            {
                var declared = MrpScreenResolution.ReadHeaderDimensions(package.Image);
                var size = MrpScreenResolution.Resolve(declared.Width, declared.Height, game.filename);
                width = size.Width;
                height = size.Height;
            }
            bool defaultScreen = RuntimeSession.RequiresDefaultScreen(package, width, height);
            // Short SDK buffers use the package's own ABI basename, independent of launcher aliases.
            string guestFilename = GuestFilename(bytes, game.filename, encoding);
            Session = new RuntimeSession(package, SaveDirectory,
                width: defaultScreen ? MrpScreenResolution.DefaultWidth : width,
                height: defaultScreen ? MrpScreenResolution.DefaultHeight : height,
                packageName: guestFilename, font: font, textEncoding: encoding, mythroad: mythroad,
                soundOutput: soundOutput, hostNetwork: hostNetwork, trackModuleExecution: trackModuleExecution,
                cancellation: cancellation, uptimeClock: ReadSpeedClock, storage: storage)
            {
                WindowHandles = windowHandles
            };
            if (defaultScreen)
            {
                Session.Trace.Add("compatibility screen: validated SDK dimension selector and screen-info ABI; unsupported request uses supported 240x320 default");
            }
            if (guestFilename != game.filename)
            {
                Session.Trace.Add("launcher guest filename alias: " + game.filename + " -> " + guestFilename);
            }
            Session.SimplifiedWebPages = true;
            Session.SimplifiedUcPages = package.Contains("lp_navi") && package.Contains("lp_help") &&
                package.Contains("lp_about");
            if (Session.SimplifiedUcPages)
            {
                Session.Trace.Add("UC simplified web enabled: verified 7.0.1.73 binary browsing requests; static text and links");
            }
            string startupScript = SelectStartupScript(package, Session.Trace.Add);
            Session.Trace.Add("launcher startup script: " + startupScript);
            Session.Start(startupScript);
            if (applicationPaused && Session.Started)
            {
                Session.SetPaused(true);
            }
        });
    }

    public static string GuestFilename(byte[] bytes, string filename, Encoding encoding)
    {
        if (bytes.Length >= 28)
        {
            int count = 0;
            while (count < 12 && bytes[16 + count] != 0)
            {
                count++;
            }
            try
            {
                string original = encoding.GetString(bytes, 16, count);
                if (!string.IsNullOrWhiteSpace(original) && original.EndsWith(".mrp", StringComparison.OrdinalIgnoreCase) &&
                    original.IndexOfAny(new[] { '/', '\\', ':', '\0' }) < 0)
                {
                    return original;
                }
            }
            catch (DecoderFallbackException)
            {
                // An invalid optional header name cannot escape the bounded fallback.
            }
        }
        try
        {
            if (encoding.GetByteCount(filename) <= 63)
            {
                return filename;
            }
        }
        catch (EncoderFallbackException)
        {
            // The launcher keeps the original filename; the guest requires a representable ABI alias.
        }
        return "application.mrp";
    }

    public static string SelectStartupScript(Package package, Action<string>? diagnostic = null)
    {
        string conventional = !package.Contains("start.mr") && package.Contains("_start.mr")
            ? "_start.mr" : "start.mr";
        if (!package.Contains("start.mr") || !package.Contains("shell.mr"))
        {
            return conventional;
        }
        try
        {
            // Inspect only the small, verified marker wrapper; unrelated shells can be full applications.
            var shell = ScriptChunk.Load(package.Read("shell.mr", 4096, diagnostic), "shell.mr");
            if (shell.Parameters == 0 && shell.Upvalues == 0 && shell.Children.Length == 0 &&
                shell.Constants.SequenceEqual(new object?[] { "_nes3ShP7SwK0", 370, "loadfile", "start.mr" }) &&
                shell.Code.SequenceEqual(new uint[]
                {
                    0x00000041, // LOADK R0, 370
                    0x00000007, // SETGLOBAL R0, _nes3ShP7SwK0
                    0x00000085, // GETGLOBAL R0, loadfile
                    0x010000c1, // LOADK R1, start.mr
                    0x00010099, // CALL R0, one argument, one result
                    0x01000000, // MOVE R1, R0
                    0x01008059, // CALL R1, no arguments, no results
                    0x0000801b  // RETURN no results
                }))
            {
                diagnostic?.Invoke("launcher entry recognized: shell.mr marker wrapper for start.mr");
                return "shell.mr";
            }
            diagnostic?.Invoke("launcher shell.mr not selected: not the verified marker wrapper");
        }
        catch (RuntimeFault error)
        {
            diagnostic?.Invoke("launcher shell.mr not selected: " + error.Message);
        }
        return conventional;
    }

    public bool Reopen(double now)
    {
        return Selected != null && packageBytes != null && Open(Selected, packageBytes, now);
    }

    public bool Close(double now)
    {
        Cheats?.Dispose();
        Cheats = null;
        try
        {
            if (Session == null || cancellation.IsCancellationRequested) return true;
            return Execute("stop", now, () =>
            {
                ReleaseInputs();
                Session?.Stop();
            });
        }
        finally
        {
            var session = Session;
            Session = null;
            ResetInput();
            session?.Dispose();
        }
    }

    public bool Key(int type, int key, double now)
    {
        if (Session == null || menuPaused || paused && !Session.TimerRunsWhilePaused)
        {
            return false;
        }
        return Execute("key", now, (Host: this, Type: type, Key: key, Timestamp: now),
            static input => input.Host.DeliverKey(input.Type, input.Key, input.Timestamp),
            describeFailure: static input => $"key {input.Type}:{input.Key}");
    }

    private void DeliverKey(int type, int key, double now)
    {
        if (key < 0 || key >= held.Length || type < 0 || type > 1)
        {
            throw new RuntimeFault("invalid player key");
        }
        if (type == 0 ? held[key] : !held[key])
        {
            return;
        }
        held[key] = type == 0;
        CancelDirectionRestore();
        bool direction = key >= 12 && key <= 15;
        if (!direction)
        {
            // MRP exposes key events, not action/animation state. Keep this inference brief.
            actionReleaseDeadline = type == 1 && !paused ? now + 1 / Speed : double.NegativeInfinity;
        }
        Session!.Key(type, key);
        if (direction && type == 0 && now < actionReleaseDeadline)
        {
            RestoreDirection(key, now);
        }
    }

    // Arm for a stack predecessor or a new direction following a recent action release.
    // Guests have no acceptance acknowledgement; keep retries finite and direction-only.
    public void RestoreDirection(int key, double now)
    {
        CancelDirectionRestore();
        if (Session == null || !Session.Started || paused || key < 12 || key > 15 || !held[key])
        {
            return;
        }
        for (int other = 0; other < held.Length; other++)
        {
            if (other != key && held[other])
            {
                return;
            }
        }
        restoredDirection = key;
        directionRetries = 10;
        directionRetryDeadline = now + DirectionRetrySeconds / Speed;
    }

    private void CancelDirectionRestore()
    {
        restoredDirection = -1;
        directionRetries = 0;
    }

    public void CancelDirectionBuffer()
    {
        CancelDirectionRestore();
        actionReleaseDeadline = double.NegativeInfinity;
    }

    private bool RetryDirection(double now)
    {
        if (restoredDirection < 0 || now + 1e-9 < directionRetryDeadline)
        {
            return false;
        }
        int key = restoredDirection;
        RuntimeSession? session = Session;
        if (paused || session == null || !session.Started || !held[key])
        {
            CancelDirectionRestore();
            return false;
        }
        directionRetryDeadline = now + DirectionRetrySeconds / Speed;
        if (--directionRetries == 0)
        {
            CancelDirectionRestore();
        }
        return Execute("direction restore retry", now, (Session: session, Key: key), static input =>
        {
            // Bypass ordinary duplicate-down filtering without changing physical ownership.
            input.Session.Key(1, input.Key);
            if (input.Session.Started)
            {
                input.Session.Key(0, input.Key);
            }
        });
    }

    public bool Touch(int type, int x, int y, double now)
    {
        if (Session == null || menuPaused || paused && !Session.TimerRunsWhilePaused || type != 2 && !pointerDown)
        {
            return false;
        }
        return Execute("touch", now, (Host: this, Type: type, X: x, Y: y),
            static input => input.Host.DeliverTouch(input.Type, input.X, input.Y),
            describeFailure: static input => $"touch {input.Type}");
    }

    private void DeliverTouch(int type, int x, int y)
    {
        CancelDirectionBuffer();
        pointerX = x;
        pointerY = y;
        pointerDown = type != 3;
        Session!.Touch(type, x, y);
    }

    public bool EditText(uint handle, string text, double now)
    {
        if (!CanEdit(handle))
        {
            return false;
        }
        bool accepted = false;
        return Execute("edit text", now, () => accepted = Session!.EditText(handle, text)) && accepted;
    }

    public bool EditChoice(uint handle, int choice, double now)
    {
        if (!CanEdit(handle))
        {
            return false;
        }
        return Execute("edit choice", now, () => Session!.EditChoice(handle, choice));
    }

    private bool CanEdit(uint handle) => !paused &&
        Session?.NativeEdit is { Submitted: false } edit && edit.Handle == handle;

    public void Pause(bool value, double now)
    {
        SetPauseState(menuPaused, value, now);
    }

    public void PauseMenu(bool value, double now)
    {
        SetPauseState(value, applicationPaused, now);
    }

    private void SetPauseState(bool nextMenuPaused, bool nextApplicationPaused, double now)
    {
        ReadSpeedClock();
        bool value = nextMenuPaused || nextApplicationPaused;
        bool resuming = paused && !value;
        if (value && !paused && Session != null)
        {
            Execute("pause input release", now, ReleaseInputs);
        }
        if (nextApplicationPaused != applicationPaused && Session != null)
        {
            Execute(nextApplicationPaused ? "pause runtime" : "resume runtime", now,
                () => Session!.SetPaused(nextApplicationPaused));
        }
        menuPaused = nextMenuPaused;
        applicationPaused = nextApplicationPaused;
        paused = value;
        soundOutput?.Pause(value);
        if (!value)
        {
            if (resuming)
            {
                lastArms = -1;
                Deadline = double.PositiveInfinity;
            }
            SyncTimer(now);
        }
    }

    public void SetSpeed(double value, double now)
    {
        if (value != .5 && value != 1 && value != 1.5 && value != 2 && value != 3 && value != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Unsupported game speed.");
        }
        ReadSpeedClock();
        double ratio = Speed / value;
        if (!double.IsPositiveInfinity(Deadline))
        {
            Deadline = now + Math.Max(0, Deadline - now) * ratio;
        }
        timerInterval *= ratio;
        if (restoredDirection >= 0)
        {
            directionRetryDeadline = now + Math.Max(0, directionRetryDeadline - now) * ratio;
        }
        actionReleaseDeadline = actionReleaseDeadline > now
            ? now + (actionReleaseDeadline - now) * ratio : double.NegativeInfinity;
        lastTimerPump = now;
        Speed = value;
    }

    private uint ReadSpeedClock()
    {
        long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        // RuntimeSession rebases each outer guest call, excluding idle/pause gaps.
        // Keep polling live during startup, lifecycle hooks and allowed paused timers.
        speedClockMilliseconds += (timestamp - speedClockTimestamp) *
            (1000.0 / System.Diagnostics.Stopwatch.Frequency) * Speed;
        speedClockTimestamp = timestamp;
        return unchecked((uint)(long)speedClockMilliseconds);
    }

    public bool Update(double now)
    {
        Cheats?.ApplyFreezes();
        RuntimeSession? session = Session;
        if (session == null || menuPaused)
        {
            return false;
        }
        if (paused && !session.TimerRunsWhilePaused)
        {
            SyncTimer(now);
            if (now >= Deadline)
            {
                Execute("ignored paused timer", now, session, static runtime => runtime.Tick());
            }
            return false;
        }
        if (!Execute("platform", now, session, static runtime => runtime.PumpPlatform()))
        {
            return false;
        }
        SyncTimer(now);
        if (minimumTimerSeconds > 0 && !double.IsPositiveInfinity(Deadline))
        {
            // Discard excess stall time, as FrameTimer did, without a catch-up burst.
            double elapsed = Math.Max(0, now - lastTimerPump);
            if (elapsed > timerInterval)
            {
                Deadline += elapsed - timerInterval;
            }
            lastTimerPump = now;
        }
        bool ticked = now + 1e-9 >= Deadline && Execute("timer", now, Session!, static runtime => runtime.Tick(),
            minimumTimerSeconds > 0 ? Deadline : (double?)null);
        bool retried = RetryDirection(now);
        return ticked || retried;
    }

    private void SyncTimer(double now, double? rearmOrigin = null)
    {
        if (Session == null || !Session.Started || Session.NextTimerMilliseconds == 0)
        {
            Deadline = double.PositiveInfinity;
            return;
        }
        int arms = unchecked((int)Session.TimerGeneration);
        if (lastArms != arms || double.IsPositiveInfinity(Deadline))
        {
            lastArms = arms;
            timerInterval = Math.Max(minimumTimerSeconds, Session.NextTimerMilliseconds / 1000.0) / Speed;
            Deadline = (rearmOrigin ?? now) + timerInterval;
            lastTimerPump = now;
        }
    }

    private void ReleaseInputs()
    {
        CancelDirectionBuffer();
        for (int key = 0; key < held.Length; key++)
        {
            if (held[key] && Session != null && Session.Started)
            {
                held[key] = false;
                Session.Key(1, key);
            }
        }
        if (pointerDown && Session != null && Session.Started)
        {
            Session.Touch(3, pointerX, pointerY);
        }
        Array.Clear(held, 0, held.Length);
        pointerDown = false;
    }

    private void ResetInput()
    {
        CancelDirectionBuffer();
        Array.Clear(held, 0, held.Length);
        pointerDown = false;
        lastArms = -1;
        Deadline = double.PositiveInfinity;
    }

    private bool Execute(string operation, double now, Action action) =>
        Execute(operation, now, action, static callback => callback());

    private bool Execute<T>(string operation, double now, T state, Action<T> action, double? rearmOrigin = null,
        Func<T, string>? describeFailure = null)
    {
        try
        {
            RuntimeSession? previousSession = Session;
            uint stops = Session?.TimerStopGeneration ?? 0;
            action(state);
            RuntimeSession? next = Session?.TakeSuccessor();
            if (next != null)
            {
                Session = next;
                ResetInput();
            }
            if (Session != null && !Session.Started)
            {
                Session.Dispose();
                Session = null;
                ResetInput();
            }
            bool sessionChanged = !ReferenceEquals(previousSession, Session);
            if (sessionChanged || Session?.TimerStopGeneration != stops)
            {
                rearmOrigin = null;
            }
            if (sessionChanged)
            {
                Cheats?.Dispose();
                Cheats = Session == null ? null : new GameCheats(Session, cheatAccess);
            }
            Cheats?.ApplyFreezes();
            SyncTimer(now, rearmOrigin);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            RuntimeSession? failedSession = Session;
            Session = null;
            ResetInput();
            if (describeFailure != null) operation = describeFailure(state);
            Failure = operation + ": " + error.Message;
            Cheats?.Dispose();
            Cheats = null;
            string report = "Game: " + Selected?.id + "\nPackage: " + Selected?.filename +
                "\nSHA256: " + Selected?.sha256 + "\nDiagnostic: " + Selected?.diagnostic +
                "\nOperation: " + operation + "\nUTC: " + DateTime.UtcNow.ToString("O") +
                "\nSave directory: " + SaveDirectory + "\n" + error + "\nTrace:\n" +
                (failedSession == null ? "" : string.Join("\n", failedSession.Trace));
            try
            {
                failedSession?.Dispose();
            }
            catch (Exception cleanupError)
            {
                Failure += "; session cleanup failed: " + cleanupError.Message;
                report += "\nSession cleanup failed:\n" + cleanupError + "\n";
            }
            try
            {
                string path = Path.Combine(root, "reports", Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(path, report);
                ReportPath = path;
            }
            catch (Exception writeError)
            {
                ReportPath = null;
                Failure += "; could not write report: " + writeError.Message;
            }
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            Close(0);
        }
        finally
        {
            packageBytes = null;
            (soundOutput as IDisposable)?.Dispose();
        }
    }
}
