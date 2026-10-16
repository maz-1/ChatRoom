using System;
using Eto.Drawing;
using Eto.Forms;

namespace ChatRoomTray.ConfigEditor;

/// <summary>Responsive sizing shared by the configuration editor and its dialogs.</summary>
internal static class EditorLayout
{
    public static Scrollable VerticalScroll(Control content, string id)
    {
        var scroll = new Scrollable
        {
            ID = id,
            Content = content,
            Border = BorderType.None,
            ExpandContentWidth = true,
        };
#if WINDOWS_TRAY
        // ExpandContentWidth sets only a minimum in Eto WinForms. Constrain long
        // labels to the viewport so they wrap instead of pushing buttons outside it.
        content.Width = 1;
        var native = (System.Windows.Forms.ScrollableControl)scroll.ControlObject;
        var updating = false;
        void FitContent()
        {
            if (updating || native.IsDisposed || native.ClientSize.Width <= 0) return;
            updating = true;
            try
            {
                content.Width = Math.Max(1, native.ClientSize.Width - scroll.Padding.Horizontal);
            }
            finally
            {
                updating = false;
            }
        }
        native.ClientSizeChanged += (_, _) => FitContent();
        scroll.LoadComplete += (_, _) => FitContent();
#endif
        return scroll;
    }

    public static void SizeWindow(Window window, Size preferredClient, Size minimum)
    {
#if WINDOWS_TRAY
        var native = (System.Windows.Forms.Form)window.ControlObject;
        var logicalClient = preferredClient;
        var logicalMinimum = minimum;
        void ApplySize(bool loaded)
        {
            var scale = native.DeviceDpi / 96d;
            var screen = loaded
                ? System.Windows.Forms.Screen.FromControl(native.Owner ?? native)
                : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            var work = screen.WorkingArea;
            // Eto WinForms uses physical pixels. Allow for the real window frame
            // and leave a margin inside the monitor work area, including taskbar.
            var frame = native.Size - native.ClientSize;
            var available = new Size(
                Math.Max(1, work.Width - frame.Width - 32),
                Math.Max(1, work.Height - frame.Height - 32));
            var client = new Size(
                Math.Min(available.Width, (int)Math.Ceiling(logicalClient.Width * scale)),
                Math.Min(available.Height, (int)Math.Ceiling(logicalClient.Height * scale)));
            window.MinimumSize = new Size(
                Math.Min(client.Width + frame.Width, (int)Math.Ceiling(logicalMinimum.Width * scale)),
                Math.Min(client.Height + frame.Height, (int)Math.Ceiling(logicalMinimum.Height * scale)));
            window.ClientSize = client;
        }

        ApplySize(loaded: false);
        // Eto's native Load handler replaces the constructor's client constraints
        // with its default form size. Apply once AFTER that handler, not during
        // Eto LoadComplete/AttachNative, which can fire before native OnLoad.
        void OnFirstLoad(object? sender, EventArgs e)
        {
            native.Load -= OnFirstLoad;
            ApplySize(loaded: true);
        }
        native.Load += OnFirstLoad;
#else
        window.MinimumSize = minimum;
        window.ClientSize = preferredClient;
#endif
    }
}
