using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Notipet.Hotkeys;
using Windows.System;
using Windows.UI.Core;

namespace Notipet.Windows;

// Click, then press a key combination to set a hotkey. Backspace or Delete
// clears it; Esc puts the text back. Copied from quota-scope.
//
// While it has focus the host lifts the registered hotkey (Focused/Unfocused):
// otherwise pressing the current keys here would open Recent instead of
// arriving as a key press.
internal sealed class HotkeyCaptureBox : TextBox
{
    private string _committedText = "";

    public event Action<HotkeyDefinition>? Captured;
    public event Action? Cleared;

    public HotkeyCaptureBox()
    {
        IsReadOnly = true;
        IsSpellCheckEnabled = false;
        MinWidth = 180;
        PlaceholderText = Loc.T("Click, then press keys", "클릭한 뒤 키를 누르세요");
        KeyDown += OnCaptureKeyDown;
    }

    public void SetBinding(string text)
    {
        _committedText = text;
        Text = text;
    }

    private void OnCaptureKeyDown(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        var key = e.Key;

        if (key == VirtualKey.Escape)
        {
            Text = _committedText;
            return;
        }
        if (key is VirtualKey.Back or VirtualKey.Delete)
        {
            _committedText = "";
            Text = "";
            Cleared?.Invoke();
            return;
        }
        if (IsModifierKey(key)) return;   // wait for the key itself

        var definition = new HotkeyDefinition(CurrentModifiers(), (uint)key);
        if (!definition.IsUsable) return;   // needs Ctrl, Alt or Win, and a supported key

        _committedText = definition.Format();
        Text = _committedText;
        Captured?.Invoke(definition);
    }

    private static bool IsModifierKey(VirtualKey key) => key is
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu or
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift or
        VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static uint CurrentModifiers()
    {
        uint modifiers = 0;
        if (IsDown(VirtualKey.Control)) modifiers |= HotkeyDefinition.ModControl;
        if (IsDown(VirtualKey.Menu)) modifiers |= HotkeyDefinition.ModAlt;
        if (IsDown(VirtualKey.Shift)) modifiers |= HotkeyDefinition.ModShift;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= HotkeyDefinition.ModWin;
        return modifiers;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
