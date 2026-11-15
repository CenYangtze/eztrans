using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SnapTrans.Core.Translation;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;

namespace SnapTrans.App.Controls;

/// <summary>One choice in a <see cref="SearchPicker"/>; <see cref="Search"/> holds every word the filter may match.</summary>
public sealed record PickerItem(string Value, string Label, string? Detail = null, string? Search = null)
{
    public string SearchText { get; } = (Search ?? $"{Label} {Detail} {Value}").ToLowerInvariant();
}

/// <summary>
/// A drop-down with a search box: users choose languages and fonts by name instead of typing tags.
/// Type to filter, arrow keys to move, Enter to choose, Esc to close.
/// </summary>
public sealed class SearchPicker : Grid
{
    private readonly Button _button = new();
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Popup _popup;
    private readonly Border _surface;
    private readonly TextBox _search = new() { Tag = "搜索" };
    private readonly ListBox _list = new() { MaxHeight = 300, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _empty = Ui.Muted("没有匹配项");
    private IReadOnlyList<PickerItem> _items;
    private string? _value;
    private DateTime _closedAt = DateTime.MinValue;

    /// <summary>Raised only when the user picks a different value.</summary>
    public event Action<string>? ValueChanged;

    public SearchPicker(IEnumerable<PickerItem> items, string? value)
    {
        _items = items.ToArray();
        var chevron = Ui.Icon(Ui.Glyph.ChevronDown, 10, "Brush.TextMuted");
        chevron.Margin = new Thickness(8, 0, 0, 0);
        var content = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(chevron, Dock.Right);
        content.Children.Add(chevron);
        content.Children.Add(_label);
        _button.Content = content;
        _button.SetResourceReference(StyleProperty, "Button.Picker");
        _button.Click += (_, _) => Toggle();
        System.Windows.Automation.AutomationProperties.SetAutomationId(_button, "SearchPicker");
        Children.Add(_button);

        _list.DisplayMemberPath = nameof(PickerItem.Label);
        _empty.Margin = new Thickness(12, 10, 12, 8);
        _empty.Visibility = Visibility.Collapsed;
        var body = new DockPanel();
        DockPanel.SetDock(_search, Dock.Top);
        body.Children.Add(_search);
        DockPanel.SetDock(_empty, Dock.Top);
        body.Children.Add(_empty);
        body.Children.Add(_list);
        var surface = _surface = new Border
        {
            Child = body, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 10, 10), Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = .22 }
        };
        surface.SetResourceReference(Border.BackgroundProperty, "Brush.Popup");
        surface.SetResourceReference(Border.BorderBrushProperty, "Brush.CardBorder");
        surface.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
        surface.SetResourceReference(TextElement.FontFamilyProperty, "Font.Ui");
        TextElement.SetFontSize(surface, 13);
        _popup = new Popup
        {
            PlacementTarget = _button, Placement = PlacementMode.Bottom, VerticalOffset = 4, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, Child = surface
        };
        Children.Add(_popup);
        _popup.Opened += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { _search.Focus(); Keyboard.Focus(_search); });
        _popup.Closed += (_, _) => _closedAt = DateTime.UtcNow;
        _search.TextChanged += (_, _) => Filter();
        _search.PreviewKeyDown += OnSearchKey;
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: PickerItem item }) Commit(item);
        };
        _list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _list.SelectedItem is PickerItem item) { Commit(item); e.Handled = true; }
            else if (e.Key == Key.Escape) { _popup.IsOpen = false; e.Handled = true; }
        };
        Value = value;
    }

    public string? Value
    {
        get => _value;
        set { _value = value; UpdateLabel(); }
    }

    /// <summary>Smaller metrics for toolbars such as the floating result header.</summary>
    public void MakeCompact()
    {
        _button.MinHeight = _button.Height = 28;
        _button.FontSize = 12;
        _button.Padding = new Thickness(8, 0, 8, 0);
    }

    public void SetItems(IEnumerable<PickerItem> items) { _items = items.ToArray(); UpdateLabel(); }

    private void UpdateLabel()
    {
        var item = _items.FirstOrDefault(i => string.Equals(i.Value, _value, StringComparison.OrdinalIgnoreCase));
        _label.Text = item?.Label ?? _value ?? "";
        _button.ToolTip = item is null || item.Detail is null ? null : $"{item.Label}（{item.Detail}）";
    }

    private void Toggle()
    {
        // A click on the button first dismisses the open popup; do not reopen it on the same click.
        if (_popup.IsOpen || (DateTime.UtcNow - _closedAt).TotalMilliseconds < 250) { _popup.IsOpen = false; return; }
        _search.Text = "";
        Filter();
        _surface.MinWidth = Math.Max(220, _button.ActualWidth);
        _popup.IsOpen = true;
    }

    private void Filter()
    {
        string[] terms = _search.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = _items.Where(item => terms.All(item.SearchText.Contains)).ToArray();
        _list.ItemsSource = matches;
        _empty.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var current = matches.FirstOrDefault(i => string.Equals(i.Value, _value, StringComparison.OrdinalIgnoreCase));
        _list.SelectedItem = terms.Length == 0 ? current : matches.FirstOrDefault();
        if (_list.SelectedItem is not null) _list.ScrollIntoView(_list.SelectedItem);
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Move(1); break;
            case Key.Up: Move(-1); break;
            case Key.Enter: if (_list.SelectedItem is PickerItem item) Commit(item); break;
            case Key.Escape: _popup.IsOpen = false; break;
            default: return;
        }
        e.Handled = true;
    }

    private void Move(int delta)
    {
        if (_list.Items.Count == 0) return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);
        _list.ScrollIntoView(_list.SelectedItem);
    }

    private void Commit(PickerItem item)
    {
        _popup.IsOpen = false;
        if (string.Equals(item.Value, _value, StringComparison.Ordinal)) return;
        Value = item.Value;
        ValueChanged?.Invoke(item.Value);
    }

    /// <summary>All catalog languages; a tag outside the catalog (hand-edited settings) stays selectable.</summary>
    public static IReadOnlyList<PickerItem> Languages(params string?[] keep)
    {
        var items = LanguageCatalog.All.Select(l => new PickerItem(l.Tag, l.Display, l.Tag, l.SearchText)).ToList();
        foreach (string? tag in keep)
            if (!string.IsNullOrWhiteSpace(tag) && !items.Any(i => i.Value.Equals(tag, StringComparison.OrdinalIgnoreCase)))
                items.Add(new PickerItem(tag, LanguageCatalog.Describe(tag), tag));
        return items;
    }

    private static IReadOnlyList<PickerItem>? _fonts;

    /// <summary>Installed font families, labelled with their Chinese name when the font provides one.</summary>
    public static IReadOnlyList<PickerItem> SystemFonts()
    {
        if (_fonts is not null) return _fonts;
        var chinese = System.Windows.Markup.XmlLanguage.GetLanguage("zh-cn");
        var items = new List<PickerItem>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            string source = family.Source;
            if (string.IsNullOrWhiteSpace(source)) continue;
            string label = source;
            try { if (family.FamilyNames.TryGetValue(chinese, out string? localized) && !string.IsNullOrWhiteSpace(localized)) label = localized; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.IOException) { }
            items.Add(new PickerItem(source, label, label == source ? null : source, $"{label} {source}"));
        }
        return _fonts = items.OrderBy(i => i.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
