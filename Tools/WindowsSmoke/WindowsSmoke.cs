using System.Diagnostics;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CSharpMrp.Desktop;

internal static class WindowsSmoke
{
    [STAThread]
    private static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ApplicationConfiguration.Initialize();
        using var lifetime = new ApplicationContext();
        int exit = 1;
        Application.Idle += Run;
        Application.Run(lifetime);
        return exit;

        async void Run(object? sender, EventArgs e)
        {
            Application.Idle -= Run;
            try
            {
                await Verify(args);
                Console.WriteLine("PASS: WinForms pixels, pointer capture, mouse keypad, keyboard, guest edit, BMP save/reopen and resolution");
                exit = 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
            }
            finally
            {
                lifetime.ExitThread();
            }
        }
    }

    private static async Task Verify(string[] args)
    {
        int rootIndex = Array.IndexOf(args, "--save-root");
        if (rootIndex < 0 || rootIndex + 1 >= args.Length || Directory.Exists(args[rootIndex + 1]))
        {
            throw new ArgumentException("Pass a new --save-root directory and the local --game bmpdoor.mrp.");
        }
        string root = args[rootIndex + 1];
        int gameIndex = Array.IndexOf(args, "--game");
        int resourceIndex = Array.IndexOf(args, "--mythroad");
        if (gameIndex < 0 || gameIndex + 1 >= args.Length ||
            resourceIndex < 0 || resourceIndex + 1 >= args.Length)
        {
            throw new ArgumentException("Pass --game and --mythroad for the real guest and mounted-resource checks.");
        }
        VerifyStorage(args[gameIndex + 1], args[resourceIndex + 1], root + "-storage");
        VerifyPartialPresentation();
        VerifyRgb565Colors();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        async Task WaitFor(Func<bool> check, string operation)
        {
            while (!check())
            {
                if (limit.IsCancellationRequested)
                {
                    throw new TimeoutException(operation);
                }
                await Task.Delay(25);
            }
        }
        using var firstWindow = new PlayerWindow(args);
        PlayerWindow window = firstWindow;
        var workerField = typeof(PlayerWindow).GetField("worker", BindingFlags.Instance | BindingFlags.NonPublic)!;
        PlayerSessionWorker? Worker() => (PlayerSessionWorker?)workerField.GetValue(window);
        DesktopFrame Presenter() => Descendants(window).OfType<DesktopFrame>().Single();
        int PresentedPixel() => ReadPresentedPixel(Presenter(), 5, 5);
        Button ButtonFor(string label) => Descendants(window).OfType<Button>().Single(button => button.Text == label);
        async Task Click(string label)
        {
            var button = ButtonFor(label);
            if (label == "#")
            {
                var center = new Point(button.Width / 2, button.Height / 2);
                PostMouse(button, 0x0201, center, 1);
                await Task.Delay(55);
                PostMouse(button, 0x0202, center);
            }
            else
            {
                button.PerformClick();
            }
            await Task.Delay(65);
        }
        async Task Tap(Keys key)
        {
            Control target = ButtonFor("Open MRP…");
            Post(target, 0x0100, (nint)key);
            await Task.Delay(55);
            Post(target, 0x0101, (nint)key);
            await Task.Delay(65);
        }
        async Task<uint> Pixel() => await Worker()!.InvokeAsync((owner, _) =>
            owner.Session!.Memory.U16(owner.Session.Framebuffer + (5 * 240 + 5) * 2));
        window.Show();
        await WaitFor(() => Worker()?.State.FrameVersion > 0, "first game frame");
        ButtonFor("Open MRP…").Focus();
        if (await Pixel() != 0xffff)
        {
            throw new InvalidOperationException("The initial canvas must be white.");
        }
        await WaitFor(() => PresentedPixel() == unchecked((int)0xffffffff), "first white WinForms pixel");
        await Click("#");
        await Tap(Keys.Down);
        await Tap(Keys.Down);
        await Tap(Keys.Down);
        await Tap(Keys.F1);
        await Tap(Keys.Enter);
        if (await Pixel() != 0x10a2)
        {
            throw new InvalidOperationException("The mixed pointer and keyboard input did not paint the expected pixel.");
        }
        await WaitFor(() => PresentedPixel() == unchecked((int)0xff101410), "changed WinForms pixel after guest drawing");
        await Tap(Keys.F1);
        await Tap(Keys.Down);
        await Tap(Keys.Down);
        await Tap(Keys.Down);
        await Tap(Keys.Enter);
        var first = Worker()!;
        await Click("Close game");
        await first.Completion;
        window.Close();

        var saved = Directory.GetFiles(root, "*.bmp", SearchOption.AllDirectories)
            .Select(File.ReadAllBytes).Single(bytes => bytes.Length == 582);
        if (saved[550] != 0xa2 || saved[551] != 0x10)
        {
            throw new InvalidOperationException("The saved BMP did not contain the expected pixel.");
        }

        using var reopenedWindow = new PlayerWindow(args);
        window = reopenedWindow;
        window.Show();
        await WaitFor(() => Worker()?.State.FrameVersion > 0, "fresh game frame");
        ButtonFor("Open MRP…").Focus();
        await Tap(Keys.F1);
        await Tap(Keys.Down);
        await Tap(Keys.Enter);
        await Tap(Keys.Down);
        await Tap(Keys.Enter);
        if (await Pixel() != 0x10a2)
        {
            throw new InvalidOperationException("A new session did not reopen the saved canvas.");
        }
        var active = Worker()!;
        Task<uint> OpenGuestEdit() => active.InvokeAsync((owner, _) =>
        {
            var vm = owner.Session!;
            uint title = vm.Memory.CopyIn(Encoding.BigEndianUnicode.GetBytes("Edit\0"));
            uint initial = vm.Memory.CopyIn(new byte[2]);
            return vm.Cpu.Invoke(0xf0000000u + 75 * 4, new[] { title, initial, 0u, 8u });
        });
        uint editHandle = await OpenGuestEdit();
        var editor = Descendants(window).OfType<TextBox>().Single();
        await WaitFor(() => editor.Visible && editor.Focused, "guest text prompt");
        foreach (char character in "A中") Post(editor, 0x0102, character);
        await WaitFor(() => editor.Text == "A中", "WinForms Unicode input");
        await Click("Confirm");
        await WaitFor(() => active.State.Edit?.Submitted == true, "guest text confirmation");
        if (active.State.Edit!.Handle != editHandle || active.State.Edit.Text != "A中")
        {
            throw new InvalidOperationException("Text did not reach the real guest edit buffer.");
        }
        await active.InvokeAsync((owner, _) => owner.Session!.Cpu.Invoke(0xf0000000u + 76 * 4, new[] { editHandle }));
        await WaitFor(() => !editor.Visible, "guest prompt closed");
        editHandle = await OpenGuestEdit();
        await WaitFor(() => editor.Visible && editor.Focused, "guest cancel prompt");
        foreach (char character in "Discard") Post(editor, 0x0102, character);
        await WaitFor(() => editor.Text == "Discard", "pending WinForms text");
        await Click("Cancel");
        await WaitFor(() => active.State.Edit?.Submitted == true, "guest edit cancellation");
        if (active.State.Edit!.Text.Length != 0)
        {
            throw new InvalidOperationException("Cancel committed the pending text.");
        }
        await active.InvokeAsync((owner, _) => owner.Session!.Cpu.Invoke(0xf0000000u + 76 * 4, new[] { editHandle }));
        await WaitFor(() => !editor.Visible, "cancelled prompt closed");
        var guide = Descendants(window).OfType<Label>().Single(label => label.Text.StartsWith("支持触摸屏，"));
        var keypad = ButtonFor("R").Parent!;
        if (!guide.Visible || !guide.Text.Contains("Esc / Backspace → Back") || ButtonFor("R").Height < 40 ||
            ButtonFor("R").Right > keypad.Parent!.ClientSize.Width ||
            guide.PointToScreen(Point.Empty).Y < keypad.PointToScreen(new Point(0, keypad.Height)).Y)
            throw new InvalidOperationException("The enlarged keypad and the keyboard guide must fit in their column.");
        using (var screenshot = new Bitmap(window.Width, window.Height))
        {
            window.DrawToBitmap(screenshot, new Rectangle(Point.Empty, screenshot.Size));
            screenshot.Save(Path.Combine(root, "desktop-game.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        var presenter = Presenter();
        Point center = new(presenter.Width / 2, presenter.Height / 2);
        PostMouse(presenter, 0x0201, center, 1);
        await WaitFor(() => presenter.Capture, "mouse captured by the game screen");
        PostMouse(presenter, 0x0200, new Point(-10, presenter.Height + 10), 1);
        PostMouse(presenter, 0x0202, new Point(-10, presenter.Height + 10));
        await WaitFor(() => !presenter.Capture, "mouse released outside the game screen");
        PostMouse(presenter, 0x0201, center, 1);
        await WaitFor(() => presenter.Capture, "game screen recaptured the mouse");
        presenter.Capture = false;
        var pointerField = typeof(PlayerWindow).GetField("screenPointer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int? CapturedPointer() => (int?)pointerField.GetValue(window);
        await WaitFor(() => CapturedPointer() == null, "lost mouse capture released the guest touch");
        Point screenCenter = presenter.PointToScreen(center);
        SendPointer(presenter, 0x0246, 71, screenCenter); // WM_POINTERDOWN uses screen coordinates.
        await WaitFor(() => CapturedPointer() == 71, "touch contact reached the guest through WM_POINTERDOWN");
        SendPointer(presenter, 0x024C, 71); // WM_POINTERCAPTURECHANGED
        await WaitFor(() => CapturedPointer() == null, "lost pointer capture released the guest touch");
        SendPointer(presenter, 0x0246, 72, screenCenter);
        await WaitFor(() => CapturedPointer() == 72, "new touch contact after losing capture");
        SendPointer(presenter, 0x0247, 72, screenCenter); // WM_POINTERUP
        await WaitFor(() => CapturedPointer() == null, "touch contact released through WM_POINTERUP");
        var resolution = Descendants(window).OfType<ComboBox>().Single();
        await WaitFor(() => resolution.Enabled, "resolution selector enabled");
        var commit = typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", BindingFlags.Instance | BindingFlags.NonPublic)!;
        async Task Resize(int width, int height)
        {
            resolution.SelectedItem = resolution.Items.Cast<MrpScreenResolution>()
                .Single(size => size.Width == width && size.Height == height);
            commit.Invoke(resolution, new object[] { EventArgs.Empty });
            await WaitFor(() => presenter.FrameBitmap?.Size == new Size(width, height), "resized WinForms bitmap");
            if (Worker() != active || active.State.Width != width || active.State.Height != height)
                throw new InvalidOperationException("Changing resolution must keep the active MRP session.");
        }
        await Resize(128, 128);
        await WaitFor(() => resolution.Enabled, "resolution selector re-enabled");
        await Resize(240, 320);
        var physicalKeys = new[] { (Keys.OemMinus, 10), (Keys.Multiply, 10), (Keys.Oemplus, 11), (Keys.Add, 11) };
        var held = typeof(PlayerSessionHost).GetField("held", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var keyboardTarget = ButtonFor("Open MRP…");
        keyboardTarget.Focus();
        foreach (var (key, guest) in physicalKeys)
        {
            Task<bool> IsHeld() => active.InvokeAsync((owner, _) => ((bool[])held.GetValue(owner)!)[guest]);
            Post(keyboardTarget, 0x0100, (nint)key);
            await Task.Delay(55);
            if (!await IsHeld()) throw new InvalidOperationException($"{key} must press guest key {guest}.");
            Post(keyboardTarget, 0x0101, (nint)key);
            await Task.Delay(55);
            if (await IsHeld()) throw new InvalidOperationException($"{key} must release guest key {guest}.");
        }
        await Click("Close game");
        await active.Completion;
        window.Close();
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void PostMouse(Control target, int message, Point point, int buttons = 0) =>
        Post(target, message, buttons, (point.Y << 16) | (point.X & 0xffff));

    private static void SendPointer(Control target, int message, int pointerId, Point position = default) =>
        SendMessage(target.Handle, message, pointerId, (position.Y << 16) | (position.X & 0xffff));

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);

    private static void Post(Control target, int message, nint wParam, nint lParam = 0)
    {
        if (!PostMessage(target.Handle, message, wParam, lParam)) throw new Win32Exception();
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);

    private static int ReadPresentedPixel(DesktopFrame presenter, int x, int y) =>
        presenter.FrameBitmap?.GetPixel(x, y).ToArgb() ?? 0;

    private static void VerifyPartialPresentation()
    {
        using var presenter = new DesktopFrame();
        const int width = 240, height = 320, changedRow = 13;
        var pixels = new byte[width * height * 2];
        pixels[0] = 0x1f;
        pixels[(changedRow - 1) * width * 2 + 1] = 0xf8;
        pixels[(changedRow + 1) * width * 2] = 0x1f;
        long before = GC.GetAllocatedBytesForCurrentThread();
        presenter.Present(pixels, width, height);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"First 240x320 bitmap presentation: {allocated} managed bytes");
        presenter.Size = new Size(400, 320);
        if (presenter.TryMapTouch(new Point(79, 0), clamp: false, out _) ||
            !presenter.TryMapTouch(new Point(80, 0), clamp: false, out Point first) || first != Point.Empty ||
            !presenter.TryMapTouch(new Point(400, 400), clamp: true, out Point last) || last != new Point(239, 319))
        {
            throw new InvalidOperationException("Touch mapping must ignore black bars and clamp drags to the guest edge.");
        }

        Array.Clear(pixels);
        pixels[changedRow * width * 2] = 0xe0;
        pixels[changedRow * width * 2 + 1] = 0x07;
        if (!presenter.PresentRows(pixels, width, height, changedRow, changedRow + 1) ||
            ReadPresentedPixel(presenter, 0, 0) != unchecked((int)0xff0000ff) ||
            ReadPresentedPixel(presenter, 0, changedRow - 1) != unchecked((int)0xffff0000) ||
            ReadPresentedPixel(presenter, 0, changedRow) != unchecked((int)0xff00ff00) ||
            ReadPresentedPixel(presenter, 0, changedRow + 1) != unchecked((int)0xff0000ff) ||
            presenter.PresentRows(pixels, width, height, height, height))
        {
            throw new InvalidOperationException("A partial update must change only the supplied rows.");
        }

        Array.Fill(pixels, (byte)0xff);
        if (!presenter.PresentRows(pixels, height, width, width, width) ||
            presenter.FrameBitmap?.Size != new Size(height, width) ||
            ReadPresentedPixel(presenter, 0, 0) != -1 || ReadPresentedPixel(presenter, height - 1, width - 1) != -1)
        {
            throw new InvalidOperationException("Rotating the frame must initialize every pixel of the new bitmap.");
        }

        Measure("full frames", () => presenter.Present(pixels, width, height));
        Measure("single rows", () => presenter.PresentRows(pixels, width, height, changedRow, changedRow + 1));
        static void Measure(string label, Action present)
        {
            present();
            var times = new double[7];
            for (int run = 0; run < times.Length; run++)
            {
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 256; i++) present();
                times[run] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            Array.Sort(times);
            Console.WriteLine($"Presenting 256 {label} at 240x320: median {times[3]:F3} ms");
        }
    }

    private static void VerifyRgb565Colors()
    {
        using var presenter = new DesktopFrame();
        const int width = 257, height = 256, colorCount = 65536;
        var pixels = new byte[width * height * 2];
        for (int color = 0; color < colorCount; color++)
        {
            pixels[color * 2] = (byte)(color & 255);
            pixels[color * 2 + 1] = (byte)(color >> 8);
        }
        presenter.Present(pixels, width, height);
        for (int color = 0; color < colorCount; color++)
        {
            int red = (color >> 11) * 255 / 31;
            int green = ((color >> 5) & 63) * 255 / 63;
            int blue = (color & 31) * 255 / 31;
            int expected = unchecked((int)0xff000000) | (red << 16) | (green << 8) | blue;
            int actual = ReadPresentedPixel(presenter, color % width, color / width);
            if (actual != expected)
                throw new InvalidOperationException($"RGB565 color {color:x4} produced {actual:x8}, expected {expected:x8}.");
        }

        int tail = (width - 1) * 2;
        pixels[tail] = 0x1f;
        pixels[tail + 1] = 0;
        presenter.PresentRows(pixels, width, height, 0, 1);
        if (ReadPresentedPixel(presenter, width - 1, 0) != unchecked((int)0xff0000ff))
            throw new InvalidOperationException("An odd-width row must update its last pixel.");
        Console.WriteLine($"RGB565 colors and odd row passed. Hardware vectors: {System.Numerics.Vector.IsHardwareAccelerated}");
    }

    private static void VerifyStorage(string fixture, string resourceRoot, string root)
    {
        var package = new Package(File.ReadAllBytes(fixture));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "system"));
        File.WriteAllBytes(Path.Combine(root, "data", "config.bin"), new byte[] { 0, 0, 0, 0, 9 });
        string shadow = Path.Combine(root, "system", "gb16.uc2");
        File.WriteAllBytes(shadow, new byte[] { 0xfa, 0xfa, 0xfa, 0xfa });
        var storage = SystemFileStorage.Instance;
        var resources = PlayerFiles.LoadMythroad(resourceRoot, storage);
        using (var session = new RuntimeSession(package, root, mythroad: resources, storage: storage))
        {
            uint Call(int slot, params uint[] args) => session.Cpu.Invoke(0xf0000000u + (uint)slot * 4, args);
            uint Name(string path) => session.Memory.CopyIn(Encoding.ASCII.GetBytes(path + "\0"));
            foreach (string denied in new[] { "data/../escape", "//host/path", "C:/escape", "data/CON" })
            {
                if (Call(40, Name(denied), 12) != 0)
                {
                    throw new InvalidOperationException("Guest opened a denied path: " + denied);
                }
            }
            uint mountedName = Name("system/gb16.uc2");
            uint mounted = Call(40, mountedName, 1);
            uint mountedBytes = session.Memory.Allocate(4);
            if (mounted == 0 || Call(44, mounted, mountedBytes, 4) != 4 ||
                !session.Memory.Read(mountedBytes, 4)
                    .SequenceEqual(File.ReadAllBytes(Path.Combine(resourceRoot, "system", "gb16.uc2")).Take(4)) ||
                Call(41, mounted) != 0 || Call(40, mountedName, 12) != 0 ||
                !File.ReadAllBytes(shadow).SequenceEqual(new byte[] { 0xfa, 0xfa, 0xfa, 0xfa }))
            {
                throw new InvalidOperationException("Mounted resources lost precedence or write protection.");
            }
            uint handle = Call(40, Name(@"data\\config.bin"), 28);
            uint data = session.Memory.CopyIn(new byte[] { 1, 0, 255, 42 });
            if (handle == 0 || Call(43, handle, data, 4) != 4 || Call(41, handle) != 0)
            {
                throw new InvalidOperationException("Native file mode did not write and close.");
            }
        }
        using var fresh = new RuntimeSession(package, root, storage: storage);
        uint filename = fresh.Memory.CopyIn(Encoding.ASCII.GetBytes("data/config.bin\0"));
        uint reopened = fresh.Cpu.Invoke(0xf0000000u + 40 * 4, new[] { filename, 1u });
        uint result = fresh.Memory.Allocate(5);
        if (reopened == 0 || fresh.Cpu.Invoke(0xf0000000u + 44 * 4, new[] { reopened, result, 5u }) != 5 ||
            !fresh.Memory.Read(result, 5).SequenceEqual(new byte[] { 1, 0, 255, 42, 9 }))
        {
            throw new InvalidOperationException("Native file did not reopen with the persisted bytes.");
        }
    }
}
