using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudClient.UI.Controls;

namespace CloudClient.UI;

/// <summary>Small builders so pages are written in C# with the theme's look.</summary>
public static class Ui
{
    public static Brush B(string key) => (Brush)Application.Current.Resources[key];
    public static T Res<T>(string key) => (T)Application.Current.Resources[key];

    public static TextBlock Text(string text, string style)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(FrameworkElement.StyleProperty, style);
        return block;
    }

    public static StackPanel Stack(params UIElement[] children) => Stack(0, children);

    public static StackPanel Stack(double spacing, params UIElement[] children)
    {
        var panel = new StackPanel();
        for (var i = 0; i < children.Length; i++)
        {
            if (i > 0 && spacing > 0 && children[i] is FrameworkElement element)
                element.Margin = new Thickness(element.Margin.Left, element.Margin.Top + spacing, element.Margin.Right, element.Margin.Bottom);
            panel.Children.Add(children[i]);
        }
        return panel;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            if (child is FrameworkElement element && element.VerticalAlignment == VerticalAlignment.Stretch)
                element.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(child);
        }
        return panel;
    }

    /// <summary>The app's card: dark panel, 1 px border, bottom-right corner cut.</summary>
    public static ChamferBorder Card(UIElement child, double padding = 22)
    {
        var card = new ChamferBorder
        {
            Padding = new Thickness(padding, padding - 2, padding, padding - 2),
            Chamfer = 16,
            Child = child,
        };
        card.SetResourceReference(ChamferBorder.BackgroundProperty, "PanelBg");
        card.SetResourceReference(ChamferBorder.BorderBrushProperty, "PanelBorder");
        return card;
    }

    /// <summary>Card header: a small accent square and an uppercase title.</summary>
    public static UIElement PanelTitle(string title)
    {
        var square = new Border { Width = 8, Height = 8, Margin = new Thickness(0, 0, 10, 0) };
        square.SetResourceReference(Border.BackgroundProperty, "Accent");
        return Row(square, Text(title.ToUpperInvariant(), "PanelTitleText"));
    }

    public static LucideIcon Icon(string name, double size = 16, string? brushKey = null)
    {
        var icon = new LucideIcon { Icon = name, Width = size, Height = size };
        if (brushKey is not null) icon.SetResourceReference(LucideIcon.ForegroundProperty, brushKey);
        return icon;
    }

    /// <summary>A button with an icon and uppercase text. Style: PrimaryButton, SecondaryButton, DangerButton.</summary>
    public static Button Button(string text, string icon, string style, RoutedEventHandler? onClick = null)
    {
        var label = new TextBlock { Text = text.ToUpperInvariant(), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = Row(Icon(icon), label) };
        button.SetResourceReference(FrameworkElement.StyleProperty, style);
        AutomationProperties.SetName(button, text.ToUpperInvariant());
        if (onClick is not null) button.Click += onClick;
        return button;
    }

    public static Button Link(string text, RoutedEventHandler onClick)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
        button.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        AutomationProperties.SetName(button, text);
        button.Click += onClick;
        return button;
    }

    /// <summary>A square tinted chip holding an icon.</summary>
    public static Border IconChip(string icon, string toneKey, string chipKey, double size, double iconSize)
    {
        var chip = new Border { Width = size, Height = size, Child = Icon(icon, iconSize, toneKey) };
        chip.SetResourceReference(Border.BackgroundProperty, chipKey);
        ((FrameworkElement)chip.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((FrameworkElement)chip.Child).VerticalAlignment = VerticalAlignment.Center;
        return chip;
    }

    /// <summary>A labelled input: an uppercase label above the field.</summary>
    public static StackPanel Field(string label, Control input)
    {
        var caption = Text(label.ToUpperInvariant(), "CardLabel");
        caption.Margin = new Thickness(0, 0, 0, 6);
        AutomationProperties.SetName(input, label);
        return Stack(caption, input);
    }

    public static TextBlock Sentence(string text, string brushKey)
    {
        var block = Text(text, "Body");
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }

    /// <summary>An image from the brand folder (icon.png, logo.png), picked at build time.</summary>
    public static Image BrandImage(string file, double height)
    {
        var image = new Image
        {
            Source = new BitmapImage(new Uri($"pack://application:,,,/Brand/{file}")),
            Height = height,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }
}
