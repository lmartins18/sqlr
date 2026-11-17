using System.Text;
using Terminal.Gui;

namespace Sqlr.Tui;

internal static class ClipboardHelper
{
    /// <summary>
    /// Copies to the OS clipboard; when that isn't available (SSH sessions, Linux without xclip)
    /// falls back to the OSC 52 escape sequence, which most modern terminals honour.
    /// </summary>
    public static bool Copy(string text)
    {
        try
        {
            if (Clipboard.IsSupported && Clipboard.TrySetClipboardData(text))
                return true;
        }
        catch { /* fall through to OSC 52 */ }

        try
        {
            Application.Driver?.WriteRaw($"\u001b]52;c;{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}\u0007");
            return true;
        }
        catch
        {
            return false;
        }
    }
}
