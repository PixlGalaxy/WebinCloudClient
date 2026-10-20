using System.Windows;
using System.Windows.Controls;
using CloudClient.UI.Controls;

namespace CloudClient.UI;

/// <summary>The app's own message box: a chamfered card with a coloured top line. Never use MessageBox.</summary>
public static class AppDialog
{
    /// <summary>Yes/no question. Returns true when the user confirms.</summary>
    public static bool Confirm(Window? owner, string title, string message, string confirmText, string icon,
        string toneKey = "Accent", bool destructive = false)
    {
        var confirmed = false;
        var window = new AppWindow
        {
            Title = Brand.Name,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = owner is null,
        };
        if (owner is not null && owner.IsVisible) window.Owner = owner;

        var chipKey = toneKey switch { "Danger" => "DangerChip", "Warning" => "WarningChip", "Success" => "SuccessChip", _ => "AccentChip" };
        var heading = Ui.Text(title, "PanelTitleText");
        heading.FontSize = 20;
        heading.VerticalAlignment = VerticalAlignment.Center;
        heading.Margin = new Thickness(14, 0, 0, 0);
        var text = Ui.Text(message, "Body");
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        text.MaxWidth = 420;

        var cancel = Ui.Button("Cancel", "x", "SecondaryButton", (_, _) => window.Close());
        cancel.IsCancel = true;
        var ok = Ui.Button(confirmText, icon, destructive ? "DangerButton" : "PrimaryButton", (_, _) =>
        {
            confirmed = true;
            window.Close();
        });
        ok.Margin = new Thickness(10, 0, 0, 0);
        var buttons = Ui.Row(cancel, ok);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Margin = new Thickness(0, 24, 0, 0);

        var topLine = new Border { Height = 3, Margin = new Thickness(-22, -20, -22, 18) };
        topLine.SetResourceReference(Border.BackgroundProperty, toneKey);

        var card = Ui.Card(Ui.Stack(topLine, Ui.Row(Ui.IconChip(icon, toneKey, chipKey, 44, 22), heading), Spaced(text, 14), buttons));
        card.Margin = new Thickness(18);
        window.Content = card;
        window.ShowDialog();
        return confirmed;
    }

    private static FrameworkElement Spaced(FrameworkElement element, double top)
    {
        element.Margin = new Thickness(0, top, 0, 0);
        return element;
    }
}
