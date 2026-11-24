#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CSharpMrp;
using CSharpMrp.Unity;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public sealed class CSharpMrpSample : MonoBehaviour
{
    private const int TouchDown = 2;
    private const int TouchUp = 3;
    private const int TouchMove = 12;
    private static readonly (KeyCode Key, int Guest)[] Keyboard = Enum.GetValues(typeof(KeyCode))
        .Cast<KeyCode>().Distinct().Select(key => (Key: key, Guest: UnityPlayerIO.GuestKey(key)))
        .Where(binding => binding.Guest >= 0).ToArray();

    [SerializeField] private string startupGame = "";
    private readonly UnityFrame display = new UnityFrame();
    private CSharpMrpView view = null!;
    private string mythroadRoot = "";
    private string path = "";
    private string status = "Enter the path to a local MRP file.";
    private PlayerSessionWorker? worker;
    private MrpSoundDecoder? soundDecoder;
    private UnitySoundOutput? soundOutput;
    private SessionInput? input;
    private PhoneKeys? keys;
    private byte[]? pixels;
    private long version = -1;
    private RuntimeSession.NativeEditWindow? editRequest;
    private uint? editHandle;
    private string editText = "";
    private bool editPending;
    private int? screenPointer;
    private Vector2Int touchPosition;
    private (Vector2Int Position, int Width, int Height)? pendingTouchRelease;
    private bool keyboardActive;
    private bool applicationPaused;
    private bool busy;
    private bool closed;
#if UNITY_WEBGL && !UNITY_EDITOR
    private const bool SingleThreaded = true;
    private const string OpenPrompt = "Choose a local MRP file.";
    private CSharpMrpWebFiles web = null!;
#else
    private const bool SingleThreaded = false;
    private const string OpenPrompt = "Enter the path to a local MRP file.";
#endif

    private bool CanSendInput => !busy && !applicationPaused && editHandle == null &&
        !view.PathPanel.gameObject.activeSelf && worker?.State is { Started: true, Finished: false };

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddPlayer()
    {
        if (Camera.main == null)
        {
            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.12f, 0.12f, 0.12f);
            camera.cullingMask = 0;
        }
        if (FindObjectOfType<CSharpMrpSample>() == null)
        {
            new GameObject("CSharpMrp Player").AddComponent<CSharpMrpSample>();
        }
    }

    private void Start()
    {
        Application.targetFrameRate = 60;
#if UNITY_WEBGL && !UNITY_EDITOR
        mythroadRoot = Path.Combine(Application.temporaryCachePath, "mythroad");
        var bridge = new GameObject("CSharpMrp Web Files");
        bridge.transform.SetParent(transform, false);
        web = bridge.AddComponent<CSharpMrpWebFiles>();
        path = startupGame;
        status = OpenPrompt;
#else
        mythroadRoot = ResolveMythroadRoot();
        string[] args = Environment.GetCommandLineArgs();
        int file = Array.IndexOf(args, "--game");
        path = file >= 0 && file + 1 < args.Length ? args[file + 1] : startupGame;
#endif
        if (path.Length != 0 && path == Path.GetFileName(path))
        {
            path = Path.Combine(mythroadRoot, path);
        }
        view = new CSharpMrpView(transform,
            (source, key) =>
            {
                if (CanSendInput) keys?.Press(PointerKeySource(source), key);
            },
            source => keys?.Release(PointerKeySource(source)));
        view.PathInput.SetTextWithoutNotify(path);
        view.PathInput.onValueChanged.AddListener(value => path = value);
#if UNITY_WEBGL && !UNITY_EDITOR
        view.Open.onClick.AddListener(() => _ = OpenGameCore(pickFile: true));
#else
        view.Open.onClick.AddListener(() => ShowPath(!view.PathPanel.gameObject.activeSelf));
#endif
        view.OpenPath.onClick.AddListener(() => _ = OpenGame());
        view.CancelPath.onClick.AddListener(() => ShowPath(false));
        view.Stop.onClick.AddListener(() => _ = StopGame());
        view.Resolution.onValueChanged.AddListener(index =>
        {
            if (view.GetResolution(index) is { } size) _ = ChangeResolution(size);
        });
        view.EditInput.onValueChanged.AddListener(value => editText = value);
        view.ConfirmEdit.onClick.AddListener(() =>
        {
            if (editHandle is uint handle) _ = ChooseEdit(handle, 0);
        });
        view.CancelEdit.onClick.AddListener(() =>
        {
            if (editHandle is uint handle) _ = ChooseEdit(handle, 1);
        });
        var touch = view.Screen.gameObject.AddComponent<EventTrigger>();
        CSharpMrpView.BindPointer(touch, EventTriggerType.InitializePotentialDrag, e => e.useDragThreshold = false);
        CSharpMrpView.BindPointer(touch, EventTriggerType.PointerDown, BeginScreenTouch);
        CSharpMrpView.BindPointer(touch, EventTriggerType.Drag, MoveScreenTouch);
        CSharpMrpView.BindPointer(touch, EventTriggerType.PointerUp, EndScreenTouch);
        _ = OpenGame();
    }

    private void ShowPath(bool show)
    {
        if (show) ReleaseInputs();
        view.PathPanel.gameObject.SetActive(show);
        if (show) view.PathInput.ActivateInputField();
    }

    private static string ResolveMythroadRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("CSHARPMRP_MYTHROAD");
        if (!string.IsNullOrEmpty(configured)) return Path.GetFullPath(configured);
        if (Application.isEditor) return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "mythroad"));

        var appDirectory = Directory.GetParent(Application.dataPath)!;
        for (DirectoryInfo? directory = appDirectory; directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "mythroad");
            if (Directory.Exists(candidate)) return candidate;
        }
        return Path.Combine(appDirectory.FullName, "mythroad");
    }

    private Task OpenGame() => OpenGameCore(pickFile: false);

    private async Task OpenGameCore(bool pickFile)
    {
        if (busy || (!pickFile && string.IsNullOrWhiteSpace(path)))
        {
            return;
        }
        busy = true;
        ShowPath(false);
        ReleaseInputs();
        try
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            using var picked = pickFile ? await web.PickAsync() : null;
            if (closed || (pickFile && picked == null)) return;
            string selected = picked?.Path ?? path;
            string originalName = picked?.Name ?? Path.GetFileName(selected);
            string root = UnityPlayerIO.DefaultSaveRoot;
            const bool useMythroadForSaves = false;
            var storage = UnityPlayerIO.Storage;
            status = "Loading " + originalName;
            var resources = await web.PrepareMythroadAsync(mythroadRoot);
            if (closed) return;
            var prepared = (bytes: UnityPlayerIO.OpenPackage(selected), resources);
            await CloseSession();
#else
            await CloseSession();
            if (closed) return;
            string selected = path.Trim().Trim('"');
            string originalName = Path.GetFileName(selected);
            string? separateSaveRoot = Environment.GetEnvironmentVariable("CSHARPMRP_SAVE_ROOT");
            bool useMythroadForSaves = string.IsNullOrEmpty(separateSaveRoot);
            string root = useMythroadForSaves ? mythroadRoot : separateSaveRoot!;
            var storage = UnityPlayerIO.Storage;
            var prepared = await Task.Run(() =>
            {
                var bytes = UnityPlayerIO.OpenPackage(selected);
                var resources = PlayerFiles.LoadMythroad(mythroadRoot, storage,
                    Environment.GetEnvironmentVariable("CSHARPMRP_MYTHROAD_FALLBACK"), mountFiles: !useMythroadForSaves);
                return (bytes, resources);
            });
#endif
            if (closed)
            {
                return;
            }
#if UNITY_WEBGL && !UNITY_EDITOR
            var encoding = (Encoding)new I18N.CJK.CP54936().Clone();
            encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
            encoding.DecoderFallback = DecoderFallback.ExceptionFallback;
#else
            var encoding = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
#endif
            if (soundDecoder == null)
            {
                soundDecoder = await UnitySoundBank.PrepareAsync();
                if (closed) return;
            }
            var sessionAudio = new UnitySoundOutput(transform, soundDecoder);
            soundOutput = sessionAudio;
            bool hostNetwork = UnityPlayerIO.SupportsHostNetwork;
            worker = new PlayerSessionWorker(token => new PlayerSessionHost(root, prepared.resources.CreateFont(),
                encoding, prepared.resources, soundOutput: sessionAudio,
                targetFps: 30, trackModuleExecution: false, cancellation: token,
                storage: storage, hostNetwork: hostNetwork, useRootAsSaveDirectory: useMythroadForSaves),
                PlayerFiles.DescribePackage(originalName), prepared.bytes, singleThreaded: SingleThreaded);
            input = new SessionInput(worker);
            keys = new PhoneKeys(input.TryKey);
            status = "Starting " + originalName;
        }
        catch (Exception error)
        {
            if (worker == null) ReleaseAudio();
            status = error.Message;
            Debug.LogException(error);
        }
        finally
        {
            busy = false;
        }
    }

    private async Task StopGame()
    {
        if (busy)
        {
            return;
        }
        busy = true;
        try
        {
            await CloseSession();
            status = OpenPrompt;
        }
        catch (Exception error)
        {
            status = error.Message;
            Debug.LogException(error);
        }
        finally
        {
            busy = false;
        }
    }

    private async Task CloseSession()
    {
        var current = worker;
        if (current == null) return;
        ReleaseInputs();
        keys = null;
        input = null;
        worker = null;
        pendingTouchRelease = null;
        editRequest = null;
        editHandle = null;
        view.SetEditVisible(false);
        view.BindFrame(null);
        view.SetResolution(0, 0);
        display.Dispose();
        pixels = null;
        version = -1;
        current.Dispose();
        ReleaseAudio();
        await current.Completion;
#if UNITY_WEBGL && !UNITY_EDITOR
        await web.FlushAsync();
#endif
    }

    private void ReleaseAudio()
    {
        soundOutput?.Release();
        soundOutput = null;
    }

    private void Update()
    {
        worker?.Pump();
        soundOutput?.Pump();
        UpdateSession();
        view.Status.text = status;
        view.Open.interactable = !busy;
        view.Stop.interactable = !busy && worker != null;
        bool canSendInput = CanSendInput;
        view.Resolution.interactable = canSendInput;
        view.SetKeypadEnabled(canSendInput);
        PollKeyboard(canSendInput);
    }

    private void UpdateSession()
    {
        var current = worker;
        if (current == null)
        {
            return;
        }
        if (current.State.Finished)
        {
            status = current.State.Failure ?? "Game finished";
            if (keys != null)
            {
                ReleaseInputs();
                keys = null;
                pendingTouchRelease = null;
                editRequest = null;
                editHandle = null;
                view.SetEditVisible(false);
            }
            return;
        }
        keys?.RetryReleases();
        RetryTouchRelease();
        editRequest = current.State.Edit;
        if (editHandle != editRequest?.Handle)
        {
            ReleaseInputs();
            editHandle = editRequest?.Handle;
            editText = editRequest?.Text ?? "";
            editPending = false;
            view.SetEditVisible(editRequest != null);
            if (editRequest is { } request)
            {
                ShowPath(false);
                view.EditTitle.text = request.Title;
                view.EditInput.contentType = request.Type == 2
                    ? InputField.ContentType.Password : InputField.ContentType.Standard;
                view.EditInput.characterLimit = request.MaximumLength;
                view.EditInput.SetTextWithoutNotify(editText);
                SetEditEnabled(true);
                view.EditInput.ActivateInputField();
            }
        }
        if (current.TryCopyChangedRows(version, ref pixels, out var state, out int firstRow, out int endRow) &&
            state != null && pixels != null)
        {
            bool resized = display.NeedsResize(state.Width, state.Height);
            if (resized) ReleaseScreenTouch();
            display.PresentChangedRows(pixels, state.Width, state.Height, firstRow, endRow);
            version = state.FrameVersion;
            if (resized)
            {
                view.BindFrame(display.Texture);
                view.SetResolution(state.Width, state.Height);
                status = $"{state.Width} × {state.Height}";
            }
        }
    }

    private void PollKeyboard(bool canSendInput)
    {
        var selected = EventSystem.current?.currentSelectedGameObject;
        if (!canSendInput || selected != null && selected.transform.IsChildOf(view.Resolution.transform))
        {
            if (keyboardActive) keys?.ReleaseAll();
            keyboardActive = false;
            return;
        }

        bool anyKey = Input.anyKey;
        if (!anyKey && !keyboardActive) return;
        keyboardActive = anyKey;
        foreach (var (key, guest) in Keyboard)
        {
            if (Input.GetKeyDown(key)) keys?.Press((int)key, guest);
            if (Input.GetKeyUp(key)) keys?.Release((int)key);
        }
    }

    // StandaloneInputModule uses -1 for the left mouse button and nonnegative touch IDs.
    private static int PointerKeySource(int pointerId) => int.MinValue + pointerId + 1;

    private void BeginScreenTouch(PointerEventData e)
    {
        if (e.button != PointerEventData.InputButton.Left || !CanSendInput || screenPointer != null ||
            !RetryTouchRelease() || !TryMapTouch(e, clamp: false, out var point) || !SendTouch(TouchDown, point))
            return;

        screenPointer = e.pointerId;
        touchPosition = point;
    }

    private void MoveScreenTouch(PointerEventData e)
    {
        if (screenPointer == e.pointerId && TryMapTouch(e, clamp: true, out var point) &&
            point != touchPosition && SendTouch(TouchMove, point))
            touchPosition = point;
    }

    private void EndScreenTouch(PointerEventData e)
    {
        if (screenPointer != e.pointerId) return;
        if (TryMapTouch(e, clamp: true, out var point)) touchPosition = point;
        ReleaseScreenTouch();
    }

    private bool TryMapTouch(PointerEventData e, bool clamp, out Vector2Int point)
    {
        point = default;
        var screen = view.Screen;
        if (screen.texture == null || !RectTransformUtility.ScreenPointToLocalPointInRectangle(
                screen.rectTransform, e.position, e.pressEventCamera, out var local))
            return false;

        var bounds = screen.rectTransform.rect;
        if (bounds.width <= 0 || bounds.height <= 0 || (!clamp && !bounds.Contains(local))) return false;
        float x = (local.x - bounds.xMin) / bounds.width;
        float y = (bounds.yMax - local.y) / bounds.height;
        int width = screen.texture.width;
        int height = screen.texture.height;
        point = new Vector2Int(Mathf.Clamp(Mathf.FloorToInt(x * width), 0, width - 1),
            Mathf.Clamp(Mathf.FloorToInt(y * height), 0, height - 1));
        return true;
    }

    private bool SendTouch(int type, Vector2Int point) =>
        display.Texture is { } texture && input?.TryTouchMapped(type, point.x, point.y, texture.width, texture.height) == true;

    private void ReleaseScreenTouch()
    {
        if (screenPointer == null) return;
        screenPointer = null;
        if (display.Texture is { } texture)
        {
            pendingTouchRelease = (touchPosition, texture.width, texture.height);
            RetryTouchRelease();
        }
    }

    private bool RetryTouchRelease()
    {
        if (pendingTouchRelease is { } release)
        {
            if (input?.TryTouchMapped(TouchUp, release.Position.x, release.Position.y, release.Width, release.Height) != true)
                return false;
            pendingTouchRelease = null;
        }
        return true;
    }

    private void ReleaseInputs()
    {
        ReleaseScreenTouch();
        keys?.ReleaseAll();
        keyboardActive = false;
    }

    private async Task ChangeResolution(MrpScreenResolution size)
    {
        var current = worker;
        if (busy || current?.State is not { Started: true, Finished: false }) return;

        busy = true;
        try
        {
            ReleaseInputs();
            if (!RetryTouchRelease())
                throw new InvalidOperationException("Try again after the current touch is released.");
            await current.SetResolutionAsync(size.Width, size.Height);
        }
        catch (Exception error)
        {
            if (display.Texture is { } texture) view.SetResolution(texture.width, texture.height);
            status = error.Message;
        }
        finally
        {
            busy = false;
        }
    }

    private void SetEditEnabled(bool enabled)
    {
        view.EditInput.interactable = enabled;
        view.ConfirmEdit.interactable = enabled;
        view.CancelEdit.interactable = enabled;
    }

    private async Task ChooseEdit(uint handle, int choice)
    {
        var owner = input;
        if (owner == null || editPending)
        {
            return;
        }
        editPending = true;
        SetEditEnabled(false);
        try
        {
            if (!await owner.SubmitEdit(handle, editText, choice) && input == owner && editHandle == handle)
            {
                editPending = false;
                SetEditEnabled(true);
                status = "The game rejected this text.";
            }
        }
        catch (Exception error)
        {
            if (input == owner && editHandle == handle)
            {
                editPending = false;
                SetEditEnabled(true);
                status = error.Message;
            }
        }
    }

    private void OnApplicationFocus(bool focused)
    {
        if (!focused)
        {
            ReleaseInputs();
        }
    }

    private void OnApplicationPause(bool paused)
    {
        applicationPaused = paused;
        if (paused) ReleaseInputs();
        worker?.Enqueue((host, now) => host.Pause(paused, now));
    }

    private void OnDestroy()
    {
        closed = true;
        ReleaseInputs();
        worker?.Dispose();
        ReleaseAudio();
        display.Dispose();
        view?.Release();
    }
}
