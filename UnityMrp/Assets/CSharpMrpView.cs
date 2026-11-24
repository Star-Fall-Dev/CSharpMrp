#nullable enable
using System;
using System.Collections.Generic;
using CSharpMrp;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

internal sealed class CSharpMrpView
{
    private const string InputGuide = "支持触摸屏，点击按键按钮，键盘按键输入。\n\n" +
        "方向键 → ↑ ↓ ← →\n主键盘 / 数字键盘 0–9 → 0–9\nF1 → L，F2 → R\n" +
        "Enter / Space → OK\nEsc / Backspace → Back\n- / 小键盘 * → *\n= / 小键盘 + → #\nCall 使用屏幕按键";
    private const float KeyHeight = 44;
    private static readonly Vector2 TopLeft = new Vector2(0, 1);
    private static readonly Vector2 TopRight = Vector2.one;
    private static readonly Color Ink = new Color32(26, 26, 26, 255);
    private static readonly Color Background = new Color32(241, 241, 241, 255);
    private static readonly Color ControlColor = new Color32(250, 250, 250, 255);
    private static readonly DefaultControls.Resources Controls = new DefaultControls.Resources();
    private readonly Font font;
    private readonly AspectRatioFitter aspect;
    private readonly Text guide;
    private readonly Button[] keypad = new Button[PhoneKeys.Layout.Count];
    private bool keypadEnabled = true;
    private int resolutionOffset;

    public RectTransform Root { get; }
    public Button Open { get; }
    public Button Stop { get; }
    public Dropdown Resolution { get; }
    public RawImage Screen { get; }
    public Text Status { get; }
    public Image PathPanel { get; }
    public InputField PathInput { get; }
    public Button OpenPath { get; }
    public Button CancelPath { get; }
    public Image EditPanel { get; }
    public Text EditTitle { get; }
    public InputField EditInput { get; }
    public Button ConfirmEdit { get; }
    public Button CancelEdit { get; }

    public CSharpMrpView(Transform owner, Action<int, int> press, Action<int> release)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        font = Resources.Load<Font>("ResourceHanRoundedSC-Regular");
        if (font == null) throw new InvalidOperationException("The browser UI font is missing.");
#else
        font = Font.CreateDynamicFontFromOSFont(new[]
        {
            "Microsoft YaHei UI", "PingFang SC", "Noto Sans CJK SC", "Noto Sans CJK", "Droid Sans Fallback", "Arial"
        }, 13);
#endif
        var canvasObject = new GameObject("CSharpMrp Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(owner, false);
        canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(700, 540);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        Root = (RectTransform)canvasObject.transform;

        if (EventSystem.current == null)
        {
            var events = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            events.transform.SetParent(owner, false);
            events.GetComponent<EventSystem>().sendNavigationEvents = false;
        }

        var background = Panel("Background", Root, Background);
        Stretch(background.rectTransform, Vector2.zero, Vector2.zero);
        var toolbar = Rect("Toolbar", Root);
        toolbar.anchorMin = TopLeft;
        toolbar.anchorMax = TopRight;
        toolbar.pivot = TopLeft;
        toolbar.offsetMin = new Vector2(14, -44);
        toolbar.offsetMax = new Vector2(-14, -14);
        Open = MakeButton("Open MRP…", toolbar, 0, 0, 96, 28);
        Stop = MakeButton("Close game", toolbar, 102, 0, 92, 28);
        var label = MakeText("Resolution Label", toolbar, "Resolution", 13, TextAnchor.MiddleLeft);
        Place(label.rectTransform, TopLeft, 202, 0, 68, 28);
        Resolution = DefaultControls.CreateDropdown(Controls).GetComponent<Dropdown>();
        Resolution.name = "Resolution";
        Resolution.transform.SetParent(toolbar, false);
        Place((RectTransform)Resolution.transform, TopLeft, 270, 0, 116, 28);
        StyleText(Resolution.gameObject);
        Resolution.captionText.verticalOverflow = VerticalWrapMode.Overflow;
        Resolution.itemText.verticalOverflow = VerticalWrapMode.Overflow;
        Resolution.template.sizeDelta = new Vector2(Resolution.template.sizeDelta.x, 188);
        Resolution.GetComponent<Image>().color = ControlColor;
        Resolution.transform.Find("Arrow").GetComponent<Image>().enabled = false;
        var arrow = MakeText("Dropdown Indicator", Resolution.transform, font.HasCharacter('▾') ? "▾" : "↓",
            14, TextAnchor.MiddleCenter);
        Place(arrow.rectTransform, TopRight, -23, 0, 21, 28);
        SetResolution(0, 0);

        var screenBackground = Panel("Screen Frame", Root, Color.black);
        Stretch(screenBackground.rectTransform, new Vector2(14, 41), new Vector2(-231, -55));
        Screen = Rect("Game Screen", screenBackground.transform).gameObject.AddComponent<RawImage>();
        Stretch(Screen.rectTransform, Vector2.zero, Vector2.zero);
        aspect = Screen.gameObject.AddComponent<AspectRatioFitter>();
        aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        BindFrame(null);

        var keys = Rect("Keypad", Root);
        Place(keys, TopRight, -219, 55, 205, 336);
        for (int index = 0; index < keypad.Length; index++)
        {
            var (caption, key) = PhoneKeys.Layout[index];
            var button = MakeButton(caption, keys, 2 + index % 3 * 68, 2 + index / 3 * 48, 64, KeyHeight);
            keypad[index] = button;
            var trigger = button.gameObject.AddComponent<EventTrigger>();
            BindPointer(trigger, EventTriggerType.PointerDown, e =>
            {
                if (button.interactable && e.button == PointerEventData.InputButton.Left)
                    press(e.pointerId, key);
            });
            BindPointer(trigger, EventTriggerType.PointerUp, e =>
            {
                if (e.button == PointerEventData.InputButton.Left) release(e.pointerId);
            });
        }
        guide = MakeText("Keyboard Guide", Root, InputGuide, 12, TextAnchor.UpperLeft);
        Place(guide.rectTransform, TopRight, -217, 399, 201, 133);
        // Preserve the whole guide, including the final mapping, on the reference canvas.
        guide.resizeTextForBestFit = true;
        guide.resizeTextMinSize = 10;
        guide.resizeTextMaxSize = 12;
#if UNITY_WEBGL && !UNITY_EDITOR
        guide.lineSpacing = 0.9f;
#endif

        Status = MakeText("Status", Root, "", 13, TextAnchor.MiddleLeft);
        Status.rectTransform.anchorMin = Vector2.zero;
        Status.rectTransform.anchorMax = Vector2.right;
        Status.rectTransform.offsetMin = new Vector2(14, 12);
        Status.rectTransform.offsetMax = new Vector2(-231, 36);

        EditPanel = Panel("Guest Edit", Root, Background);
        Place(EditPanel.rectTransform, TopRight, -219, 398, 205, 118);
        EditTitle = MakeText("Guest Edit Title", EditPanel.transform, "", 13, TextAnchor.MiddleLeft);
        Place(EditTitle.rectTransform, TopLeft, 2, 0, 201, 35);
        EditInput = MakeInput("Guest Text", EditPanel.transform, 2, 38, 201);
        ConfirmEdit = MakeButton("Confirm", EditPanel.transform, 2, 72, 96, 28);
        CancelEdit = MakeButton("Cancel", EditPanel.transform, 104, 72, 96, 28);
        EditPanel.gameObject.SetActive(false);

        PathPanel = Panel("Open Game Panel", Root, Background);
        Place(PathPanel.rectTransform, TopLeft, 14, 49, 440, 48);
        PathInput = MakeInput("Package Path", PathPanel.transform, 6, 10, 285);
        ((Text)PathInput.placeholder).text = "Local MRP file path";
        OpenPath = MakeButton("Open", PathPanel.transform, 297, 10, 64, 28);
        CancelPath = MakeButton("Cancel", PathPanel.transform, 367, 10, 66, 28);
        PathPanel.gameObject.SetActive(false);
    }

    public void BindFrame(Texture2D? texture)
    {
        Screen.texture = texture;
        Screen.enabled = texture != null;
        if (texture != null)
            aspect.aspectRatio = (float)texture.width / texture.height;
    }

    public void SetKeypadEnabled(bool enabled)
    {
        if (keypadEnabled == enabled) return;
        keypadEnabled = enabled;
        foreach (var key in keypad) key.interactable = enabled;
    }

    public void SetEditVisible(bool visible)
    {
        guide.gameObject.SetActive(!visible);
        EditPanel.gameObject.SetActive(visible);
    }

    public void SetResolution(int width, int height)
    {
        var options = Resolution.options;
        options.Clear();
        int selected = -1;
        foreach (var size in MrpScreenResolution.SupportedResolutions)
        {
            if (size.Width == width && size.Height == height) selected = options.Count;
            options.Add(new Dropdown.OptionData(size.ToString()));
        }
        resolutionOffset = selected < 0 ? 1 : 0;
        if (resolutionOffset != 0)
        {
            options.Insert(0, new Dropdown.OptionData(width > 0 ? $"{width} × {height}" : "—"));
            selected = 0;
        }
        Resolution.SetValueWithoutNotify(selected);
        Resolution.RefreshShownValue();
    }

    public MrpScreenResolution? GetResolution(int index) =>
        index >= resolutionOffset ? MrpScreenResolution.SupportedResolutions[index - resolutionOffset] : null;

    public void Release()
    {
#if !UNITY_WEBGL || UNITY_EDITOR
        UnityEngine.Object.Destroy(font);
#endif
    }

    public static void BindPointer(EventTrigger trigger, EventTriggerType type, Action<PointerEventData> receive)
    {
        var entry = new EventTrigger.Entry { eventID = type };
        entry.callback.AddListener(data => receive((PointerEventData)data));
        trigger.triggers.Add(entry);
    }

    private static RectTransform Rect(string name, Transform parent)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        return rect;
    }

    private static void Place(RectTransform rect, Vector2 anchor, float x, float y, float width, float height)
    {
        rect.anchorMin = rect.anchorMax = anchor;
        rect.pivot = TopLeft;
        rect.anchoredPosition = new Vector2(x, -y);
        rect.sizeDelta = new Vector2(width, height);
    }

    private static void Stretch(RectTransform rect, Vector2 min, Vector2 max)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = min;
        rect.offsetMax = max;
    }

    private static Image Panel(string name, Transform parent, Color color)
    {
        var image = Rect(name, parent).gameObject.AddComponent<Image>();
        image.color = color;
        return image;
    }

    private Text MakeText(string name, Transform parent, string value, int size, TextAnchor alignment)
    {
        var text = Rect(name, parent).gameObject.AddComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.color = Ink;
        text.alignment = alignment;
        text.supportRichText = false;
        text.raycastTarget = false;
        text.text = value;
        return text;
    }

    private Button MakeButton(string caption, Transform parent, float x, float y, float width, float height)
    {
        var image = Panel(caption, parent, ControlColor);
        Place(image.rectTransform, TopLeft, x, y, width, height);
        var button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        var label = MakeText("Label", image.transform, caption, 13, TextAnchor.MiddleCenter);
        Stretch(label.rectTransform, Vector2.zero, Vector2.zero);
        return button;
    }

    private InputField MakeInput(string name, Transform parent, float x, float y, float width)
    {
        var input = DefaultControls.CreateInputField(Controls).GetComponent<InputField>();
        input.name = name;
        input.transform.SetParent(parent, false);
        Place((RectTransform)input.transform, TopLeft, x, y, width, 28);
        StyleText(input.gameObject);
        return input;
    }

    private void StyleText(GameObject root)
    {
        foreach (var text in root.GetComponentsInChildren<Text>(true))
        {
            text.font = font;
            text.fontSize = 13;
            text.color = Ink;
            text.supportRichText = false;
            text.raycastTarget = false;
        }
    }
}
