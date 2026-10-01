using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Typedown.Uno.Services;

/// <summary>
/// Escape closes a dialog, as Cancel would - what a ContentDialog does on Windows but not in Uno, where the dialog
/// stayed up and every key typed afterwards went into it. A key the dialog's own fields use (a shortcut box that
/// clears on Escape, an open drop-down) is handled there first and does not close the dialog.
/// </summary>
public static class DialogKeys
{
    public static ContentDialog CloseOnEscape(ContentDialog dialog)
    {
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Escape || e.Handled) return;
            e.Handled = true;
            dialog.Hide();
        };
        return dialog;
    }
}
