using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

// Replace the old InputEvent.cs / OnGUI tap with this file.
// The MonoBehaviour filename must be InputEvents.cs; do not keep two InputEvents classes.
[DisallowMultipleComponent]
public sealed class InputEvents : MonoBehaviour
{
    public static InputEvents Current { get; private set; }
    public InputProgram Program { get; private set; }
    public bool Capturing => Program != null && Program.Capturing;
    public int Count => Program == null ? 0 : Program.Count;

    private readonly Dictionary<int, KeyboardHook> keyboards = new Dictionary<int, KeyboardHook>();
    private bool hooked;

    private sealed class KeyboardHook
    {
        public Keyboard Keyboard;
        public Action<char> Text;
        public Action<IMECompositionString> Composition;
        public readonly InputTextAccumulator Decoder = new InputTextAccumulator();
    }

    private void Awake()
    {
        Program = new InputProgram();
    }

    public void StartCapture()
    {
#if !ENABLE_INPUT_SYSTEM
        throw new InvalidOperationException(
            "Enable Input System Package (New) or Both in Player > Active Input Handling, then rebuild the player.");
#else
        if (hooked) return;
        if (Current != null && Current != this)
            throw new InvalidOperationException("Another InputEvents source is already active.");
        if (Program == null) Program = new InputProgram();
        Current = this;
        hooked = true;
        Program.Capturing = true;
        InputSystem.onEvent += OnInputEvent;
        InputSystem.onDeviceChange += OnDeviceChange;
        foreach (InputDevice device in InputSystem.devices)
            if (device is Keyboard keyboard) HookKeyboard(keyboard);
        Program.Post(new InputSample(InputKind.Focus, Now(), focused: Application.isFocused));
        if (Program.KeyboardCount == 0)
            Debug.LogWarning("[input] Started with no Keyboard device; waiting for device discovery.");
#endif
    }

    public void StopCapture()
    {
        if (!hooked) return;
        hooked = false;
        InputSystem.onEvent -= OnInputEvent;
        InputSystem.onDeviceChange -= OnDeviceChange;
        foreach (KeyboardHook hook in keyboards.Values)
        {
            hook.Keyboard.onTextInput -= hook.Text;
            hook.Keyboard.onIMECompositionChange -= hook.Composition;
        }
        keyboards.Clear();
        Program.KeyboardCount = 0;
        Program.Capturing = false;
        Program.Reset(Now(), "capture stopped");
        if (Current == this) Current = null;
    }

    public string Drain(int maximum) => Program.Drain(maximum);
    public void Clear() => Program.Reset(Now(), "explicit reset");
    public string Describe() => Program.Describe();

    private void OnDisable() { StopCapture(); }
    private void OnDestroy() { StopCapture(); }

    private void OnApplicationFocus(bool focused)
    {
        if (!Capturing) return;
        foreach (KeyboardHook hook in keyboards.Values) hook.Decoder.Clear();
        Program.Reset(Now(), focused ? "focus gained" : "focus lost");
        Program.Post(new InputSample(InputKind.Focus, Now(), focused: focused));
    }

    private void HookKeyboard(Keyboard keyboard)
    {
        if (keyboards.ContainsKey(keyboard.deviceId)) return;
        var hook = new KeyboardHook { Keyboard = keyboard };
        hook.Text = c => OnText(hook, c);
        hook.Composition = text => OnComposition(hook, text);
        keyboard.onTextInput += hook.Text;
        keyboard.onIMECompositionChange += hook.Composition;
        keyboards.Add(keyboard.deviceId, hook);
        Program.KeyboardCount = keyboards.Count;
    }

    private void UnhookKeyboard(int device)
    {
        if (!keyboards.TryGetValue(device, out KeyboardHook hook)) return;
        hook.Keyboard.onTextInput -= hook.Text;
        hook.Keyboard.onIMECompositionChange -= hook.Composition;
        hook.Decoder.Clear();
        keyboards.Remove(device);
        Program.KeyboardCount = keyboards.Count;
    }

    private void OnDeviceChange(InputDevice device, InputDeviceChange change)
    {
        if (!Capturing) return;
        bool reset = change == InputDeviceChange.Removed || change == InputDeviceChange.Disconnected ||
            change == InputDeviceChange.Disabled || change == InputDeviceChange.SoftReset ||
            change == InputDeviceChange.HardReset;
        if (reset)
        {
            foreach (KeyboardHook hook in keyboards.Values) hook.Decoder.Clear();
            Program.Reset(Now(), "device " + device.deviceId + " " + change);
        }
        if (change == InputDeviceChange.Removed || change == InputDeviceChange.Disconnected ||
            change == InputDeviceChange.Disabled)
            UnhookKeyboard(device.deviceId);
        else if (device is Keyboard keyboard)
            HookKeyboard(keyboard);
        Program.Post(new InputSample(InputKind.Device, Now(), device.deviceId,
            control: device.layout, text: change.ToString(), focused: Application.isFocused));
    }

    // Copy values while the event is valid. No InputEventPtr escapes this callback.
    // Observe only: do not set event.handled or change Unity's input settings.
    private void OnInputEvent(InputEventPtr evt, InputDevice device)
    {
        if (!Capturing || !Application.isFocused || device == null) return;
        if (!evt.IsA<StateEvent>() && !evt.IsA<DeltaStateEvent>()) return;

        if (device is Keyboard keyboard)
        {
            InputModifiers modifiers = ReadModifiers(keyboard, evt, true);
            foreach (InputControl control in evt.EnumerateChangedControls(device))
            {
                if (!(control is KeyControl key)) continue;
                if (!key.ReadValueFromEvent(evt, out float value)) continue;
                bool pressed = value >= 0.5f;
                // A raw event can change other state while leaving this key unchanged.
                if (pressed == key.isPressed) continue;
                Program.Post(new InputSample(pressed ? InputKind.KeyDown : InputKind.KeyUp,
                    evt.time, device.deviceId, NormalizeKey(key.name), key.displayName,
                    modifiers: modifiers));
            }
        }
        else if (device is Mouse mouse)
        {
            InputModifiers modifiers = ReadModifiers(Keyboard.current, default(InputEventPtr), false);
            Vector2 position = mouse.position.ReadValue();
            if (mouse.position.ReadValueFromEvent(evt, out Vector2 newPosition)) position = newPosition;
            Vector2 delta = Vector2.zero;
            mouse.delta.ReadValueFromEvent(evt, out delta);
            if (delta != Vector2.zero || position != mouse.position.ReadValue())
                Program.Post(new InputSample(InputKind.PointerMove, evt.time, device.deviceId,
                    modifiers: modifiers, x: position.x, y: position.y, deltaX: delta.x, deltaY: delta.y));
            if (mouse.scroll.ReadValueFromEvent(evt, out Vector2 wheel) && wheel != Vector2.zero)
                Program.Post(new InputSample(InputKind.Scroll, evt.time, device.deviceId,
                    modifiers: modifiers, x: position.x, y: position.y, deltaX: wheel.x, deltaY: wheel.y));
            MouseButton(evt, mouse.leftButton, "left", position, modifiers);
            MouseButton(evt, mouse.rightButton, "right", position, modifiers);
            MouseButton(evt, mouse.middleButton, "middle", position, modifiers);
            MouseButton(evt, mouse.forwardButton, "forward", position, modifiers);
            MouseButton(evt, mouse.backButton, "back", position, modifiers);
        }
        else
        {
            // Generic continuous control facts for gamepads, joysticks, sensors and XR.
            foreach (InputControl control in evt.EnumerateChangedControls(device))
                if (control is AxisControl axis && axis.ReadValueFromEvent(evt, out float value))
                    Program.Post(new InputSample(InputKind.Signal, evt.time, device.deviceId,
                        control.path, control.displayName, x: value));
        }
    }
    private void MouseButton(InputEventPtr evt, ButtonControl button, string name,
        Vector2 position, InputModifiers modifiers)
    {
        if (!button.ReadValueFromEvent(evt, out float value)) return;
        bool pressed = value >= button.pressPointOrDefault;
        if (pressed == button.isPressed) return;
        Program.Post(new InputSample(pressed ? InputKind.PointerDown : InputKind.PointerUp,
            evt.time, button.device.deviceId, name, modifiers: modifiers,
            x: position.x, y: position.y));
    }

    private void OnText(KeyboardHook hook, char character)
    {
        if (!Capturing || !Application.isFocused || character == '\0') return;
        string text = hook.Decoder.Push(character);
        if (text.Length == 0) return;
        Program.Post(new InputSample(InputKind.Text, Now(), hook.Keyboard.deviceId,
            text: text, modifiers: ReadModifiers(hook.Keyboard, default(InputEventPtr), false)));
    }

    private void OnComposition(KeyboardHook hook, IMECompositionString value)
    {
        if (!Capturing || !Application.isFocused) return;
        Program.Post(new InputSample(InputKind.Composition, Now(), hook.Keyboard.deviceId,
            text: value.ToString(), modifiers: ReadModifiers(hook.Keyboard, default(InputEventPtr), false)));
    }

    private static bool Pressed(ButtonControl key, InputEventPtr evt, bool useEvent)
    {
        if (useEvent && key.ReadValueFromEvent(evt, out float value)) return value >= key.pressPointOrDefault;
        return key.isPressed;
    }

    private static InputModifiers ReadModifiers(Keyboard keyboard, InputEventPtr evt, bool useEvent)
    {
        if (keyboard == null) return InputModifiers.None;
        InputModifiers result = InputModifiers.None;
        if (Pressed(keyboard.leftShiftKey, evt, useEvent) || Pressed(keyboard.rightShiftKey, evt, useEvent)) result |= InputModifiers.Shift;
        if (Pressed(keyboard.leftCtrlKey, evt, useEvent) || Pressed(keyboard.rightCtrlKey, evt, useEvent)) result |= InputModifiers.Control;
        if (Pressed(keyboard.leftAltKey, evt, useEvent) || Pressed(keyboard.rightAltKey, evt, useEvent)) result |= InputModifiers.Alt;
        if (Pressed(keyboard.leftMetaKey, evt, useEvent) || Pressed(keyboard.rightMetaKey, evt, useEvent)) result |= InputModifiers.Meta;
        if (Pressed(keyboard.rightAltKey, evt, useEvent)) result |= InputModifiers.RightAlt;
        return result;
    }

    private static string NormalizeKey(string name)
    {
        string key = name.ToLowerInvariant();
        if (key.StartsWith("digit", StringComparison.Ordinal) && key.Length == 6) return key.Substring(5);
        switch (key)
        {
            case "uparrow": return "up";
            case "downarrow": return "down";
            case "leftarrow": return "left";
            case "rightarrow": return "right";
            case "numpadenter": return "keypad_enter";
            case "pageup": return "page_up";
            case "pagedown": return "page_down";
            case "backquote": return "grave";
            case "leftshift": return "left_shift";
            case "rightshift": return "right_shift";
            case "leftctrl": return "left_ctrl";
            case "rightctrl": return "right_ctrl";
            case "leftalt": return "left_alt";
            case "rightalt": return "right_alt";
            case "leftmeta": return "left_meta";
            case "rightmeta": return "right_meta";
            case "leftbracket": return "left_bracket";
            case "rightbracket": return "right_bracket";
            default: return key;
        }
    }

    private static double Now() => Time.realtimeSinceStartupAsDouble;
}
