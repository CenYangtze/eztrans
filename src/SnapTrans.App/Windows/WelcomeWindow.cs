using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SnapTrans.App.Controls;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace SnapTrans.App.Windows;

/// <summary>First-run guidance; provider connectivity and secrets remain in SettingsWindow.</summary>
public sealed class WelcomeWindow : Window
{
    public WelcomeWindow(Action openSettings, Action? tryCapture = null)
    {
        Title = "欢迎使用屏译 SnapTrans";
        Icon = Ui.LogoSource;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        MinWidth = 520;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 13;
        SystemTheme.Attach(this);

        var root = new StackPanel { Margin = new Thickness(36, 32, 36, 28) };
        var logo = Ui.Logo(48);
        logo.HorizontalAlignment = HorizontalAlignment.Left;
        logo.Margin = new Thickness(0, 0, 0, 18);
        root.Children.Add(logo);
        root.Children.Add(Ui.Text("框选屏幕，即刻翻译", 24, FontWeights.SemiBold));
        var lead = Ui.Muted("先连接一个模型，就可以开始使用。", 14);
        lead.Margin = new Thickness(0, 8, 0, 24);
        root.Children.Add(lead);

        root.Children.Add(Step("1", "连接模型", "选择服务商预设，填写模型名和 API Key"));
        root.Children.Add(Step("2", "测试连接", null));
        root.Children.Add(Step("3", "开始翻译", null, ("Alt", "Q", "悬浮译文"), ("Alt+Shift", "Q", "原位译图"), ("Alt", "W", "查光标旁的单词")));
        root.Children.Add(new Border { Height = 20 });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var later = Ui.Button("稍后", "Button.Subtle");
        later.Click += (_, _) => Close();
        buttons.Children.Add(later);
        if (tryCapture is not null)
        {
            var capture = Ui.Button("试一下");
            capture.Margin = new Thickness(8, 0, 0, 0);
            capture.Click += (_, _) => { Close(); tryCapture(); };
            buttons.Children.Add(capture);
        }
        var settings = Ui.Button("连接模型", "Button.Accent");
        settings.Margin = new Thickness(8, 0, 0, 0);
        settings.Padding = new Thickness(18, 0, 18, 0);
        settings.IsDefault = true;
        settings.Click += (_, _) => openSettings();
        buttons.Children.Add(settings);
        root.Children.Add(buttons);
        Content = root;
    }

    private static Border Step(string number, string title, string? text, params (string Modifiers, string Key, string Action)[] shortcuts)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 14, 0),
            Child = Ui.Text(number, 12, FontWeights.SemiBold, "Brush.Accent") };
        ((TextBlock)badge.Child).HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)badge.Child).VerticalAlignment = VerticalAlignment.Center;
        badge.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        grid.Children.Add(badge);
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(Ui.Text(title, 14, FontWeights.SemiBold));
        if (text is not null)
        {
            var detail = Ui.Muted(text, 12.5);
            detail.Margin = new Thickness(0, 3, 0, 0);
            body.Children.Add(detail);
        }
        foreach (var (modifiers, key, action) in shortcuts)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var part in modifiers.Split('+')) row.Children.Add(Ui.Key(part));
            row.Children.Add(Ui.Key(key));
            var label = Ui.Muted(action, 12.5);
            label.Margin = new Thickness(6, 0, 0, 0);
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            body.Children.Add(row);
        }
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        var card = Ui.Card(grid, new Thickness(16, 14, 16, 14));
        card.Margin = new Thickness(0, 0, 0, 6);
        return card;
    }
}
