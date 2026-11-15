using System.Windows;
using System.Windows.Controls;
using SnapTrans.App.Controls;
using SnapTrans.Core.Data;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace SnapTrans.App.Windows;

/// <summary>Opt-in text history viewer. Reading or copying never sends a network request.</summary>
public sealed class HistoryWindow : Window
{
    private readonly ITranslationHistory _history;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ListBox _items = new() { MinWidth = 220 };
    private readonly TextBox _source = Viewer();
    private readonly TextBox _translation = Viewer();
    private readonly TextBlock _status = Ui.Muted("");
    private readonly TextBox _search = new() { Tag = "搜索原文、译文或模型", Margin = new Thickness(0, 0, 0, 10) };
    private IReadOnlyList<HistoryEntry> _entries = [];

    public HistoryWindow(ITranslationHistory history)
    {
        _history = history;
        Title = "屏译 · 翻译历史";
        Width = Math.Min(960, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(700, SystemParameters.WorkArea.Height - 32);
        MinWidth = Math.Min(640, Width);
        MinHeight = Math.Min(400, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 13;
        SystemTheme.Attach(this);
        var root = new DockPanel { Margin = new Thickness(24, 20, 24, 16) };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var commands = new StackPanel { Orientation = Orientation.Horizontal };
        commands.Children.Add(Command(Ui.Glyph.Refresh, "", "刷新", RefreshAsync));
        commands.Children.Add(Command(Ui.Glyph.Delete, "清空", "清空全部历史", ClearAsync, "Button.Danger"));
        DockPanel.SetDock(commands, Dock.Right);
        top.Children.Add(commands);
        top.Children.Add(Ui.Text("翻译历史", 22, FontWeights.SemiBold));
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        _status.Margin = new Thickness(2, 10, 0, 0);
        root.Children.Add(top);
        root.Children.Add(_status);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var list = new DockPanel { Margin = new Thickness(0, 0, 16, 0) };
        DockPanel.SetDock(_search, Dock.Top);
        list.Children.Add(_search);
        _items.ItemTemplate = ItemTemplate();
        list.Children.Add(_items);
        grid.Children.Add(list);
        var details = new Grid();
        details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        details.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var source = Pane("原文", _source, () => CopyAsync(_source.Text));
        source.Margin = new Thickness(0, 0, 0, 8);
        details.Children.Add(source);
        var translated = Pane("译文", _translation, () => CopyAsync(_translation.Text));
        Grid.SetRow(translated, 1);
        details.Children.Add(translated);
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);
        root.Children.Add(grid);
        Content = root;
        _items.SelectionChanged += (_, _) =>
        {
            if (_items.SelectedItem is not HistoryItem selected) { _source.Text = _translation.Text = ""; return; }
            var entry = selected.Entry;
            _source.Text = entry.SourceText;
            _translation.Text = entry.Translation;
            _status.Text = $"{entry.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {entry.Pipeline} · {entry.ProfileName} / {entry.Model} · {entry.TargetLanguage}";
        };
        _search.TextChanged += (_, _) => Filter();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private async Task RefreshAsync()
    {
        try
        {
            _entries = await _history.ListAsync(_lifetime.Token);
            Filter();
            _status.Text = $"共 {_entries.Count} 条";
        }
        catch (OperationCanceledException) { }
        catch { _status.Text = "读取失败，请检查文件权限。"; }
    }
    private void Filter()
    {
        string query = _search.Text.Trim();
        var filtered = _entries.Where(entry => query.Length == 0 || entry.SourceText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Translation.Contains(query, StringComparison.OrdinalIgnoreCase) || entry.Model.Contains(query, StringComparison.OrdinalIgnoreCase));
        _items.ItemsSource = filtered.Select(entry => new HistoryItem(entry)).ToArray();
        if (_items.Items.Count > 0) _items.SelectedIndex = 0;
    }
    private async Task ClearAsync()
    {
        if (MessageBox.Show(this, "清空所有翻译历史？此操作无法撤销。", "清空历史", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _history.ClearAsync(_lifetime.Token);
        await RefreshAsync();
    }
    private Task CopyAsync(string text)
    {
        if (text.Length > 0) System.Windows.Clipboard.SetText(text);
        _status.Text = "已复制";
        return Task.CompletedTask;
    }
    private Button Command(string glyph, string title, string tooltip, Func<Task> action, string? style = null)
    {
        var button = Ui.Button(title, style, glyph);
        button.ToolTip = tooltip;
        button.Margin = new Thickness(8, 0, 0, 0);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(); }
            catch (OperationCanceledException) { }
            catch { _status.Text = "操作失败，请稍后重试。"; }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
    private static Border Pane(string title, TextBox viewer, Func<Task> copy)
    {
        var panel = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(16, 8, 8, 0) };
        var copyButton = Ui.IconButton(Ui.Glyph.Copy, "复制" + title);
        copyButton.Click += async (_, _) => await copy();
        DockPanel.SetDock(copyButton, Dock.Right);
        header.Children.Add(copyButton);
        var label = Ui.Text(title, 12, FontWeights.SemiBold, "Brush.TextMuted");
        label.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(label);
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(viewer);
        return Ui.Card(panel, new Thickness(0));
    }
    private static DataTemplate ItemTemplate()
    {
        var root = new FrameworkElementFactory(typeof(StackPanel));
        var meta = new FrameworkElementFactory(typeof(TextBlock));
        meta.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(HistoryItem.Meta)));
        meta.SetValue(TextBlock.FontSizeProperty, 11.5);
        meta.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextMuted");
        var preview = new FrameworkElementFactory(typeof(TextBlock));
        preview.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(HistoryItem.Preview)));
        preview.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        preview.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 0));
        root.AppendChild(meta);
        root.AppendChild(preview);
        return new DataTemplate { VisualTree = root };
    }
    private static TextBox Viewer()
    {
        var box = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 6, 16, 12)
        };
        box.SetResourceReference(StyleProperty, "TextBox.Reader");
        return box;
    }
    private sealed record HistoryItem(HistoryEntry Entry)
    {
        public string Meta => $"{Entry.CreatedAt.ToLocalTime():MM-dd HH:mm} · {Entry.Model}";
        public string Preview
        {
            get
            {
                string line = Entry.Translation.Replace('\r', ' ').Replace('\n', ' ');
                return line.Length > 70 ? line[..70] + "…" : line;
            }
        }
    }
}
