using System.Text;

namespace CSharpMrp.Desktop;

internal sealed class PlayerWindow : Form, IMessageFilter
{
    private const string PlayerName = "CSharpMrp";
    private const string DefaultGame = "dsm_gm.mrp";
    private const int TouchDown = 2;
    private const int TouchUp = 3;
    private const int TouchMove = 12;
    private const int MouseSource = -1;
    private const string InputGuide = "支持触摸屏，点击按键按钮，键盘按键输入。\n\n" +
        "方向键 → ↑ ↓ ← →\n主键盘 / 数字键盘 0–9 → 0–9\nF1 → L，F2 → R\n" +
        "Enter / Space → OK\nEsc / Backspace → Back\n- / 小键盘 * → *\n= / 小键盘 + → #\nCall 使用屏幕按键";
    private readonly DesktopFrame screen = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 12, 8) };
    private readonly Label status = new() { Text = "Open an MRP game", AutoSize = true, UseMnemonic = false };
    private readonly Button open = new() { Text = "Open MRP…", AutoSize = true };
    private readonly Button stop = new() { Text = "Close game", AutoSize = true, Enabled = false };
    private readonly ComboBox resolution = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Resolution", Enabled = false
    };
    private readonly TableLayoutPanel editPanel = new()
    {
        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Visible = false
    };
    private readonly Label editTitle = new() { AutoSize = true, UseMnemonic = false };
    private readonly Label inputGuide = new()
    {
        Text = InputGuide, AutoSize = true, UseMnemonic = false, Margin = new Padding(2, 7, 2, 0)
    };
    private readonly TextBox edit = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    private readonly IFileStorage storage = SystemFileStorage.Instance;
    private readonly string saveRoot;
    private readonly string mythroadRoot;
    private readonly string? mythroadFallback;
    private readonly bool useMythroadForSaves;
    private PlayerSessionWorker? worker;
    private SessionInput? input;
    private PhoneKeys? keys;
    private byte[]? frame;
    private long version = -1;
    private int width;
    private int height;
    private uint? editHandle;
    private int? screenPointer;
    private Point touchPosition;
    private (Point Position, int Width, int Height)? pendingTouchRelease;
    private bool busy;
    private bool closing;
    private bool closeRequested;

    public PlayerWindow(string[] args)
    {
        Text = PlayerName;
        ClientSize = new Size(700, 540);
        MinimumSize = new Size(500, 430);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        var assemblyDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        string programDirectory = assemblyDirectory.Name == "dlls"
            ? assemblyDirectory.Parent!.FullName : assemblyDirectory.FullName;
        mythroadRoot = Path.GetFullPath(Argument(args, "--mythroad") ?? EnvironmentSetting("MYTHROAD") ??
            Path.Combine(programDirectory, "mythroad"));
        string? separateSaveRoot = Argument(args, "--save-root") ?? EnvironmentSetting("SAVE_ROOT");
        useMythroadForSaves = string.IsNullOrEmpty(separateSaveRoot);
        saveRoot = useMythroadForSaves ? mythroadRoot : separateSaveRoot!;
        mythroadFallback = Argument(args, "--mythroad-fallback") ?? EnvironmentSetting("MYTHROAD_FALLBACK");
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var resolutionLabel = new Label { Text = "Resolution", AutoSize = true, Anchor = AnchorStyles.Left };
        root.Controls.Add(ButtonRow(open, stop, resolutionLabel, resolution), 0, 0);
        foreach (var size in MrpScreenResolution.SupportedResolutions)
        {
            resolution.Items.Add(size);
        }
        resolution.Enter += (_, _) => keys?.ReleaseAll();
        resolution.SelectionChangeCommitted += async (_, _) =>
        {
            if (resolution.SelectedItem is MrpScreenResolution size)
                await ChangeResolution(size);
        };

        var game = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = 2, RowCount = 1 };
        game.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        const int keypadWidth = 205;
        var keypadColumn = new ColumnStyle(SizeType.Absolute, keypadWidth);
        game.ColumnStyles.Add(keypadColumn);
        void SizeKeypad()
        {
            keypadColumn.Width = LogicalToDeviceUnits(keypadWidth);
            inputGuide.MaximumSize = new Size(LogicalToDeviceUnits(keypadWidth - 18), 0);
        }
        HandleCreated += (_, _) => SizeKeypad();
        DpiChanged += (_, _) => SizeKeypad();
        game.Controls.Add(screen, 0, 0);
        var side = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8), AutoScroll = true };
        var controls = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Margin = Padding.Empty, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 3
        };
        controls.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        controls.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        controls.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        controls.Controls.Add(BuildKeypad(), 0, 0);
        controls.Controls.Add(inputGuide, 0, 1);
        var ok = new Button { Text = "Confirm", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true };
        ok.Click += async (_, _) => await ChooseEdit(0);
        cancel.Click += async (_, _) => await ChooseEdit(1);
        editPanel.ColumnCount = 1;
        editPanel.Controls.Add(editTitle, 0, 0);
        editPanel.Controls.Add(edit, 0, 1);
        editPanel.Controls.Add(ButtonRow(ok, cancel), 0, 2);
        controls.Controls.Add(editPanel, 0, 2);
        side.Controls.Add(controls);
        game.Controls.Add(side, 1, 0);
        root.Controls.Add(game, 0, 1);
        root.Controls.Add(status, 0, 2);
        Controls.Add(root);

        open.Click += async (_, _) => await PickPackage();
        stop.Click += async (_, _) => await StopGame();
        screen.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && BeginScreenTouch(MouseSource, e.Location)) screen.Capture = true;
        };
        screen.MouseMove += (_, e) => MoveScreenTouch(MouseSource, e.Location);
        screen.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) EndScreenTouch(MouseSource, e.Location);
        };
        screen.MouseCaptureChanged += (_, _) =>
        {
            if (screenPointer == MouseSource && !screen.Capture) ReleaseScreenTouch();
        };
        _ = new PointerInput(screen, (phase, id, point) =>
        {
            switch (phase)
            {
                case PointerPhase.Down: BeginScreenTouch(id, point); break;
                case PointerPhase.Move: MoveScreenTouch(id, point); break;
                case PointerPhase.Up: EndScreenTouch(id, point); break;
                case PointerPhase.Lost when screenPointer == id: ReleaseScreenTouch(); break;
            }
        });
        Deactivate += (_, _) =>
        {
            ReleaseScreenTouch();
            keys?.ReleaseAll();
        };
        FormClosing += async (_, e) =>
        {
            if (worker == null && !busy)
            {
                return;
            }
            e.Cancel = true;
            if (closing)
            {
                return;
            }
            if (busy)
            {
                closeRequested = true;
                return;
            }
            closing = true;
            await StopGame();
            BeginInvoke(Close);
        };
        timer.Tick += (_, _) => Poll();
        string requestedGame = Argument(args, "--game") ?? DefaultGame;
        string file = requestedGame == Path.GetFileName(requestedGame)
            ? Path.Combine(mythroadRoot, requestedGame) : Path.GetFullPath(requestedGame);
        Shown += async (_, _) => await OpenGame(Path.GetFileName(file), () => File.OpenRead(file));
    }

    protected override void OnShown(EventArgs e)
    {
        Application.AddMessageFilter(this);
        timer.Start();
        base.OnShown(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true;
            Application.RemoveMessageFilter(this);
            timer.Dispose();
        }
        base.Dispose(disposing);
    }

    private static FlowLayoutPanel ButtonRow(params Control[] buttons)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false };
        row.Controls.AddRange(buttons);
        return row;
    }

    private static string? EnvironmentSetting(string name) =>
        Environment.GetEnvironmentVariable("CSHARPMRP_" + name);

    private static string? Argument(string[] args, string key)
    {
        int index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private bool BeginScreenTouch(int source, Point position)
    {
        if (screenPointer != null || editHandle != null || busy || input == null ||
            !screen.TryMapTouch(position, clamp: false, out var point) ||
            !RetryTouchRelease() || !SendScreenTouch(TouchDown, point))
        {
            return false;
        }
        screenPointer = source;
        touchPosition = point;
        return true;
    }

    private void MoveScreenTouch(int source, Point position)
    {
        if (screenPointer == source && screen.TryMapTouch(position, clamp: true, out var point) && point != touchPosition)
        {
            touchPosition = point;
            SendScreenTouch(TouchMove, point);
        }
    }

    private void EndScreenTouch(int source, Point position)
    {
        if (screenPointer != source) return;
        if (screen.TryMapTouch(position, clamp: true, out var point)) touchPosition = point;
        ReleaseScreenTouch();
    }

    private void ReleaseScreenTouch()
    {
        var pointer = screenPointer;
        if (pointer == null) return;
        screenPointer = null;
        pendingTouchRelease = (touchPosition, width, height);
        RetryTouchRelease();
        if (pointer == MouseSource && screen.Capture) screen.Capture = false;
    }

    private bool RetryTouchRelease()
    {
        if (pendingTouchRelease is { } release)
        {
            if (input?.TryTouchMapped(TouchUp, release.Position.X, release.Position.Y,
                    release.Width, release.Height) != true) return false;
            pendingTouchRelease = null;
        }
        return true;
    }

    private bool SendScreenTouch(int type, Point point) =>
        input?.TryTouchMapped(type, point.X, point.Y, width, height) == true;

    private TableLayoutPanel BuildKeypad()
    {
        var keypad = new TableLayoutPanel
        {
            ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top, Margin = Padding.Empty
        };
        for (int column = 0; column < 3; column++) keypad.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        for (int i = 0; i < PhoneKeys.Layout.Count; i++)
        {
            var (label, key) = PhoneKeys.Layout[i];
            var button = new Button
            {
                Text = label, TabStop = false, Dock = DockStyle.Fill, Height = 44, Margin = new Padding(2),
                UseMnemonic = false
            };
            button.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left && editHandle == null) keys?.Press(MouseSource, key);
            };
            button.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) keys?.Release(MouseSource);
            };
            button.MouseCaptureChanged += (_, _) =>
            {
                if (!button.Capture) keys?.Release(MouseSource);
            };
            _ = new PointerInput(button, (phase, id, _) =>
            {
                int source = -2 - id;
                if (phase == PointerPhase.Down && editHandle == null) keys?.Press(source, key);
                if (phase is PointerPhase.Up or PointerPhase.Lost) keys?.Release(source);
            });
            button.Click += (_, e) =>
            {
                if (editHandle == null && e is not MouseEventArgs)
                {
                    keys?.Press(int.MinValue, key);
                    keys?.Release(int.MinValue);
                }
            };
            keypad.Controls.Add(button, i % 3, i / 3);
        }
        return keypad;
    }

    private static int GuestKey(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => (int)key - (int)Keys.D0,
        >= Keys.NumPad0 and <= Keys.NumPad9 => (int)key - (int)Keys.NumPad0,
        Keys.OemMinus or Keys.Multiply => 10,
        Keys.Oemplus or Keys.Add => 11,
        Keys.Up => 12,
        Keys.Down => 13,
        Keys.Left => 14,
        Keys.Right => 15,
        Keys.F1 => 17,
        Keys.F2 => 18,
        Keys.Enter or Keys.Space => 20,
        Keys.Escape or Keys.Back => 23,
        _ => -1
    };

    // Arrow and Enter messages can be consumed as dialog navigation before KeyDown.
    bool IMessageFilter.PreFilterMessage(ref Message message)
    {
        const int keyDown = 0x0100, keyUp = 0x0101;
        if (message.Msg is not (keyDown or keyUp) || editHandle != null || resolution.Focused || keys == null ||
            GuestKey((Keys)message.WParam) is not (>= 0 and var key) ||
            Control.FromChildHandle(message.HWnd)?.FindForm() != this)
        {
            return false;
        }
        int source = (int)message.WParam;
        if (message.Msg == keyDown) keys.Press(source, key);
        else keys.Release(source);
        return true;
    }

    private async Task PickPackage()
    {
        try
        {
            using var picker = new OpenFileDialog
            {
                Title = "Open an MRP game",
                Filter = "MRP game (*.mrp)|*.mrp",
                RestoreDirectory = true
            };
            if (picker.ShowDialog(this) == DialogResult.OK)
            {
                string file = picker.FileName;
                await OpenGame(Path.GetFileName(file), () => File.OpenRead(file));
            }
        }
        catch (Exception error)
        {
            status.Text = error.Message;
        }
    }

    private async Task OpenGame(string filename, Func<Stream> openPackage)
    {
        if (busy || closing)
        {
            return;
        }
        busy = true;
        open.Enabled = false;
        try
        {
            await CloseSession();
            if (closeRequested) return;
            Text = PlayerName;
            status.Text = "Loading " + filename;
            var prepared = await Task.Run(() =>
            {
                using var source = openPackage();
                byte[] bytes = PlayerFiles.ReadPackage(source);
                var resources = PlayerFiles.LoadMythroad(mythroadRoot, storage, mythroadFallback,
                    mountFiles: !useMythroadForSaves);
                return (bytes, resources);
            });
            var encoding = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            worker = new PlayerSessionWorker(token => new PlayerSessionHost(saveRoot, prepared.resources.CreateFont(),
                encoding, prepared.resources, soundOutput: new DesktopSoundOutput(),
                targetFps: 30, trackModuleExecution: false, cancellation: token,
                storage: storage, useRootAsSaveDirectory: useMythroadForSaves),
                PlayerFiles.DescribePackage(filename), prepared.bytes);
            input = new SessionInput(worker);
            keys = new PhoneKeys(input.TryKey);
            stop.Enabled = true;
            Text = PlayerName + " — " + filename;
        }
        catch (Exception error)
        {
            status.Text = error.Message;
        }
        finally
        {
            FinishOperation();
        }
    }

    private async Task StopGame()
    {
        if (busy)
        {
            return;
        }
        busy = true;
        open.Enabled = false;
        try
        {
            await CloseSession();
            status.Text = "Open an MRP game";
        }
        catch (Exception error)
        {
            status.Text = error.Message;
        }
        finally
        {
            Text = PlayerName;
            FinishOperation();
        }
    }

    private async Task ChangeResolution(MrpScreenResolution size)
    {
        var current = worker;
        if (busy || current?.State is not { Started: true, Finished: false }) return;

        busy = true;
        open.Enabled = false;
        resolution.Enabled = false;
        try
        {
            ReleaseScreenTouch();
            if (!RetryTouchRelease())
                throw new InvalidOperationException("Try again after the current touch is released.");
            keys?.ReleaseAll();
            await current.SetResolutionAsync(size.Width, size.Height);
        }
        catch (Exception error)
        {
            SyncResolutionSelection();
            status.Text = error.Message;
        }
        finally
        {
            FinishOperation();
        }
    }

    private void SyncResolutionSelection()
    {
        resolution.SelectedItem = MrpScreenResolution.SupportedResolutions
            .FirstOrDefault(size => size.Width == width && size.Height == height);
    }

    private async Task CloseSession()
    {
        var current = worker;
        if (current == null) return;
        stop.Enabled = false;
        resolution.Enabled = false;
        resolution.SelectedIndex = -1;
        ReleaseScreenTouch();
        keys?.ReleaseAll();
        worker = null;
        keys = null;
        input = null;
        pendingTouchRelease = null;
        editPanel.Visible = false;
        inputGuide.Visible = true;
        editHandle = null;
        screen.Clear();
        frame = null;
        version = -1;
        width = 0;
        height = 0;
        current.Dispose();
        await current.Completion;
    }

    private void FinishOperation()
    {
        busy = false;
        open.Enabled = true;
        if (closeRequested)
        {
            closeRequested = false;
            BeginInvoke(Close);
        }
    }

    private void Poll()
    {
        var current = worker;
        if (current == null)
        {
            return;
        }
        var state = current.State;
        if (state.Finished)
        {
            stop.Enabled = true;
            resolution.Enabled = false;
            status.Text = state.Failure == null ? "Game finished" : state.Failure + " " + state.ReportPath;
            ReleaseScreenTouch();
            keys?.ReleaseAll();
            keys = null;
            pendingTouchRelease = null;
            editHandle = null;
            editPanel.Visible = false;
            inputGuide.Visible = true;
            return;
        }
        if (!state.Started)
        {
            return;
        }
        keys?.RetryReleases();
        RetryTouchRelease();
        if (current.TryCopyChangedRows(version, ref frame, out var result, out int firstRow, out int endRow) &&
            frame != null && result is { } published)
        {
            if (width != published.Width || height != published.Height) ReleaseScreenTouch();
            screen.PresentRows(frame, published.Width, published.Height, firstRow, endRow);
            version = published.FrameVersion;
            if (width != published.Width || height != published.Height)
            {
                width = published.Width;
                height = published.Height;
                SyncResolutionSelection();
                status.Text = $"{width} × {height}";
            }
        }
        if (state.Edit?.Handle != editHandle)
        {
            ReleaseScreenTouch();
            keys?.ReleaseAll();
            editHandle = state.Edit?.Handle;
            editPanel.Visible = state.Edit != null;
            inputGuide.Visible = state.Edit == null;
            if (state.Edit is { } request)
            {
                editTitle.Text = request.Title;
                edit.MaxLength = request.MaximumLength;
                edit.PasswordChar = request.Type == 2 ? '●' : '\0';
                edit.Text = request.Text;
                editPanel.Enabled = true;
                edit.Focus();
            }
        }
        resolution.Enabled = !busy && editHandle == null;
    }

    private async Task ChooseEdit(int choice)
    {
        var owner = input;
        if (editHandle is not uint handle || owner == null || !editPanel.Enabled)
        {
            return;
        }
        editPanel.Enabled = false;
        try
        {
            if (!await owner.SubmitEdit(handle, edit.Text, choice) &&
                input == owner && editHandle == handle)
            {
                editPanel.Enabled = true;
                status.Text = "The game rejected this text.";
            }
        }
        catch (Exception error)
        {
            if (input == owner && editHandle == handle)
            {
                editPanel.Enabled = true;
                status.Text = error.Message;
            }
        }
    }
}
