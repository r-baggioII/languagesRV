using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
#if UNITY_EDITOR
using UnityEngine.InputSystem;
#endif

// Controller-operated world-space settings. Each visual key is also an XRI
// physics interactable, so the existing controller Grip/selection rays work
// without a touch screen, mouse, EventSystem or platform keyboard.
public sealed class VrSettingsPanel : MonoBehaviour
{
    private enum EditField { Url, Pin }

    [SerializeField] private RemyVoiceController remyVoiceController;

    private GameObject panel;
    private TMP_Text urlDisplay;
    private TMP_Text pinDisplay;
    private TMP_Text providerDisplay;
    private TMP_Text statusDisplay;
    private string draftUrl;
    private string draftPin = string.Empty;
    private EditField editing = EditField.Url;
    private bool upperCase;
    private bool symbols;
    private bool pairing;
    private int observedSessionRevision;
    private CancellationTokenSource pairingCancellation;
    private readonly List<TMP_Text> keyLabels = new List<TMP_Text>();
    private int lastButtonFrame = -1;
#if UNITY_EDITOR
    private readonly Dictionary<Collider, Action> editorMouseButtons = new Dictionary<Collider, Action>();
    private Keyboard editorKeyboard;
#endif

    private static readonly string[] LetterRows = {
        "1234567890", "qwertyuiop", "asdfghjkl", "zxcvbnm", ".:/-_+=@"
    };
    private static readonly string[] SymbolRows = {
        "~!@#$%^&*(", ")_+-=[]{}|", "\\;:'\",<>?", "/`~!@#$", "%^&*()_+"
    };

    private static readonly Color Background = new Color(0.025f, 0.07f, 0.08f, 0.96f);
    private static readonly Color KeyColor = new Color(0.08f, 0.25f, 0.27f, 1f);
    private static readonly Color HoverColor = new Color(0.17f, 0.51f, 0.52f, 1f);

    private void Start()
    {
        Camera xrCamera = Camera.main;
        if (xrCamera == null)
        {
            Debug.LogError("[VR Settings] Active XR Main Camera not found.", this);
            return;
        }

        if (remyVoiceController == null)
            remyVoiceController = FindFirstObjectByType<RemyVoiceController>();

        draftUrl = LanguageBackend.BaseUrl ?? "http://";
        editing = LanguageBackend.BaseUrl == null ? EditField.Url : EditField.Pin;
        Build(xrCamera.transform);
        panel.SetActive(!LanguageBackend.IsConfigured);
        observedSessionRevision = LanguageBackend.SessionRevision;
        Refresh();
#if UNITY_EDITOR
        editorKeyboard = Keyboard.current;
        if (editorKeyboard != null) editorKeyboard.onTextInput += OnEditorTextInput;
#endif
    }

    private void Update()
    {
#if UNITY_EDITOR
        HandleEditorInput();
#endif
        // Service 401 responses and local expiry can arrive during café play.
        if (panel == null) return;
        bool configured = LanguageBackend.IsConfigured;
        if (observedSessionRevision == LanguageBackend.SessionRevision) return;
        observedSessionRevision = LanguageBackend.SessionRevision;
        if (!configured)
        {
            panel.SetActive(true);
            if (statusDisplay != null)
                statusDisplay.text = LanguageBackend.SessionIssue ?? "Enter a new notebook PIN.";
        }
    }

    private void OnDestroy()
    {
#if UNITY_EDITOR
        if (editorKeyboard != null) editorKeyboard.onTextInput -= OnEditorTextInput;
#endif
        pairingCancellation?.Cancel();
        pairingCancellation?.Dispose();
    }

#if UNITY_EDITOR
    // Editor convenience for testing the same physical XR buttons when the
    // simulator disables mouse input in XRUIInputModule. Excluded from APKs.
    private void OnEditorTextInput(char character)
    {
        if (panel != null && panel.activeInHierarchy && editing == EditField.Pin &&
            character >= '0' && character <= '9')
            Append(character);
    }

    private void HandleEditorInput()
    {
        if (panel == null) return;
        if (panel.activeInHierarchy && editorKeyboard != null)
        {
            if (editorKeyboard.backspaceKey.wasPressedThisFrame) Backspace();
            if (editorKeyboard.enterKey.wasPressedThisFrame ||
                editorKeyboard.numpadEnterKey.wasPressedThisFrame)
                _ = ConnectAsync();
        }

        Mouse mouse = Mouse.current;
        Camera camera = Camera.main;
        if (mouse == null || camera == null || !mouse.leftButton.wasPressedThisFrame) return;
        Vector2 point = mouse.position.ReadValue();
        if (point.x < 0 || point.y < 0 || point.x >= Screen.width || point.y >= Screen.height) return;
        Physics.SyncTransforms();
        RaycastHit[] hits = Physics.RaycastAll(camera.ScreenPointToRay(point), 10f);
        float nearest = float.PositiveInfinity;
        Action clicked = null;
        foreach (RaycastHit hit in hits)
        {
            if (hit.distance >= nearest || !hit.collider.gameObject.activeInHierarchy ||
                !editorMouseButtons.TryGetValue(hit.collider, out Action action)) continue;
            nearest = hit.distance;
            clicked = action;
        }
        if (clicked != null) ActivateButton(clicked);
    }
#endif

    private void ActivateButton(Action selected)
    {
        if (lastButtonFrame == Time.frameCount) return;
        lastButtonFrame = Time.frameCount;
        selected();
    }

    private void Build(Transform view)
    {
        var canvasObject = new GameObject("VR Settings Canvas", typeof(RectTransform), typeof(Canvas));
        canvasObject.transform.SetParent(view, false);
        canvasObject.transform.localPosition = new Vector3(0f, -0.04f, 1.35f);
        canvasObject.transform.localRotation = Quaternion.identity;
        canvasObject.transform.localScale = Vector3.one * 0.001f;
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 500;
        RectTransform canvasRect = (RectTransform)canvasObject.transform;
        canvasRect.sizeDelta = new Vector2(1100f, 820f);

        panel = CreateRect("Panel", canvasRect, Vector2.zero, new Vector2(1100f, 820f)).gameObject;
        Image background = panel.AddComponent<Image>();
        background.color = Background;
        background.raycastTarget = false;
        RectTransform root = panel.GetComponent<RectTransform>();

        Text(root, "BACKEND & VOICE SETTINGS", new Vector2(0, 370), new Vector2(1000, 55), 38);
        Text(root, "Point with either controller and squeeze Grip to select", new Vector2(0, 325),
            new Vector2(1000, 35), 22);
        Button(root, "URL", new Vector2(-425, 270), new Vector2(135, 60), () => Select(EditField.Url));
        urlDisplay = Text(root, string.Empty, new Vector2(70, 270), new Vector2(840, 60), 27);
        Button(root, "PIN", new Vector2(-425, 205), new Vector2(135, 60), () => Select(EditField.Pin));
        pinDisplay = Text(root, string.Empty, new Vector2(70, 205), new Vector2(840, 60), 27);

        Text(root, "Remy Voice Provider", new Vector2(-325, 133), new Vector2(335, 45), 27);
        Button(root, "Kokoro - Local TTS", new Vector2(50, 133), new Vector2(260, 52),
            () => SetVoice(TtsProvider.Kokoro));
        Button(root, "Cartesia - Cloud TTS", new Vector2(348, 133), new Vector2(275, 52),
            () => SetVoice(TtsProvider.Cartesia));
        providerDisplay = Text(root, string.Empty, new Vector2(0, 83), new Vector2(900, 38), 22);
        statusDisplay = Text(root, string.Empty, new Vector2(0, 31), new Vector2(1000, 40), 23);

        float[] widths = { 84, 84, 92, 111, 120 };
        for (int row = 0; row < LetterRows.Length; row++)
        {
            string keys = LetterRows[row];
            float width = widths[row];
            float start = -(keys.Length - 1) * (width + 5f) * 0.5f;
            for (int col = 0; col < keys.Length; col++)
            {
                int keyRow = row;
                int keyCol = col;
                keyLabels.Add(Button(root, keys[col].ToString(),
                    new Vector2(start + col * (width + 5f), -50 - row * 64),
                    new Vector2(width, 55), () => Append(KeyAt(keyRow, keyCol))));
            }
        }
        Button(root, "SHIFT", new Vector2(-454, -365), new Vector2(120, 57), ToggleCase);
        Button(root, "SYMBOLS", new Vector2(-315, -365), new Vector2(130, 57), ToggleSymbols);
        Button(root, "SPACE", new Vector2(-145, -365), new Vector2(145, 57), () => Append(' '));
        Button(root, "BACKSPACE", new Vector2(32, -365), new Vector2(170, 57), Backspace);
        Button(root, "CLEAR", new Vector2(185, -365), new Vector2(110, 57), ClearField);
        Button(root, "CONNECT / PAIR", new Vector2(329, -365), new Vector2(180, 57), () => _ = ConnectAsync());
        Button(root, "CLOSE", new Vector2(470, -365), new Vector2(95, 57), Close);

        // A small physical settings tab stays available during the café visit.
        var tab = CreateRect("Open Settings", canvasRect, new Vector2(470, -460), new Vector2(220, 56));
        Button(tab, "SETTINGS", Vector2.zero, new Vector2(220, 56), Open);
    }

    private void Open()
    {
        draftUrl = LanguageBackend.BaseUrl ?? draftUrl;
        panel.SetActive(true);
        Refresh();
    }

    private void Close()
    {
        pairingCancellation?.Cancel();
        panel.SetActive(false);
    }

    private void Select(EditField field) { editing = field; Refresh(); }
    private void ToggleCase() { upperCase = !upperCase; Refresh(); }
    private void ToggleSymbols() { symbols = !symbols; Refresh(); }

    private char KeyAt(int row, int col)
    {
        char value = (symbols ? SymbolRows : LetterRows)[row][col];
        return !symbols && upperCase ? char.ToUpperInvariant(value) : value;
    }

    private void Append(char value)
    {
        if (editing == EditField.Url && draftUrl.Length < 120)
        {
            draftUrl += value;
            InvalidateIfUrlEdited();
        }
        else if (editing == EditField.Pin && value >= '0' && value <= '9' && draftPin.Length < 6)
            draftPin += value;
        Refresh();
    }

    private void Backspace()
    {
        if (editing == EditField.Url && draftUrl.Length > 0)
        {
            draftUrl = draftUrl.Substring(0, draftUrl.Length - 1);
            InvalidateIfUrlEdited();
        }
        else if (editing == EditField.Pin && draftPin.Length > 0)
            draftPin = draftPin.Substring(0, draftPin.Length - 1);
        Refresh();
    }

    private void ClearField()
    {
        if (editing == EditField.Url)
        {
            draftUrl = string.Empty;
            InvalidateIfUrlEdited();
        }
        else draftPin = string.Empty;
        Refresh();
    }

    private void InvalidateIfUrlEdited()
    {
        pairingCancellation?.Cancel();
        if (!LanguageBackend.IsConfigured) return;
        if (!LanguageBackend.TryNormalizeUrl(draftUrl, out string normalized, out _) ||
            !string.Equals(normalized, LanguageBackend.BaseUrl, StringComparison.OrdinalIgnoreCase))
            LanguageBackend.ClearSessionToken("Backend address changed. Enter a new PIN to pair.");
    }

    private void SetVoice(TtsProvider provider)
    {
        if (remyVoiceController != null)
            remyVoiceController.SetProvider(provider);
        Refresh();
        statusDisplay.text = "Voice choice saved. It applies to subsequent responses.";
    }

    private async Task ConnectAsync()
    {
        if (pairing) return;
        string pin = draftPin;
        draftPin = string.Empty; // Never keep a used or failed PIN on the visible panel.
        RefreshFields();
        if (!LanguageBackend.TrySetBaseUrl(draftUrl, out string error))
        {
            statusDisplay.text = error;
            return;
        }
        if (LanguageBackend.IsConfigured)
            LanguageBackend.ClearSessionToken("Re-pairing with the notebook.");

        pairing = true;
        pairingCancellation = new CancellationTokenSource();
        statusDisplay.text = "Pairing with notebook...";
        try
        {
            LanguageBackend.PairResult result =
                await LanguageBackend.PairAsync(pin, pairingCancellation.Token);
            if (statusDisplay != null)
                statusDisplay.text = result.Message;
            observedSessionRevision = LanguageBackend.SessionRevision;
        }
        catch (Exception)
        {
            if (statusDisplay != null)
                statusDisplay.text = "Pairing failed. Check the notebook and LAN.";
        }
        finally
        {
            pairing = false;
            pairingCancellation?.Dispose();
            pairingCancellation = null;
            RefreshFields();
        }
    }

    private void Refresh()
    {
        int index = 0;
        for (int row = 0; row < LetterRows.Length; row++)
            for (int col = 0; col < LetterRows[row].Length; col++)
                keyLabels[index++].text = KeyAt(row, col).ToString();
        RefreshFields();
        if (providerDisplay != null)
            providerDisplay.text = remyVoiceController == null
                ? "Remy voice controller unavailable"
                : "Selected: " + remyVoiceController.Provider;
    }

    private void RefreshFields()
    {
        if (urlDisplay != null)
            urlDisplay.text = (editing == EditField.Url ? "> " : "  ") + draftUrl;
        if (pinDisplay != null)
            pinDisplay.text = (editing == EditField.Pin ? "> " : "  ") +
                (draftPin.Length == 0 ? "(six-digit notebook PIN)" :
                    new string('*', draftPin.Length)) + "  " + draftPin.Length + "/6";
    }

    private static RectTransform CreateRect(string name, RectTransform parent, Vector2 position, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.SetParent(parent, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        return rect;
    }

    private static TMP_Text Text(RectTransform parent, string value, Vector2 position,
        Vector2 size, float fontSize)
    {
        RectTransform rect = CreateRect("Text", parent, position, size);
        var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = fontSize;
        text.color = Color.white;
        text.alignment = TextAlignmentOptions.Center;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.raycastTarget = false;
        return text;
    }

    private TMP_Text Button(RectTransform parent, string label, Vector2 position,
        Vector2 size, Action selected)
    {
        RectTransform rect = CreateRect(label, parent, position, size);
        Image face = rect.gameObject.AddComponent<Image>();
        face.color = KeyColor;
        face.raycastTarget = false;
        BoxCollider hitBox = rect.gameObject.AddComponent<BoxCollider>();
        hitBox.size = new Vector3(size.x, size.y, 20f);
        var interactable = rect.gameObject.AddComponent<XRSimpleInteractable>();
        interactable.hoverEntered.AddListener(_ => face.color = HoverColor);
        interactable.hoverExited.AddListener(_ => face.color = KeyColor);
        interactable.selectEntered.AddListener(_ => ActivateButton(selected));
#if UNITY_EDITOR
        editorMouseButtons.Add(hitBox, selected);
#endif
        return Text(rect, label, Vector2.zero, size, label.Length > 10 ? 19 : 25);
    }
}
