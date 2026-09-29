using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace EasyFlix.App;

internal static class LegacyTextPolish
{
    private const string BrokenSeparator = "\u0393\u00F6\u00BC\u0393\u00F2\u00FB";
    private const string BrokenEllipsis = "\u256C\u00F4\u251C\u00E7\u252C\u00AC";

    private static readonly DependencyProperty SubscribedProperty =
        DependencyProperty.RegisterAttached(
            "Subscribed",
            typeof(bool),
            typeof(LegacyTextPolish),
            new PropertyMetadata(false));

    private static readonly DependencyPropertyDescriptor? TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    private static bool _enabled;

    public static void Enable()
    {
        if (_enabled)
            return;

        _enabled = true;

        EventManager.RegisterClassHandler(
            typeof(TextBlock),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnTextBlockLoaded));

        EventManager.RegisterClassHandler(
            typeof(ContentControl),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnContentControlLoaded));

        EventManager.RegisterClassHandler(
            typeof(FrameworkElement),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnFrameworkElementLoaded));
    }

    internal static string CleanVisible(string value)
        => value
            .Replace(BrokenSeparator, "|", StringComparison.Ordinal)
            .Replace(BrokenEllipsis, "...", StringComparison.Ordinal)
            .Replace(
                "No ReelShelf subtitle events recorded yet.",
                "No EasyFlix subtitle events recorded yet.",
                StringComparison.Ordinal)
            .Replace(
                "ReelShelf will never traverse this folder",
                "EasyFlix will never traverse this folder",
                StringComparison.Ordinal)
            .Replace(
                "Animated while ReelShelf is working",
                "Animated while EasyFlix is working",
                StringComparison.Ordinal);

    private static void OnTextBlockLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock textBlock)
            return;

        PolishTextBlock(textBlock);

        if ((bool)textBlock.GetValue(SubscribedProperty))
            return;

        textBlock.SetValue(SubscribedProperty, true);
        TextDescriptor?.AddValueChanged(textBlock, (_, _) => PolishTextBlock(textBlock));
    }

    private static void OnContentControlLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ContentControl { Content: string content } control)
            return;

        var cleaned = CleanVisible(content);
        if (!string.Equals(content, cleaned, StringComparison.Ordinal))
            control.SetCurrentValue(ContentControl.ContentProperty, cleaned);
    }

    private static void OnFrameworkElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ToolTip: string toolTip } element)
            return;

        var cleaned = CleanVisible(toolTip);
        if (!string.Equals(toolTip, cleaned, StringComparison.Ordinal))
            element.SetCurrentValue(FrameworkElement.ToolTipProperty, cleaned);
    }

    private static void PolishTextBlock(TextBlock textBlock)
    {
        var current = textBlock.Text ?? string.Empty;
        var cleaned = CleanVisible(current);
        if (!string.Equals(current, cleaned, StringComparison.Ordinal))
            textBlock.SetCurrentValue(TextBlock.TextProperty, cleaned);
    }
}
