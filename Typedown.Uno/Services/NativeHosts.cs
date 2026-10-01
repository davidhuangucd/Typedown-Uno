using System.Collections;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Typedown.Uno.Services;

/// <summary>
/// Puts a window's editor back when Uno has taken it out for another window.
///
/// Uno keeps the hosts of native elements (the X window each web view lives in) in one list for the whole app, and
/// when the native elements a window draws change - a second window opening, with its own editor - it goes through
/// that list with the order of this one window only: the hosts of every other window are not in it, and Uno
/// detaches them (the web view's window is unmapped and moved to the root). Nothing attaches them again, since a
/// window's own order has not changed: the first window was left without an editor - keys and clicks went nowhere.
/// So each window checks that its web view is attached when its own compositor draws it, and attaches it again
/// through Uno's own code for that (Uno 6.7; if the members change, it is logged once and left alone).
/// </summary>
public static class NativeHosts
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static bool reported;

    /// <summary>Attaches the web view again if its window draws it but Uno has detached it; true if it did.</summary>
    public static bool Repair(FrameworkElement? webView)
    {
        if (!OperatingSystem.IsLinux() || webView == null || !webView.IsLoaded) return false;
        try
        {
            var presenter = FindHost(webView);
            if (presenter == null) return false;
            var type = typeof(ContentPresenter);
            var attachedField = type.GetField("_nativeElementAttached", Flags);
            var attach = type.GetMethod("AttachNativeElement", Flags, Type.EmptyTypes);
            var arrange = type.GetMethod("ArrangeNativeElement", Flags, Type.EmptyTypes);
            var visual = typeof(UIElement).GetProperty("Visual", Flags)?.GetValue(presenter);
            var target = visual?.GetType().GetProperty("CompositionTarget", Flags)?.GetValue(visual);
            var order = target?.GetType().GetField("_nativeVisualsInZOrder", Flags)?.GetValue(target) as IList;
            if (attachedField == null || attach == null || arrange == null || order == null)
            {
                if (!reported) Log.Write("native hosts: Uno's members are not as expected, the editor is not re-attached");
                reported = true;
                return false;
            }
            if (attachedField.GetValue(presenter) is true || !order.Contains(visual)) return false;
            attach.Invoke(presenter, null);
            arrange.Invoke(presenter, null);
            Log.Write("native hosts: the editor's web view was detached by another window, attached again");
            return true;
        }
        catch (Exception ex)
        {
            if (!reported) Log.Error("native hosts", ex);
            reported = true;
            return false;
        }
    }

    // The presenter in the web view's template whose content is the native window.
    private static ContentPresenter? FindHost(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ContentPresenter presenter && presenter.Content?.GetType().Name == "X11NativeWindow") return presenter;
            if (FindHost(child) is { } found) return found;
        }
        return null;
    }
}
