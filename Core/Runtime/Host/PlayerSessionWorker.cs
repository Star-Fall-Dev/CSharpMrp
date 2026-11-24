#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CSharpMrp
{
    public sealed class PlayerSessionState
    {
        public readonly bool Started;
        public readonly bool Finished;
        public readonly string? Failure;
        public readonly string? ReportPath;
        public readonly int Width;
        public readonly int Height;
        public readonly long FrameVersion;
        public readonly int Timers;
        public readonly long Instructions;
        public readonly int OwnerThread;
        public readonly RuntimeSession.NativeEditWindow? Edit;

        internal PlayerSessionState(bool started, bool finished, PlayerSessionHost? host, long frameVersion,
            RuntimeSession.NativeEditWindow? edit, int ownerThread, string? failure = null)
        {
            Started = started;
            Finished = finished;
            Failure = failure ?? host?.Failure;
            ReportPath = host?.ReportPath;
            Width = host?.Session?.Width ?? 0;
            Height = host?.Session?.Height ?? 0;
            FrameVersion = frameVersion;
            Timers = host?.Session?.Timers ?? 0;
            Instructions = host?.Session?.Cpu.Instructions ?? 0;
            OwnerThread = ownerThread;
            Edit = edit;
        }
    }

    /// <summary>One owner for all guest calls, pixels, callbacks and files. Unity never reads the live VM.</summary>
    public sealed class PlayerSessionWorker : IDisposable
    {
        private enum CommandKind { Callback, Key, Touch, MappedTouch }

        private readonly struct Command
        {
            private readonly CommandKind kind;
            private readonly Action<PlayerSessionHost, double>? callback;
            private readonly CancellationToken cancellation;
            private readonly int type, first, second, displayedWidth, displayedHeight;

            internal Command(Action<PlayerSessionHost, double> callback, CancellationToken cancellation = default) : this()
            {
                this.callback = callback;
                this.cancellation = cancellation;
            }

            internal Command(CommandKind kind, int type, int first, int second = 0,
                int displayedWidth = 0, int displayedHeight = 0) : this()
            {
                this.kind = kind;
                this.type = type;
                this.first = first;
                this.second = second;
                this.displayedWidth = displayedWidth;
                this.displayedHeight = displayedHeight;
            }

            internal bool IsCancelled => cancellation.IsCancellationRequested;

            internal void Execute(PlayerSessionHost host, double now)
            {
                switch (kind)
                {
                    case CommandKind.Callback:
                        callback!(host, now);
                        break;
                    case CommandKind.Key:
                        host.Key(type, first, now);
                        break;
                    case CommandKind.Touch:
                        host.Touch(type, first, second, now);
                        break;
                    case CommandKind.MappedTouch:
                        if (host.Session is { Started: true } guest)
                        {
                            // The guest can resize between presentation and input delivery.
                            int x = (int)((long)first * guest.Width / displayedWidth);
                            int y = (int)((long)second * guest.Height / displayedHeight);
                            host.Touch(type, x, y, now);
                        }
                        break;
                }
            }
        }

        private const int MaximumCommands = 128;
        private readonly object gate = new object();
        private readonly object frameGate = new object();
        private readonly Queue<Command> commands = new Queue<Command>();
        private int cancelledCommands;
        private readonly AutoResetEvent? wake;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> completion;
        private Func<CancellationToken, PlayerSessionHost>? singleFactory;
        private PlayerGame? singleGame;
        private byte[]? singleBytes;
        private PlayerSessionHost? singleHost;
        private bool singleStarted;
        private bool singleFinished;
        private bool singlePumping;
        private readonly int singleOwner;
        public bool SingleThreaded { get; }
        private PlayerSessionState state = new PlayerSessionState(false, false, null, 0, null, 0);
        private byte[]? frame;
        private long[] frameRowVersions = Array.Empty<long>();
        private PlayerSessionState? frameState;
        private int frameFirstRow;
        private int frameEndRow;
        private long version;
        private int lastFrames = -1;
        private RuntimeSession? lastSession;
        private RuntimeSession.NativeEditWindow? edit;
        private bool stopping;
        public PlayerSessionState State => Volatile.Read(ref state);
        public Task Completion => completion.Task;

        public PlayerSessionWorker(Func<CancellationToken, PlayerSessionHost> createHost, PlayerGame game, byte[] bytes,
            bool singleThreaded = false)
        {
            SingleThreaded = singleThreaded;
            singleOwner = Thread.CurrentThread.ManagedThreadId;
            completion = new TaskCompletionSource<bool>(singleThreaded
                ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);
            if (singleThreaded)
            {
                singleFactory = createHost;
                singleGame = game;
                singleBytes = bytes;
            }
            else
            {
                wake = new AutoResetEvent(false);
                Task.Factory.StartNew(() => Run(createHost, game, bytes), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        /// <summary>One bounded queue drain and timer update; only the single-thread owner calls this.</summary>
        public void Pump()
        {
            if (!SingleThreaded || singleFinished)
            {
                return;
            }
            if (singlePumping)
            {
                throw new InvalidOperationException("The single-thread guest pump cannot be reentered.");
            }
            if (Thread.CurrentThread.ManagedThreadId != singleOwner)
            {
                throw new InvalidOperationException("The single-thread guest must be pumped by its creating owner.");
            }
            if (stopping)
            {
                FinishSingleThread(null);
                return;
            }
            singlePumping = true;
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                int owner = Thread.CurrentThread.ManagedThreadId;
                if (singleHost == null)
                {
                    singleHost = singleFactory!(lifetime.Token);
                    singleStarted = singleHost.Open(singleGame!, singleBytes!, Now);
                    singleFactory = null;
                    singleGame = null;
                    singleBytes = null;
                    Publish(singleHost, singleStarted, owner);
                }
                if (!singleStarted || singleHost.Session == null || !UpdateHost(singleHost, owner))
                {
                    FinishSingleThread(null);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                FinishSingleThread(null);
            }
            catch (Exception error)
            {
                FinishSingleThread(error.ToString());
            }
            finally
            {
                singlePumping = false;
                if (stopping && !singleFinished)
                {
                    FinishSingleThread(null);
                }
            }
        }

        private void FinishSingleThread(string? failure)
        {
            if (singleFinished)
            {
                return;
            }
            singleFinished = true;
            var host = singleHost;
            singleHost = null;
            singleFactory = null;
            singleGame = null;
            singleBytes = null;
            Finish(host, singleStarted, singleOwner, failure);
        }

        public bool Enqueue(Action<PlayerSessionHost, double> command) => Enqueue(new Command(command), false);

        internal bool EnqueueKey(int type, int key, bool allowRetry) =>
            Enqueue(new Command(CommandKind.Key, type, key), allowRetry);

        internal bool EnqueueTouch(int type, int x, int y) =>
            Enqueue(new Command(CommandKind.Touch, type, x, y), false);

        internal bool TryEnqueueMappedTouch(int type, int x, int y, int displayedWidth, int displayedHeight) =>
            Enqueue(new Command(CommandKind.MappedTouch, type, x, y, displayedWidth, displayedHeight), true);

        private bool Enqueue(Command command, bool allowRetry)
        {
            lock (gate)
            {
                if (stopping || command.IsCancelled)
                {
                    return false;
                }
                if (commands.Count >= MaximumCommands)
                {
                    if (Interlocked.Exchange(ref cancelledCommands, 0) != 0)
                    {
                        for (int remaining = commands.Count; remaining > 0; remaining--)
                        {
                            Command queued = commands.Dequeue();
                            if (!queued.IsCancelled) commands.Enqueue(queued);
                        }
                    }
                    if (commands.Count >= MaximumCommands)
                    {
                        if (allowRetry) return false;
                        throw new InvalidOperationException("Independent MRP input queue exceeded 128 commands.");
                    }
                }
                commands.Enqueue(command);
                wake?.Set();
                return true;
            }
        }

        public bool TryCopyFrame(long previous, ref byte[]? destination, out PlayerSessionState? published)
            => TryCopyFrame(previous, ref destination, out published, false, out _, out _);

        // The caller retains this buffer between submissions. The exclusive row range is
        // relative to its own last copy, even if it missed intermediate publications.
        public bool TryCopyChangedRows(long previous, ref byte[]? destination, out PlayerSessionState? published,
            out int firstRow, out int endRow)
            => TryCopyFrame(previous, ref destination, out published, true, out firstRow, out endRow);

        private bool TryCopyFrame(long previous, ref byte[]? destination, out PlayerSessionState? published,
            bool compareRows, out int firstRow, out int endRow)
        {
            firstRow = endRow = 0;
            lock (frameGate)
            {
                published = frameState;
                if (frame == null || published == null || published.FrameVersion == previous)
                {
                    return false;
                }
                endRow = published.Height;
                int stride = published.Width * 2;
                if (destination == null || destination.Length != frame.Length)
                {
                    destination = new byte[frame.Length];
                }
                else if (compareRows)
                {
                    if (previous == published.FrameVersion - 1)
                    {
                        firstRow = frameFirstRow;
                        endRow = frameEndRow;
                    }
                    else if (previous > 0 && previous < published.FrameVersion)
                    {
                        // Rows last published at or before the caller's version already match.
                        while (firstRow < endRow && frameRowVersions[firstRow] <= previous) firstRow++;
                        while (endRow > firstRow && frameRowVersions[endRow - 1] <= previous) endRow--;
                    }
                    while (firstRow < endRow && frame.AsSpan(firstRow * stride, stride)
                        .SequenceEqual(destination.AsSpan(firstRow * stride, stride)))
                        firstRow++;
                    while (endRow > firstRow && frame.AsSpan((endRow - 1) * stride, stride)
                        .SequenceEqual(destination.AsSpan((endRow - 1) * stride, stride)))
                        endRow--;
                }
                if (firstRow < endRow)
                    Buffer.BlockCopy(frame, firstRow * stride, destination, firstRow * stride, (endRow - firstRow) * stride);
                else
                    firstRow = endRow = published.Height;
                return true;
            }
        }

        public Task<T> InvokeAsync<T>(Func<PlayerSessionHost, double, T> operation,
            CancellationToken cancellation = default)
        {
            if (cancellation.IsCancellationRequested) return Task.FromCanceled<T>(cancellation);
            if (operation == null) return Task.FromException<T>(new ArgumentNullException(nameof(operation)));

            var result = new TaskCompletionSource<T>(SingleThreaded
                ? TaskCreationOptions.None : TaskCreationOptions.RunContinuationsAsynchronously);
            Func<PlayerSessionHost, double, T>? pending = operation;
            return EnqueueAndWait();

            void Execute(PlayerSessionHost host, double now)
            {
                var callback = Interlocked.Exchange(ref pending, null);
                if (callback == null) return;
                try
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        result.TrySetCanceled(cancellation);
                        return;
                    }
                    if (host.Session == null)
                    {
                        throw new InvalidOperationException("The guest has closed.");
                    }
                    result.TrySetResult(callback(host, now));
                }
                catch (Exception error)
                {
                    result.TrySetException(error);
                }
            }

            void Cancel()
            {
                if (Interlocked.Exchange(ref pending, null) != null)
                    Volatile.Write(ref cancelledCommands, 1);
                result.TrySetCanceled(cancellation);
            }

            async Task<T> EnqueueAndWait()
            {
                using CancellationTokenRegistration registration = cancellation.CanBeCanceled
                    ? cancellation.Register(Cancel) : default;
                try
                {
                    if (!Enqueue(new Command(Execute, cancellation), false))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        throw new OperationCanceledException("The guest has closed.");
                    }
                    await WaitForCommand(result.Task).ConfigureAwait(false);
                    return await result.Task.ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref pending, null);
                }
            }
        }

        public Task SetSpeedAsync(double speed)
        {
            return InvokeAsync((host, now) =>
            {
                host.SetSpeed(speed, now);
                return true;
            });
        }

        public Task SetResolutionAsync(int width, int height)
        {
            return InvokeAsync((host, _) =>
            {
                host.Session!.SetResolution(width, height);
                Publish(host, true, Thread.CurrentThread.ManagedThreadId);
                return true;
            });
        }

        private async Task WaitForCommand(Task request)
        {
            Task finished = await Task.WhenAny(request, Completion).ConfigureAwait(false);
            await finished.ConfigureAwait(false);
            if (finished != request)
            {
                throw new OperationCanceledException("The guest closed before processing the command.");
            }
        }

        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        private bool UpdateHost(PlayerSessionHost host, int owner)
        {
            CancellationToken token = lifetime.Token;
            for (int count = 0; count < MaximumCommands; count++)
            {
                Command command;
                lock (gate)
                {
                    if (!commands.TryDequeue(out command)) break;
                }
                token.ThrowIfCancellationRequested();
                command.Execute(host, Now);
                if (host.Session == null) return false;
            }
            token.ThrowIfCancellationRequested();
            host.Update(Now);
            Publish(host, true, owner);
            return host.Session != null;
        }

        private void Run(Func<CancellationToken, PlayerSessionHost> createHost, PlayerGame game, byte[] bytes)
        {
            PlayerSessionHost? host = null;
            int owner = Thread.CurrentThread.ManagedThreadId;
            bool started = false;
            string? failure = null;
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                host = createHost(lifetime.Token);
                started = host.Open(game, bytes, Now);
                Publish(host, started, owner);
                while (started && host.Session != null)
                {
                    if (!UpdateHost(host, owner)) break;
                    // Enqueue and Dispose both signal wake. A paused menu has no platform or timer work.
                    if (host.MenuPaused)
                    {
                        wake!.WaitOne();
                        continue;
                    }
                    double remaining = host.WakeDeadline - Now;
                    int delay = double.IsPositiveInfinity(remaining) ? 50 :
                        Math.Max(1, Math.Min(50, (int)Math.Ceiling(remaining * 1000)));
                    wake!.WaitOne(delay);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // The owning thread still disposes all guest files before acknowledging stop.
            }
            catch (Exception error)
            {
                failure = error.ToString();
            }
            finally
            {
                Finish(host, started, owner, failure);
            }
        }

        private void Finish(PlayerSessionHost? host, bool started, int owner, string? failure)
        {
            lock (gate)
            {
                stopping = true;
                commands.Clear();
            }
            try
            {
                host?.Dispose();
            }
            catch (Exception error)
            {
                failure = (failure == null ? "" : failure + "\n") + "Guest disposal failed: " + error;
            }
            // A caller can keep the final pixels and status after the VM has gone away.
            lastSession = null;
            edit = null;
            Volatile.Write(ref state, new PlayerSessionState(started, true, host, version, null, owner, failure));
            wake?.Dispose();
            lifetime.Dispose();
            if (failure == null) completion.TrySetResult(true);
            else completion.TrySetException(new RuntimeFault(failure));
        }

        private void Publish(PlayerSessionHost host, bool started, int owner)
        {
            RuntimeSession? session = host.Session;
            RuntimeSession.NativeEditWindow? current = session?.NativeEdit;
            if (current == null)
            {
                edit = null;
            }
            else if (session != lastSession || edit == null || current.Handle != edit.Handle ||
                current.Text != edit.Text || current.Submitted != edit.Submitted)
            {
                edit = new RuntimeSession.NativeEditWindow(current.Handle, current.Title, current.Text,
                    current.Type, current.MaximumLength, 0) { Submitted = current.Submitted };
            }
            if (session != null && (session != lastSession || lastFrames != session.Frames || session.HasDirtyRows ||
                session.Width != frameState?.Width || session.Height != frameState?.Height))
            {
                lock (frameGate)
                {
                    bool fullCopy = session != lastSession || frameState?.Width != session.Width ||
                        frameState?.Height != session.Height;
                    if (frame == null || frame.Length != session.Width * session.Height * 2)
                    {
                        frame = new byte[session.Width * session.Height * 2];
                    }
                    session.CopyFrameChangesTo(frame, fullCopy, out frameFirstRow, out frameEndRow);
                    version++;
                    if (frameRowVersions.Length < session.Height)
                    {
                        frameRowVersions = new long[session.Height];
                    }
                    frameRowVersions.AsSpan(frameFirstRow, frameEndRow - frameFirstRow).Fill(version);
                    frameState = new PlayerSessionState(started, false, host, version, edit, owner);
                    Volatile.Write(ref state, frameState);
                }
                lastSession = session;
                lastFrames = session.Frames;
                return;
            }
            PlayerSessionState previous = State;
            if (previous.Started != started || previous.Timers != (session?.Timers ?? 0) ||
                previous.Instructions != (session?.Cpu.Instructions ?? 0) ||
                !ReferenceEquals(previous.Edit, edit) || previous.Failure != host.Failure)
            {
                Volatile.Write(ref state, new PlayerSessionState(started, false, host, version, edit, owner));
            }
        }

        public void Dispose()
        {
            bool finishOnOwner = false;
            try
            {
                lock (gate)
                {
                    if (stopping)
                    {
                        return;
                    }
                    stopping = true;
                    finishOnOwner = SingleThreaded && !singlePumping &&
                        Thread.CurrentThread.ManagedThreadId == singleOwner;
                    try
                    {
                        lifetime.Cancel();
                    }
                    finally
                    {
                        commands.Clear();
                        wake?.Set();
                    }
                }
            }
            finally
            {
                if (finishOnOwner)
                {
                    FinishSingleThread(null);
                }
            }
        }
    }
}
