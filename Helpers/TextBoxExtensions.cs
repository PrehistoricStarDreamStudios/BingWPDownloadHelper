using System;

namespace BingPaper
{
    public static class TextBoxExtensions
    {
        // Provide AppendText and ScrollToEnd extension methods for WinUI TextBox to keep compatibility
        public static void AppendText(this Microsoft.UI.Xaml.Controls.TextBox tb, string text)
        {
            try
            {
                if (tb == null) return;
                // Append text and keep existing content
                tb.Text = (tb.Text ?? string.Empty) + text;
            }
            catch { }
        }

        public static void ScrollToEnd(this Microsoft.UI.Xaml.Controls.TextBox tb)
        {
            try
            {
                if (tb == null) return;
                // Move caret to end so the view follows; selection APIs exist on WinUI TextBox
                tb.SelectionStart = (tb.Text ?? string.Empty).Length;
                tb.SelectionLength = 0;
                try { tb.Focus(Microsoft.UI.Xaml.FocusState.Programmatic); } catch { }
            }
            catch { }
        }
    }
}
