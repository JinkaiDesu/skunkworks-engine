using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
namespace Skunkworks.System
{
    // Fixed character space. Visibility does not imply ownership of input.
    internal static class TerminalView
    {
        private const string ObjectName = "skunkworks-system-terminal", SubscriptionName = "system.terminal";
        private const double RepeatDelay = .40, RepeatInterval = .035;
        private static GameObject gameObject;
        private static Canvas canvas;
        private static GraphicRaycaster raycaster;
        private static Material material;
        private static TerminalFontCache font;
        private static Font sourceFont;
        private static Font systemFallbackFont;
        private static string fontPath; private static string[] fallbackPaths = new string[0];
        private static readonly List<TerminalGridGraphic> strips = new List<TerminalGridGraphic>();
        private static InputProgram input;
        private static IDisposable subscription;
        private static TerminalTransport transport;
        private static TerminalKeys keys;
        private static string keyFile, notice = "", composition = "";
        private static bool shown, keyboardOwned, selecting;
        private static int selectionStart = -1, selectionEnd = -1, columns, rows;
        private static float scale = 1;
        private static double blinkEpoch;
        private static InputSample repeatKey;
        private static TerminalKeys.Binding repeatBinding;
        private static double nextRepeat;
        private static TerminalTransport.Snapshot previous;
        private static string previousFooter;
        private static bool previousBlink;
        private static Rect bounds = new Rect(0, 0, 1, 1);
        private static int rowOffset;
        private static float contentTop;
        private static readonly Dictionary<InputActionMap, List<InputAction>> gameplayMaps = new Dictionary<InputActionMap, List<InputAction>>();
        private static bool gameplaySuspended;
        private static int startupFailureRevision;
        private static Rect PixelBounds => new Rect(bounds.x * Screen.width, bounds.y * Screen.height, bounds.width * Screen.width, bounds.height * Screen.height);
        public static bool Visible => shown;
        public static bool InputOwned => shown && keyboardOwned;
        public static int Columns => columns;
        public static int Rows => rows;
        public static float CellWidth => font == null ? 0 : font.Width;
        public static float CellHeight => font == null ? 0 : font.Height;
        public static int PointSize => font == null ? 0 : font.PointSize;
        public static string Start(TerminalTransport session)
        {
            Stop();
            startupFailureRevision = 0;
            var old = GameObject.Find(ObjectName);
            if (old != null) { old.SetActive(false); UnityEngine.Object.Destroy(old); }
            var source = InputEvents.Current;
            input = source != null && source.Capturing ? source.Program : null;
            keyFile = Path.Combine(SystemPaths.UserRoot, "Terminal", "keys.config");
            try { keys = new TerminalKeys(keyFile); }
            catch (Exception e) { keys = new TerminalKeys(null); notice = "Keymap invalid; defaults active: " + e.Message; }
            try
            {
                sourceFont = Resources.Load<Font>("RedditMono-Regular");
                if (sourceFont == null) throw new InvalidOperationException("Reddit Mono TTF must be baked into Resources.");
                systemFallbackFont = Resources.Load<Font>("CascadiaCode-Regular");
                if (systemFallbackFont == null) throw new InvalidOperationException("The system monospace fallback TTF must be baked into Resources.");
                var shader = Resources.Load<Shader>("TerminalAtlas");
                if (shader == null || !shader.isSupported) throw new InvalidOperationException("Terminal atlas shader unavailable.");
                material = new Material(shader);
                gameObject = new GameObject(ObjectName, typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
                UnityEngine.Object.DontDestroyOnLoad(gameObject);
                canvas = gameObject.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 10000; canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1;
                // The atlas shader converts ANSI sRGB colors once; prevent uGUI's prior conversion.
                canvas.vertexColorAlwaysGammaSpace = true;
                raycaster = gameObject.GetComponent<GraphicRaycaster>();
                gameObject.AddComponent<TerminalViewPump>();
                transport = session;
                if (input != null) subscription = input.Observe(SubscriptionName, OnInput);
                Application.quitting += OnQuit;
                shown = keyboardOwned = true; blinkEpoch = Time.unscaledTimeAsDouble;
                Tick();
                return "terminal uGUI overlay online; TTF atlas; no camera";
            }
            catch { Stop(); throw; }
        }
        public static string Status() => gameObject == null ? "terminal stopped" :
            "terminal " + (shown ? "visible" : "hidden") + " keyboardFocus=" + AcceptsKeyboard() +
            " inputOwned=" + InputOwned + " raycast=" + (raycaster != null && raycaster.enabled) +
            " grid=" + columns + "x" + rows + " pointSize=" + PointSize;
        public static string Show() { if (canvas == null) return "terminal stopped"; shown = true; canvas.enabled = true; ApplyOwnership(); return "terminal visible"; }
        public static string Hide() { Blur(); shown = false; if (canvas != null) canvas.enabled = false; return "terminal hidden"; }
        public static string Focus() { Show(); keyboardOwned = true; blinkEpoch = Time.unscaledTimeAsDouble; ApplyOwnership(); return "terminal focused"; }
        public static string Blur() { keyboardOwned = false; selecting = false; composition = ""; ClearRepeat(); ApplyOwnership(); return "terminal input transparent"; }
        public static string Toggle() => shown ? Hide() : Focus();
        public static string SetBounds(float x, float y, float width, float height)
        {
            if (float.IsNaN(x + y + width + height) || float.IsInfinity(x + y + width + height) ||
                x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1 || y + height > 1)
                throw new ArgumentOutOfRangeException(nameof(width), "Bounds must fit the normalized screen rectangle.");
            bounds = new Rect(x, y, width, height);
            if (canvas != null)
            {
                // The screen-space canvas itself remains full screen; its strips form the panel.
                foreach (var strip in strips) SetStripBounds(strip.GetComponent<RectTransform>());
            }
            previous = null;
            Tick();
            return "terminal bounds=" + bounds;
        }
        private static void SetStripBounds(RectTransform rt)
        {
            rt.anchorMin = new Vector2(bounds.x, bounds.y); rt.anchorMax = new Vector2(bounds.xMax, bounds.yMax);
            rt.offsetMin = rt.offsetMax = Vector2.zero; rt.pivot = new Vector2(0, 1);
        }
        public static string RegisterGameplayMap(InputActionMap map)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (!gameplayMaps.ContainsKey(map)) gameplayMaps.Add(map, new List<InputAction>());
            SuspendGameplayMaps();
            return "terminal focus registered: " + map.name;
        }
        public static string UnregisterGameplayMap(InputActionMap map)
        {
            if (map != null && gameplayMaps.TryGetValue(map, out var enabled))
            {
                foreach (var action in enabled) action.Enable();
                gameplayMaps.Remove(map);
            }
            return "terminal focus unregistered";
        }
        private static void SuspendGameplayMaps()
        {
            foreach (var pair in gameplayMaps)
            {
                if (AcceptsKeyboard())
                {
                    foreach (var action in pair.Key.actions)
                        if (action.enabled) { if (!pair.Value.Contains(action)) pair.Value.Add(action); action.Disable(); }
                }
                else { foreach (var action in pair.Value) action.Enable(); pair.Value.Clear(); }
            }
            gameplaySuspended = AcceptsKeyboard();
        }
        private static void ApplyOwnership()
        {
            if (raycaster != null) raycaster.enabled = shown;
            foreach (var strip in strips) strip.raycastTarget = shown && strip.Atlas == 0 && strip.FirstRow == 0;
            SuspendGameplayMaps();
        }
        public static string SetScale(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < .5f || value > 4) throw new ArgumentOutOfRangeException(nameof(value));
            scale = Mathf.Round(value * 4) / 4; previous = null; Tick(); return "terminal scale=" + scale;
        }
        public static string SetFontFile(string path)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("Terminal TTF not found", path);
            var candidate = CreateFont(null, path, TargetPointSize());
            SwapFont(candidate); fontPath = path; return "terminal font=" + path;
        }
        public static string ResetFont() { var candidate = CreateFont(sourceFont, null, TargetPointSize()); SwapFont(candidate); fontPath = null; return "terminal font=Reddit Mono"; }
        private static TerminalFontCache CreateFont(Font source, string path, int size)
        {
            var candidate = new TerminalFontCache(source, path, size);
            try
            {
                foreach (var fallback in fallbackPaths) candidate.AddFallback(fallback);
                candidate.AddFallback(systemFallbackFont, true);
                return candidate;
            }
            catch { candidate.Dispose(); throw; }
        }
        public static string SetFallbackFontFiles(string[] paths)
        {
            if (paths == null || paths.Length > 4) throw new ArgumentException("At most four fallback TTF files are accepted.");
            var absolute = new string[paths.Length];
            for (int i = 0; i < paths.Length; i++) { absolute[i] = Path.GetFullPath(paths[i]); if (!File.Exists(absolute[i])) throw new FileNotFoundException("Fallback TTF not found", absolute[i]); }
            var old = fallbackPaths; fallbackPaths = absolute;
            try { SwapFont(CreateFont(sourceFont, fontPath, TargetPointSize())); }
            catch { fallbackPaths = old; throw; }
            return "terminal fallback fonts=" + paths.Length;
        }
        private static int TargetPointSize()
        {
            float dpi = Screen.dpi > 0 ? Mathf.Clamp(Screen.dpi / 96, 1, 2) : 1;
            return Mathf.Clamp(Mathf.RoundToInt(16 * scale * dpi / 2) * 2, 8, 128);
        }
        private static void SwapFont(TerminalFontCache candidate)
        {
            var old = font; font = candidate; previous = null;
            foreach (var strip in strips) { strip.FontCache = font; strip.canvasRenderer.Clear(); }
            old?.Dispose();
        }
        public static string RefreshNow() { Tick(); return "terminal refreshed"; }
        public static string ReloadBindings() { keys = new TerminalKeys(keyFile); ClearRepeat(); return notice = "Bindings reloaded"; }
        public static string Stop()
        {
            Application.quitting -= OnQuit; subscription?.Dispose(); subscription = null;
            transport = null; ClearRepeat(); var old = gameObject; gameObject = null; canvas = null; raycaster = null;
            if (old != null) { old.SetActive(false); UnityEngine.Object.Destroy(old); }
            strips.Clear(); font?.Dispose(); font = null;
            if (material != null) UnityEngine.Object.Destroy(material); material = null;
            sourceFont = systemFallbackFont = null; fontPath = null; fallbackPaths = new string[0]; input = null; keys = null; previous = null;
            shown = keyboardOwned = selecting = false; selectionStart = selectionEnd = -1;
            SuspendGameplayMaps(); gameplayMaps.Clear(); bounds = new Rect(0, 0, 1, 1); rowOffset = 0;
            columns = rows = 0; notice = composition = "";
            return "terminal stopped";
        }
        private static void OnQuit() { Stop(); }
        private static bool AcceptsKeyboard() => InputOwned && input != null && input.Capturing && input.Focused;
        private static int CellAt(float x, float y)
        {
            if (font == null || columns == 0) return -1;
            var panel = PixelBounds;
            int col = Mathf.Clamp((int)((x - panel.x - 12) / font.Width), 0, columns - 1);
            int row = Mathf.Clamp((int)((panel.yMax - y - contentTop) / font.Height) - rowOffset, 0, rows - 1);
            return row * columns + col;
        }
        private static string SelectedText()
        {
            var snapshot = transport.Latest;
            if (selectionStart < 0 || selectionEnd < 0) return snapshot.Screen;
            var text = new StringBuilder(); int low = Math.Min(selectionStart, selectionEnd), high = Math.Max(selectionStart, selectionEnd);
            for (int i = low; i <= Math.Min(high, snapshot.Cells.Length - 1); i++)
            {
                if (i != low && i % snapshot.Columns == 0) text.Append('\n');
                if (!snapshot.Cells[i].Continuation) text.Append(snapshot.Cells[i].Text);
            }
            return text.ToString();
        }
        private static void OnInput(InputSample sample)
        {
            if (gameObject == null) return;
            if (sample.Kind == InputKind.Focus || sample.Kind == InputKind.Reset)
            {
                ClearRepeat(); composition = "";
                ApplyOwnership();
                return;
            }
            if (sample.Kind == InputKind.KeyUp)
            {
                if (repeatKey != null && sample.Device == repeatKey.Device && sample.Control == repeatKey.Control)
                    ClearRepeat();
                return;
            }

            if (sample.Kind == InputKind.PointerDown && shown && sample.Control == "left")
            {
                if (!PixelBounds.Contains(new Vector2(sample.X, sample.Y))) { Blur(); return; }
                Focus();
                if (TerminalHost.State == ShellState.Exited || TerminalHost.State == ShellState.Failed || TerminalHost.State == ShellState.Stopped)
                { TerminalHost.Restart(); return; }
                selecting = true; selectionStart = selectionEnd = CellAt(sample.X, sample.Y); previous = null; return;
            }
            if (sample.Kind == InputKind.PointerMove && selecting)
            { selectionEnd = CellAt(sample.X, sample.Y); previous = null; return; }
            if (sample.Kind == InputKind.PointerUp) { selecting = false; return; }
            // Function keys belong to foreground programs, not global console toggles.
            TerminalKeys.Binding binding = keys.Resolve(sample);
            if (!AcceptsKeyboard()) return;
            if (binding != null && binding.Action == "blur") { Blur(); return; }
            try
            {
                if (sample.Kind == InputKind.Composition)
                {
                    composition = sample.Text; // pre-edit text is NOT sent to the PTY
                    ClearRepeat();
                }
                else if (sample.Kind == InputKind.Text)
                {
                    string text = TerminalKeys.CommittedText(sample);
                    if (text.Length != 0)
                    {
                        selectionStart = selectionEnd = -1; previous = null;
                        composition = "";
                        transport.Send(text);
                        notice = "";
                        blinkEpoch = Time.unscaledTimeAsDouble;
                    }
                }
                else if (sample.Kind == InputKind.KeyDown && binding != null && composition.Length == 0)
                {
                    ClearRepeat();
                    Execute(binding);
                    if (binding.Repeat)
                    {
                        repeatKey = sample; repeatBinding = binding;
                        nextRepeat = Time.unscaledTimeAsDouble + RepeatDelay;
                    }
                }
            }
            catch (Exception failure)
            {
                ClearRepeat();
                notice = failure.GetBaseException().Message;
                Debug.LogException(failure);
            }
        }

        private static void Execute(TerminalKeys.Binding binding)
        {
            switch (binding.Action)
            {
                case "copy":
                    GUIUtility.systemCopyBuffer = SelectedText();
                    notice = "Copied terminal selection/screen";
                    return;
                case "paste":
                    string paste = GUIUtility.systemCopyBuffer ?? "";
                    if (paste.IndexOfAny(new[] { '\u001b', '\0' }) >= 0)
                    {
                        notice = "Clipboard contains unsupported terminal control characters";
                        return;
                    }
                    paste = paste.Replace("\r\n", "\n").Replace('\r', '\n');
                    transport.Send(transport.Latest.BracketedPaste ? "\u001b[200~" + paste + "\u001b[201~" : paste.Replace('\n', '\r'));
                    break;
                case "quit":
                    Application.Quit(); return;
                case "restart":
                    TerminalHost.Restart(); return;
                case "reload":
                    ReloadBindings(); return;
                default:
                    string bytes = binding.Text;
                    if (transport.Latest.ApplicationCursor && (binding.Action == "up" || binding.Action == "down" || binding.Action == "left" || binding.Action == "right" || binding.Action == "home" || binding.Action == "end"))
                        bytes = bytes.Replace("\u001b[", "\u001bO");
                    transport.Send(bytes); break;
            }
            notice = "";
            blinkEpoch = Time.unscaledTimeAsDouble;
        }

        private static void Tick()
        {
            if (gameObject == null || transport == null) return;
            if (transport.StartupFailureRevision != startupFailureRevision)
            {
                startupFailureRevision = transport.StartupFailureRevision;
                Focus(); // Recovery even if startup program hid the overlay before failing.
            }
            bool focused = AcceptsKeyboard();
            if (focused != gameplaySuspended) ApplyOwnership();
            if (TerminalHost.State == ShellState.Exited || TerminalHost.State == ShellState.Failed)
            { if (!shown) Show(); }
            if (!focused) ClearRepeat();
            if (repeatKey != null)
            {
                if (!input.IsHeld(repeatKey.Device, repeatKey.Control)) ClearRepeat();
                else
                {
                    int burst = 0;
                    double now = Time.unscaledTimeAsDouble;
                    while (repeatBinding != null && now >= nextRepeat && burst++ < 4)
                    {
                        try { Execute(repeatBinding); }
                        catch (Exception failure) { notice = failure.Message; ClearRepeat(); }
                        nextRepeat += RepeatInterval;
                    }
                    if (repeatBinding != null && nextRepeat < now) nextRepeat = now + RepeatInterval;
                }
            }


            int size = TargetPointSize();
            if (font == null || font.PointSize != size) SwapFont(CreateFont(sourceFont, fontPath, size));
            var panel = PixelBounds;
            int newColumns = Mathf.Clamp((int)((panel.width - 24) / font.Width), 1, 300);
            int newRows = Mathf.Clamp((int)((panel.height - 24 - font.Height) / font.Height), 1, 120);
            contentTop = panel.height - font.Height - 12 - newRows * font.Height;
            if (newColumns != columns || newRows != rows)
            {
                columns = newColumns; rows = newRows; selectionStart = selectionEnd = -1;
                transport.RequestSize(columns, rows); previous = null;
            }
            var snapshot = transport.Latest;
            string banner = "";
            string footer = TerminalHost.Diagnostic.Length != 0 ? TerminalHost.Diagnostic :
                snapshot.Error.Length != 0 ? snapshot.Error : notice.Length != 0 ? notice :
                composition.Length != 0 ? "IME: " + composition : "cancel = ctrl+c | paste = ctrl+shift+v | ctrl+q = quit";
            if (TerminalHost.State == ShellState.Exited || TerminalHost.State == ShellState.Failed || TerminalHost.State == ShellState.Stopped)
                footer += " | click terminal to restart shell";
            bool blink = snapshot.CursorVisible && TerminalHost.State == ShellState.Running && focused && (Time.unscaledTimeAsDouble - blinkEpoch) % 1 < .55;
            if (!ReferenceEquals(previous, snapshot) || footer != previousFooter)
            {
                font.Add(snapshot.Screen + banner + footer);
                previous = snapshot; previousFooter = footer;
                RebuildStrips(snapshot, banner, footer, blink);
            }
            else if (blink != previousBlink || selecting)
                foreach (var strip in strips) { strip.CursorShown = blink; strip.SelectionStart = selectionStart; strip.SelectionEnd = selectionEnd; strip.Refresh(); }
            previousBlink = blink;
        }
        private static void RebuildStrips(TerminalTransport.Snapshot snapshot, string banner, string footer, bool blink)
        {
            int lastContent = snapshot.Y;
            for (int i = snapshot.Cells.Length - 1; i >= 0; --i)
                if (!string.IsNullOrWhiteSpace(snapshot.Cells[i].Text) || snapshot.Cells[i].Style.VisibleBackground != 0)
                { lastContent = Math.Max(lastContent, i / snapshot.Columns); break; }
            rowOffset = snapshot.Alternate || snapshot.ApplicationCursor ? 0 : Math.Max(0, rows - lastContent - 1);
            int count = Math.Max(1, (snapshot.Rows + 7) / 8) * font.Pages.Count;
            while (strips.Count > count) { int last = strips.Count - 1; UnityEngine.Object.Destroy(strips[last].gameObject); strips.RemoveAt(last); }
            while (strips.Count < count)
            {
                var go = new GameObject("terminal-grid-strip", typeof(RectTransform), typeof(TerminalGridGraphic));
                go.transform.SetParent(gameObject.transform, false);
                SetStripBounds(go.GetComponent<RectTransform>());
                var graphic = go.GetComponent<TerminalGridGraphic>(); graphic.material = material; strips.Add(graphic);
            }
            int index = 0;
            for (int atlas = 0; atlas < font.Pages.Count; atlas++)
                for (int row = 0; row < Math.Max(snapshot.Rows, 1); row += 8)
                {
                    var strip = strips[index++]; strip.FontCache = font; strip.Snapshot = snapshot; strip.Atlas = atlas;
                    strip.FirstRow = row; strip.LastRow = row + 8; strip.Left = 12; strip.Top = contentTop; strip.RowOffset = rowOffset;
                    strip.Banner = banner; strip.Footer = footer; strip.CursorShown = blink;
                    strip.SelectionStart = selectionStart; strip.SelectionEnd = selectionEnd; strip.Refresh();
                }
            ApplyOwnership();
        }
        private static void ClearRepeat() { repeatKey = null; repeatBinding = null; }
    }
}
