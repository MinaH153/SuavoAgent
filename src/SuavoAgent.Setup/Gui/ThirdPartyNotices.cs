using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace SuavoAgent.Setup.Gui;

internal static class ThirdPartyNotices
{
    internal const string ResourceName =
        "SuavoAgent.Setup.Legal.THIRD-PARTY-NOTICES.txt";

    internal static string Read()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("Embedded third-party notices are missing.");
        if (stream.Length is <= 0 or > 4 * 1024 * 1024)
            throw new InvalidDataException("Embedded third-party notices have an invalid size.");
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: false);
        return reader.ReadToEnd();
    }

    internal static Window CreateWindow() => new()
    {
        Title = "SuavoAgent third-party notices",
        Width = 780,
        Height = 620,
        MinWidth = 520,
        MinHeight = 400,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = new TextBox
        {
            Text = Read(),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            FontFamily = "Consolas",
            FontSize = 12,
            Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            [AutomationProperties.NameProperty] = "Third-party software licenses and provenance",
        },
    };
}
