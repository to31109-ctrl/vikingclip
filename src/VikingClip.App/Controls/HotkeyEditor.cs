using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VikingClip.Core.Settings;

namespace VikingClip.App.Controls;

/// <summary>Click, press a combination, done. Backspace clears, Esc cancels.</summary>
public sealed class HotkeyEditor : Border
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };
    private Hotkey _value;

    public event Action<Hotkey>? ValueChanged;

    public Hotkey Value
    {
        get => _value;
        set { _value = value; Render(); }
    }

    public HotkeyEditor()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        Height = 32;
        MinWidth = 160;
        Padding = new Thickness(10, 0, 10, 0);
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "Surface2Brush");
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        _text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _text.FontFamily = (FontFamily)Application.Current.FindResource("MonoFont");
        _text.FontSize = 12;
        Child = _text;

        MouseLeftButtonDown += (_, _) => Focus();
        GotKeyboardFocus += (_, _) => { SetResourceReference(BorderBrushProperty, "AccentBrush"); _text.Text = "Press keys…  (Backspace clears, Esc cancels)"; };
        LostKeyboardFocus += (_, _) => { SetResourceReference(BorderBrushProperty, "BorderBrush"); Render(); };
        PreviewKeyDown += OnKey;
        Render();
    }

    private void Render() => _text.Text = _value.IsBound ? _value.ToString() : "Not set";

    private void OnKey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin:
                return; // wait for the real key
            case Key.Escape:
                Keyboard.ClearFocus();
                Render();
                return;
            case Key.Back or Key.Delete:
                Commit(Hotkey.None);
                return;
        }
        var mods = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        Commit(new Hotkey(mods, vk));
    }

    private void Commit(Hotkey hk)
    {
        _value = hk;
        Keyboard.ClearFocus();
        Render();
        ValueChanged?.Invoke(hk);
    }
}
