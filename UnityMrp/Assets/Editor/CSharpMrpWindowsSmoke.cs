using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using CSharpMrp;
using CSharpMrp.Unity;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public static class CSharpMrpWindowsSmoke
{
    private const string Scene = "Assets/Player.unity";
    private const string RunKey = "CSharpMrpWindowsSmoke";
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void CreateScene()
    {
        if (!File.Exists(Scene))
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, Scene);
        }
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(Scene, true) };
    }

    public static void Run()
    {
        CreateScene();
        EditorSceneManager.OpenScene(Scene);
        if (UnityEngine.Object.FindObjectOfType<CSharpMrpSample>() == null)
            throw new InvalidOperationException("Player.unity is missing its serialized CSharpMrpSample component.");
        SessionState.SetBool(RunKey, true);
        EditorApplication.isPlaying = true;
    }

    [InitializeOnLoadMethod]
    private static void Attach()
    {
        if (SessionState.GetBool(RunKey, false))
        {
            EditorApplication.playModeStateChanged += OnPlay;
        }
    }

    private static async void OnPlay(PlayModeStateChange change)
    {
        if (change != PlayModeStateChange.EnteredPlayMode)
        {
            return;
        }
        SessionState.EraseBool(RunKey);
        EditorApplication.playModeStateChanged -= OnPlay;
        try
        {
            await Verify();
            UnityEngine.Debug.Log("CSHARPMRP WINDOWS SMOKE PASS");
            EditorApplication.Exit(0);
        }
        catch (Exception error)
        {
            UnityEngine.Debug.LogException(error);
            EditorApplication.Exit(1);
        }
    }

    private static async Task Verify()
    {
        string root = Environment.GetEnvironmentVariable("CSHARPMRP_SAVE_ROOT")
            ?? throw new InvalidOperationException("Set CSHARPMRP_SAVE_ROOT to a new acceptance directory.");
        var watch = Stopwatch.StartNew();
        async Task WaitFor(Func<bool> condition)
        {
            while (!condition())
            {
                if (watch.Elapsed.TotalSeconds > 35)
                {
                    throw new TimeoutException("Waiting for the sample player.");
                }
                await Task.Delay(30);
            }
        }
        T Field<T>(CSharpMrpSample sample, string name)
        {
            return (T)typeof(CSharpMrpSample).GetField(name, Fields).GetValue(sample);
        }
        Task Invoke(CSharpMrpSample sample, string name, params object[] args)
        {
            return (Task)typeof(CSharpMrpSample).GetMethod(name, Fields).Invoke(sample, args);
        }
        CSharpMrpSample player = null;
        await WaitFor(() => (player = UnityEngine.Object.FindObjectOfType<CSharpMrpSample>()) != null);
        var display = Field<UnityFrame>(player, "display");
        await WaitFor(() => display.Texture != null);
        if (UnityPlayerIO.GuestKey(KeyCode.Minus) != 10 || UnityPlayerIO.GuestKey(KeyCode.KeypadMultiply) != 10 ||
            UnityPlayerIO.GuestKey(KeyCode.Equals) != 11 || UnityPlayerIO.GuestKey(KeyCode.KeypadPlus) != 11)
            throw new InvalidOperationException("The star and pound keyboard bindings must match the launcher defaults.");
        if (display.Texture.width != 240 || display.Texture.height != 320 ||
            ((Color32)display.Texture.GetPixel(5, 314)).r != 255)
        {
            throw new InvalidOperationException("Unexpected starting canvas.");
        }
        async Task Tap(int key)
        {
            var controls = Field<PhoneKeys>(player, "keys");
            controls.Press(-1, key);
            await Task.Delay(50);
            controls.Release(-1);
            await Task.Delay(60);
        }
        var keypad = player.GetComponentsInChildren<Button>().Single(button => button.name == "#");
        var press = new PointerEventData(EventSystem.current) { pointerId = 12, button = PointerEventData.InputButton.Left };
        ExecuteEvents.Execute(keypad.gameObject, press, ExecuteEvents.pointerDownHandler);
        await Task.Delay(50);
        ExecuteEvents.Execute(keypad.gameObject, press, ExecuteEvents.pointerUpHandler);
        await Task.Delay(60);
        await Tap(UnityPlayerIO.GuestKey(KeyCode.DownArrow));
        await Tap(13);
        await Tap(13);
        await Tap(17);
        await Tap(20);
        await WaitFor(() => ((Color32)display.Texture.GetPixel(5, 314)).r == 16);
        await Tap(17);
        await Tap(13);
        await Tap(13);
        await Tap(13);
        await Tap(20);
        string save = null;
        await WaitFor(() => (save = Directory.EnumerateFiles(root, "*.bmp", SearchOption.AllDirectories)
            .FirstOrDefault(path => new FileInfo(path).Length == 582)) != null);
        byte[] saved = File.ReadAllBytes(save);
        if (saved.Length != 582 || saved[0] != 'B' || saved[1] != 'M' ||
            saved[550] != 0xa2 || saved[551] != 0x10)
        {
            throw new InvalidOperationException("The real game did not persist the expected painted pixel.");
        }
        await Invoke(player, "StopGame");
        await Invoke(player, "OpenGame");
        await WaitFor(() => display.Texture != null);
        await Tap(17);
        await Tap(13);
        await Tap(20);
        await Tap(13);
        await Tap(20);
        await WaitFor(() => ((Color32)display.Texture.GetPixel(5, 314)).r == 16);
        var worker = Field<PlayerSessionWorker>(player, "worker");
        uint handle = await worker.InvokeAsync((owner, _) =>
        {
            var vm = owner.Session;
            uint title = vm.Memory.CopyIn(Encoding.BigEndianUnicode.GetBytes("Edit\0"));
            uint initial = vm.Memory.CopyIn(new byte[2]);
            return vm.Cpu.Invoke(0xf0000000u + 75 * 4, new[] { title, initial, 0u, 8u });
        });
        await WaitFor(() => Field<RuntimeSession.NativeEditWindow>(player, "editRequest") != null);
        player.GetComponentsInChildren<InputField>().Single(field => field.name == "Guest Text").text = "A中";
        player.GetComponentsInChildren<Button>().Single(button => button.name == "Confirm").onClick.Invoke();
        await WaitFor(() => worker.State.Edit?.Submitted == true);
        if (worker.State.Edit.Text != "A中")
        {
            throw new InvalidOperationException("Text did not reach the guest edit buffer.");
        }
        await worker.InvokeAsync((owner, _) => owner.Session.Cpu.Invoke(0xf0000000u + 76 * 4, new[] { handle }));
        await WaitFor(() => Field<RuntimeSession.NativeEditWindow>(player, "editRequest") == null);
        await VerifyTouch(player, worker);
        var resolutions = player.GetComponentInChildren<Dropdown>();
        async Task Resize(int width, int height)
        {
            var size = MrpScreenResolution.SupportedResolutions
                .Single(option => option.Width == width && option.Height == height);
            resolutions.value = resolutions.options.FindIndex(option => option.text == size.ToString());
            await WaitFor(() => display.Texture != null && display.Texture.width == width && display.Texture.height == height);
            if (worker != Field<PlayerSessionWorker>(player, "worker") ||
                worker.State.Width != width || worker.State.Height != height)
                throw new InvalidOperationException("Changing resolution must keep the active MRP session.");
        }
        await Resize(128, 128);
        ScreenCapture.CaptureScreenshot(Path.Combine(root, "unity-resized.png"));
        await Task.Delay(600);
        await Resize(240, 320);
        File.WriteAllBytes(Path.Combine(root, "unity-framebuffer.png"), display.Texture.EncodeToPNG());
        ScreenCapture.CaptureScreenshot(Path.Combine(root, "unity-game.png"));
        await Task.Delay(600);
        await Invoke(player, "StopGame");
    }

    private static async Task VerifyTouch(CSharpMrpSample player, PlayerSessionWorker worker)
    {
        Canvas.ForceUpdateCanvases();
        var screen = player.GetComponentInChildren<RawImage>();
        var rect = screen.rectTransform.rect;
        Vector2 Position(float x, float y) => RectTransformUtility.WorldToScreenPoint(null,
            screen.rectTransform.TransformPoint(new Vector2(rect.xMin + x * rect.width, rect.yMax - y * rect.height)));
        var touch = new PointerEventData(EventSystem.current)
        {
            pointerId = 21, button = PointerEventData.InputButton.Left, position = Position(.25f, .25f)
        };
        var hits = new System.Collections.Generic.List<RaycastResult>();
        EventSystem.current.RaycastAll(touch, hits);
        if (hits.Count == 0 || hits[0].gameObject != screen.gameObject)
            throw new InvalidOperationException("The game image must receive uGUI pointer events.");

        var down = typeof(PlayerSessionHost).GetField("pointerDown", Fields);
        var xField = typeof(PlayerSessionHost).GetField("pointerX", Fields);
        var yField = typeof(PlayerSessionHost).GetField("pointerY", Fields);
        Task<(bool Down, int X, int Y)> State() => worker.InvokeAsync((host, _) =>
            ((bool)down.GetValue(host), (int)xField.GetValue(host), (int)yField.GetValue(host)));

        var outside = new PointerEventData(EventSystem.current)
        {
            pointerId = 22, button = PointerEventData.InputButton.Left, position = Position(-.01f, .25f)
        };
        hits.Clear();
        EventSystem.current.RaycastAll(outside, hits);
        if (hits.Any(hit => hit.gameObject == screen.gameObject))
            throw new InvalidOperationException("The black bars must stay outside the interactive image.");

        ExecuteEvents.Execute(screen.gameObject, touch, ExecuteEvents.pointerDownHandler);
        var pressed = await State();
        if (!pressed.Down || Math.Abs(pressed.X - 60) > 1 || Math.Abs(pressed.Y - 80) > 1)
            throw new InvalidOperationException("The guest must receive image-relative touch coordinates.");

        ExecuteEvents.Execute(screen.gameObject, outside, ExecuteEvents.pointerDownHandler);
        if (await State() != pressed)
            throw new InvalidOperationException("A second pointer must not replace the active guest touch.");

        touch.position = Position(1.2f, 1.2f);
        ExecuteEvents.Execute(screen.gameObject, touch, ExecuteEvents.dragHandler);
        if (await State() != (true, 239, 319))
            throw new InvalidOperationException("Dragging outside the image must clamp to the guest edge.");
        ExecuteEvents.Execute(screen.gameObject, touch, ExecuteEvents.pointerUpHandler);
        if (await State() != (false, 239, 319))
            throw new InvalidOperationException("Releasing outside the image must release the guest touch.");
    }
}
