using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Collections.ObjectModel;
using System.Windows.Data;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SnapTrans.App.Controls;
using SnapTrans.Core.Dictionaries;
using SnapTrans.Core.Abstractions;
using SnapTrans.Core.Hotkeys;
using SnapTrans.Core.Ocr;
using SnapTrans.Core.Settings;
using SnapTrans.Core.Translation;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace SnapTrans.App.Windows;

/// <summary>Settings are applied immediately and persisted after 500ms of inactivity.</summary>
public sealed class SettingsWindow : Window
{
    private const string ReleasesUrl = "https://github.com/CenYangtze/eztrans/releases";
    private readonly AppSettings _settings;
    private readonly SettingsFeatureServices? _features;
    private CancellationTokenSource? _paddleSetup;
    private readonly Action _save;
    private readonly Action? _settingsChanged;
    private readonly ISecretProtector _secrets;
    private readonly IOcrEngine _ocr;
    private readonly Func<Profile, bool, CancellationToken, Task<string>> _testConnection;
    private readonly DispatcherTimer _saveTimer;
    private readonly TextBlock _status = Ui.Muted("修改会自动保存");
    private readonly TextBlock _statusIcon = Ui.Icon(Ui.Glyph.Check, 12, "Brush.TextSubtle");
    private readonly ListBox _nav = new() { Margin = new Thickness(10, 0, 10, 0) };
    private readonly ContentControl _page = new() { Focusable = false };
    private readonly List<PageEntry> _pages = [];
    private readonly CancellationTokenSource _lifetime = new();
    private ComboBox? _profilePicker;
    private StackPanel? _profileEditor;
    private bool _dirty;

    /// <summary>Pages are built on first visit; dynamic pages are rebuilt on every visit so they follow other pages' changes.</summary>
    private sealed class PageEntry(string name, Func<StackPanel> build, bool dynamic)
    {
        public string Name { get; } = name;
        public Func<StackPanel> Build { get; } = build;
        public bool Dynamic { get; } = dynamic;
        public UIElement? View { get; set; }
    }

    public SettingsWindow(AppSettings settings, Action save, ISecretProtector secrets, IOcrEngine ocr,
        Func<Profile, bool, CancellationToken, Task<string>> testConnection, Action? settingsChanged = null, SettingsFeatureServices? features = null)
    {
        _settings = settings;
        _features = features;
        _save = save;
        _secrets = secrets;
        _ocr = ocr;
        _testConnection = testConnection;
        _settingsChanged = settingsChanged;
        Title = "屏译 SnapTrans · 设置";
        Icon = Ui.LogoSource;
        Width = Math.Min(1000, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(720, SystemParameters.WorkArea.Height - 32);
        MinWidth = Math.Min(780, Width);
        MinHeight = Math.Min(520, Height);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 13;
        SystemTheme.Attach(this);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => Flush();

        AddPage("常规", Ui.Glyph.Settings, GeneralPage);
        AddPage("个性化", Ui.Glyph.Personalize, PersonalizationPage);
        AddPage("快捷键", Ui.Glyph.Keyboard, HotkeysPage);
        AddPage("翻译", Ui.Glyph.Globe, TranslationPage);
        AddPage("词典", Ui.Glyph.Dictionary, DictionaryPage);
        AddPage("模型", Ui.Glyph.Robot, ModelsPage);
        AddPage("文字识别", Ui.Glyph.Font, OcrPage);
        AddPage("译图", Ui.Glyph.Picture, ImagePage, dynamic: true);
        AddPage("高级", Ui.Glyph.Repair, AdvancedPage);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_nav, "SettingsNav");
        _nav.SelectionChanged += (_, _) => ShowPage();
        _nav.SelectedIndex = 0;
        Content = Layout();

        Closing += (_, _) => System.Windows.Input.Keyboard.ClearFocus();
        Closed += (_, _) =>
        {
            Flush(); _saveTimer.Stop(); _paddleSetup?.Cancel();
            try { _features?.HotkeyRecordingChanged?.Invoke(false); } catch { }
            _lifetime.Cancel(); _lifetime.Dispose();
        };
    }

    public void SelectModelsPage() => _nav.SelectedIndex = _pages.FindIndex(p => p.Name == "模型");

    private void ShowPage(bool rebuild = false)
    {
        if (_nav.SelectedIndex < 0) return;
        var entry = _pages[_nav.SelectedIndex];
        if (entry.View is null || entry.Dynamic || rebuild)
            entry.View = new ScrollViewer { Content = entry.Build(), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        _page.Content = entry.View;
    }

    private UIElement Layout()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(212) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        var sidebar = new DockPanel();
        sidebar.SetResourceReference(Panel.BackgroundProperty, "Brush.Sidebar");
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 22, 20, 20) };
        brand.Children.Add(Ui.Logo(34));
        var name = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Ui.Text("屏译", 15, FontWeights.SemiBold));
        name.Children.Add(Ui.Muted("SnapTrans", 11));
        brand.Children.Add(name);
        DockPanel.SetDock(brand, Dock.Top);
        sidebar.Children.Add(brand);
        var version = Ui.Muted("v" + AppVersion, 11);
        version.Margin = new Thickness(22, 0, 22, 16);
        DockPanel.SetDock(version, Dock.Bottom);
        sidebar.Children.Add(version);
        sidebar.Children.Add(_nav);
        root.Children.Add(sidebar);

        var main = new DockPanel();
        var statusBar = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(32, 10, 32, 10) };
        statusBar.SetResourceReference(Border.BorderBrushProperty, "Brush.Divider");
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        _statusIcon.Margin = new Thickness(0, 0, 8, 0);
        statusRow.Children.Add(_statusIcon);
        statusRow.Children.Add(_status);
        statusBar.Child = statusRow;
        DockPanel.SetDock(statusBar, Dock.Bottom);
        main.Children.Add(statusBar);
        main.Children.Add(_page);
        Grid.SetColumn(main, 1);
        root.Children.Add(main);
        return root;
    }

    private void AddPage(string name, string glyph, Func<StackPanel> build, bool dynamic = false)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = Ui.Icon(glyph, 15);
        icon.Margin = new Thickness(2, 0, 12, 0);
        item.Children.Add(icon);
        item.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        _nav.Items.Add(new ListBoxItem { Content = item });
        _pages.Add(new PageEntry(name, build, dynamic));
    }

    // ───────────────────────────── Pages ─────────────────────────────

    private StackPanel GeneralPage()
    {
        var page = Page("常规");
        page.Children.Add(Group("启动"));
        page.Children.Add(Toggle("开机自启", _settings.General.LaunchAtStartup, v => _settings.General.LaunchAtStartup = v));

        page.Children.Add(Group("框选"));
        page.Children.Add(Choice("松开鼠标后", _settings.General.ConfirmMode,
            [("立即执行", "releaseToRun"), ("显示工具栏，确认后执行", "toolbarConfirm")], v => _settings.General.ConfirmMode = v));
        page.Children.Add(Toggle("放大镜", _settings.Capture.Magnifier, v => _settings.Capture.Magnifier = v));
        page.Children.Add(Toggle("识别窗口", _settings.Capture.WindowSelection, v => _settings.Capture.WindowSelection = v, "悬停高亮，单击选中整个窗口"));
        page.Children.Add(Toggle("黑屏兜底", _settings.Capture.UseGraphicsCaptureFallback, v => _settings.Capture.UseGraphicsCaptureFallback = v, "截图全黑时改用 Windows Graphics Capture"));
        page.Children.Add(Toggle("截图时隐藏屏译窗口", _settings.General.ExcludeOwnWindowsFromCapture, v => _settings.General.ExcludeOwnWindowsFromCapture = v, "屏幕共享中也会不可见"));

        page.Children.Add(Group("悬浮窗"));
        page.Children.Add(Toggle("失去焦点时关闭", _settings.Floating.CloseOnDeactivate, v => _settings.Floating.CloseOnDeactivate = v));

        page.Children.Add(Group("隐私"));
        page.Children.Add(Tile("发送到云端前提示", Command("再次提示", () => { _settings.General.CloudPrivacyNoticeAccepted = false; Changed(); SetStatus("下次使用云端模型前会再次提示。"); })));
        return page;
    }

    private StackPanel PersonalizationPage()
    {
        var page = Page("个性化");
        page.Children.Add(Group("外观"));
        page.Children.Add(Choice("主题", _settings.General.Theme, [("跟随系统", "system"), ("浅色", "light"), ("深色", "dark")], v => _settings.General.Theme = v));

        page.Children.Add(Group("悬浮窗"));
        var fonts = SearchPicker.SystemFonts();
        page.Children.Add(Picker("字体", [new PickerItem("", "默认"), .. fonts], _settings.Floating.FontFamily ?? "",
            v => _settings.Floating.FontFamily = string.IsNullOrEmpty(v) ? null : v));
        page.Children.Add(Number("字号", _settings.Floating.FontSize, 10, 28, v => _settings.Floating.FontSize = v));
        page.Children.Add(Toggle("原文与译文并排", _settings.Floating.SideBySide, v => _settings.Floating.SideBySide = v));

        page.Children.Add(Group("译图字体"));
        foreach (var (key, label) in new[] { ("zh-Hans", "简体中文"), ("zh-Hant", "繁体中文"), ("ja", "日语"), ("ko", "韩语"), ("default", "其他语言") })
        {
            string current = _settings.ImageTranslate.FontMap.GetValueOrDefault(key, "Segoe UI");
            page.Children.Add(Picker(label, fonts, current, v => _settings.ImageTranslate.FontMap[key] = v));
        }

        page.Children.Add(Group("译图样式"));
        page.Children.Add(Number("最小字号", _settings.ImageTranslate.MinFontPx, 5, 48, v => _settings.ImageTranslate.MinFontPx = (float)v, "像素"));
        page.Children.Add(Toggle("保留粗体", _settings.ImageTranslate.PreserveBold, v => _settings.ImageTranslate.PreserveBold = v));
        page.Children.Add(Toggle("保留描边", _settings.ImageTranslate.PreserveOutline, v => _settings.ImageTranslate.PreserveOutline = v));
        return page;
    }

    private StackPanel HotkeysPage()
    {
        var page = Page("快捷键");
        page.Children.Add(Group("全局"));
        AddHotkey(page, "截图翻译", () => _settings.Hotkeys.TranslateFloating, v => _settings.Hotkeys.TranslateFloating = v);
        AddHotkey(page, "截图译图", () => _settings.Hotkeys.TranslateImage, v => _settings.Hotkeys.TranslateImage = v);
        AddHotkey(page, "重复上次区域", () => _settings.Hotkeys.RepeatLastRegion, v => _settings.Hotkeys.RepeatLastRegion = v, optional: true);
        AddHotkey(page, "查词", () => _settings.Hotkeys.Dictionary, v => _settings.Hotkeys.Dictionary = v, optional: true);

        page.Children.Add(Group("框选时"));
        page.Children.Add(Shortcuts(
            (["Enter"], "确认"),
            (["Ctrl", "Enter"], "生成译图"),
            (["Ctrl", "C"], "复制识别文字"),
            (["Ctrl", "D"], "查词"),
            (["Esc"], "取消"),
            (["方向键"], "移动 1 px，加 Shift 移动 10 px"),
            (["Ctrl", "方向键"], "调整大小")));

        page.Children.Add(Group("结果窗口"));
        page.Children.Add(Shortcuts(
            (["Ctrl", "P"], "钉住"),
            (["Ctrl", "R"], "重新翻译"),
            (["Ctrl", "I"], "生成译图"),
            (["Ctrl", "滚轮"], "调整字号"),
            (["空格"], "按住看原图"),
            (["Esc"], "关闭")));
        return page;
    }

    private void AddHotkey(StackPanel page, string title, Func<string> read, Action<string> write, bool optional = false)
    {
        var recorder = new HotkeyRecorder(read()) { Width = 200 };
        recorder.GotKeyboardFocus += (_, _) =>
        {
            try { _features?.HotkeyRecordingChanged?.Invoke(true); }
            catch { Error("无法暂停热键，请先在托盘菜单中暂停后再录制。"); }
        };
        recorder.LostKeyboardFocus += (_, _) =>
        {
            try { _features?.HotkeyRecordingChanged?.Invoke(false); }
            catch { recorder.Gesture = read(); MarkInvalid(recorder); Error("快捷键已被占用，已恢复原设置。"); }
        };
        recorder.GestureChanged += value =>
        {
            var old = read();
            try
            {
                _ = HotkeyGesture.Parse(value);
                var others = new[] { _settings.Hotkeys.TranslateFloating, _settings.Hotkeys.TranslateImage, _settings.Hotkeys.RepeatLastRegion, _settings.Hotkeys.Dictionary };
                if (others.Any(other => !string.IsNullOrWhiteSpace(other) && other != old && HotkeyGesture.Parse(other) == HotkeyGesture.Parse(value)))
                    throw new ArgumentException("快捷键不能重复");
                write(value);
                _settingsChanged?.Invoke();
                ClearInvalid(recorder);
                Changed(false);
            }
            catch (Exception)
            {
                write(old);
                recorder.Gesture = old;
                MarkInvalid(recorder);
                Error("快捷键无效或已被占用，已恢复原设置。");
            }
        };
        UIElement control = recorder;
        if (optional)
        {
            var clear = Ui.IconButton(Ui.Glyph.Delete, "清除");
            clear.Click += (_, _) => { write(""); recorder.Gesture = ""; Changed(); };
            control = Ui.Row(recorder, clear);
        }
        page.Children.Add(Tile(title, control));
    }

    private static Border Shortcuts(params (string[] Keys, string Action)[] items)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < items.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            foreach (var key in items[i].Keys) keys.Children.Add(Ui.Key(key));
            var action = Ui.Text(items[i].Action, brush: "Brush.TextMuted");
            action.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(keys, i); Grid.SetRow(action, i); Grid.SetColumn(action, 1);
            grid.Children.Add(keys); grid.Children.Add(action);
        }
        var card = Ui.Card(grid, new Thickness(16, 10, 16, 10));
        card.Margin = new Thickness(0, 0, 0, 4);
        return card;
    }

    private StackPanel TranslationPage()
    {
        var page = Page("翻译");
        page.Children.Add(Group("语言"));
        page.Children.Add(Picker("目标语言", SearchPicker.Languages(_settings.Translation.TargetLanguage), _settings.Translation.TargetLanguage,
            v => _settings.Translation.TargetLanguage = Required(v)));
        page.Children.Add(Picker("第二语言", SearchPicker.Languages(_settings.Translation.SecondaryLanguage), _settings.Translation.SecondaryLanguage,
            v => _settings.Translation.SecondaryLanguage = Required(v)));
        page.Children.Add(Toggle("自动切换", _settings.Translation.SmartSwap, v => _settings.Translation.SmartSwap = v, "原文已是目标语言时，改译为第二语言"));

        page.Children.Add(Group("方式"));
        page.Children.Add(Choice("悬浮翻译", _settings.Translation.Pipeline,
            [("自动", "auto"), ("发送截图", "vision"), ("先本地识别文字", "ocr-text")], v => _settings.Translation.Pipeline = v));
        page.Children.Add(Toggle("译图附带截图", _settings.Translation.AttachImageInImageMode, v => _settings.Translation.AttachImageInImageMode = v, "关闭可加快译图"));

        page.Children.Add(Group("术语表"));
        AddGlossary(page);

        page.Children.Add(Group("缓存"));
        AddCache(page);

        page.Children.Add(Group("提示词"));
        AddPrompt(page, "发送截图", () => _settings.Translation.Prompts.Vision, v => _settings.Translation.Prompts.Vision = v, PromptTemplates.Vision, ["target_lang", "secondary_lang"]);
        AddPrompt(page, "识别文字", () => _settings.Translation.Prompts.Text, v => _settings.Translation.Prompts.Text = v, PromptTemplates.Text, ["target_lang", "secondary_lang"]);
        AddPrompt(page, "译图", () => _settings.Translation.Prompts.Segments, v => _settings.Translation.Prompts.Segments = v, PromptTemplates.Segments, []);
        return page;
    }

    private StackPanel DictionaryPage()
    {
        var page = Page("词典");
        page.Children.Add(Group("取词"));
        page.Children.Add(Choice("按下查词快捷键后", _settings.Dictionary.Mode,
            [("查询光标旁的单词", "cursor"), ("框选要查的内容", "capture")], v => _settings.Dictionary.Mode = v));

        page.Children.Add(Group("释义"));
        page.Children.Add(Choice("来源", _settings.Dictionary.Source,
            [("自动", DictionarySources.Auto), ("有道词典", DictionarySources.Youdao), ("Google 翻译", DictionarySources.Google)],
            v => _settings.Dictionary.Source = v, "查询的单词会发送到所选服务；“自动”先用有道，失败时改用 Google"));
        return page;
    }

    private void AddPrompt(StackPanel page, string label, Func<string?> read, Action<string?> write, string defaultTemplate, string[] required)
    {
        var box = new TextBox { Text = read() ?? defaultTemplate, Height = 140, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(8, 6, 8, 6) };
        box.SetResourceReference(TextBox.FontFamilyProperty, "Font.Mono");
        box.LostKeyboardFocus += (_, _) =>
        {
            var value = box.Text;
            var missing = PromptBuilder.Validate(value, required);
            if (!string.IsNullOrWhiteSpace(value) && missing.Count > 0)
            {
                MarkInvalid(box);
                Error(string.Join("；", missing));
                return;
            }
            write(string.IsNullOrWhiteSpace(value) || value == defaultTemplate ? null : value);
            ClearInvalid(box);
            Changed();
        };
        var reset = Command("恢复默认", () => { write(null); box.Text = defaultTemplate; ClearInvalid(box); Changed(); }, "Button.Subtle");
        page.Children.Add(StackTile(label, box, reset));
    }

    private StackPanel ModelsPage()
    {
        var page = Page("模型");
        _profilePicker = new ComboBox { DisplayMemberPath = nameof(Profile.Name), Width = 240 };
        _profilePicker.SelectionChanged += (_, _) => RebuildProfileEditor();
        var delete = Ui.IconButton(Ui.Glyph.Delete, "删除此配置");
        delete.Click += (_, _) =>
        {
            if (_profilePicker.SelectedItem is not Profile selected) return;
            if (MessageBox.Show(this, $"删除模型配置“{selected.Name}”？", "删除配置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _settings.Profiles.Remove(selected);
            if (_settings.Translation.ActiveProfileId == selected.Id) _settings.Translation.ActiveProfileId = _settings.Profiles.FirstOrDefault()?.Id ?? "";
            if (_settings.Translation.ImageProfileId == selected.Id) _settings.Translation.ImageProfileId = null;
            if (_settings.ImageTranslate.AiProfileId == selected.Id) _settings.ImageTranslate.AiProfileId = null;
            RefreshProfiles();
            Changed();
        };
        page.Children.Add(Group("配置"));
        page.Children.Add(Tile("当前配置", Ui.Row(_profilePicker, delete)));
        var preset = new ComboBox { ItemsSource = ProfilePresets.Names, SelectedIndex = 0, Width = 160, MaxDropDownHeight = 360 };
        var add = Command("添加", () =>
        {
            var profile = ProfilePresets.Create(preset.SelectedItem as string ?? "Ollama");
            profile.Id = Guid.NewGuid().ToString("N");
            _settings.Profiles.Add(profile);
            RefreshProfiles(profile);
            Changed();
        }, "Button.Accent");
        var custom = Command("自定义", () =>
        {
            var profile = new Profile();
            _settings.Profiles.Add(profile);
            RefreshProfiles(profile);
            Changed();
        });
        page.Children.Add(Tile("新建", Ui.Row(preset, add, custom)));
        _profileEditor = new StackPanel();
        page.Children.Add(_profileEditor);
        RefreshProfiles(_settings.Profiles.FirstOrDefault(p => p.Id == _settings.Translation.ActiveProfileId));
        return page;
    }

    private void RefreshProfiles(Profile? selected = null)
    {
        if (_profilePicker is null) return;
        _profilePicker.ItemsSource = null;
        _profilePicker.ItemsSource = _settings.Profiles;
        _profilePicker.SelectedItem = selected ?? _settings.Profiles.FirstOrDefault();
        RebuildProfileEditor();
    }

    private void RebuildProfileEditor()
    {
        if (_profileEditor is null) return;
        _profileEditor.Children.Clear();
        if (_profilePicker?.SelectedItem is not Profile p)
        {
            _profileEditor.Children.Add(Note("还没有模型配置。"));
            return;
        }
        var panel = _profileEditor;
        panel.Children.Add(Group("接口"));
        panel.Children.Add(Input("名称", p.Name, v => { p.Name = Required(v); _profilePicker.Items.Refresh(); }));
        panel.Children.Add(Choice("类型", p.Kind, [("OpenAI 兼容", "openai-compatible"), ("Anthropic 原生", "anthropic")], v => { p.Kind = v; Dispatcher.BeginInvoke(RebuildProfileEditor); }));
        panel.Children.Add(Input("Base URL", p.BaseUrl, v =>
        {
            if (!Uri.TryCreate(v, UriKind.Absolute, out var url) || (url.Scheme != "http" && url.Scheme != "https") || !string.IsNullOrEmpty(url.UserInfo)) throw new ArgumentException("接口地址必须使用 http:// 或 https://");
            p.BaseUrl = v.TrimEnd('/');
        }, "不含 /chat/completions", width: 320));
        panel.Children.Add(Input("模型名", p.Model, v => p.Model = Required(v)));
        AddApiKey(panel, p);

        panel.Children.Add(Group("能力"));
        panel.Children.Add(Toggle("图像输入", p.SupportsVision, v => p.SupportsVision = v, "可直接翻译截图"));
        panel.Children.Add(Toggle("图像生成", p.SupportsImageGeneration, v => p.SupportsImageGeneration = v, "文生图或图像编辑模型，用于 AI 重绘"));

        panel.Children.Add(Group("用途"));
        bool floatingDefault = _settings.Translation.ActiveProfileId == p.Id;
        bool imageDefault = _settings.Translation.ImageProfileId == p.Id;
        panel.Children.Add(Tile("悬浮翻译", floatingDefault ? Badge("默认") : Command("设为默认", () => { _settings.Translation.ActiveProfileId = p.Id; Changed(); RebuildProfileEditor(); }),
            floatingDefault ? null : "当前：" + ProfileName(_settings.Translation.ActiveProfileId)));
        var imageActions = new List<UIElement>();
        if (imageDefault) imageActions.Add(Badge("默认"));
        else imageActions.Add(Command("设为默认", () => { _settings.Translation.ImageProfileId = p.Id; Changed(); RebuildProfileEditor(); }));
        if (_settings.Translation.ImageProfileId is not null)
            imageActions.Add(Command("跟随悬浮翻译", () => { _settings.Translation.ImageProfileId = null; Changed(); RebuildProfileEditor(); }, "Button.Subtle"));
        panel.Children.Add(Tile("译图", Ui.Row(imageActions.ToArray()),
            imageDefault ? null : "当前：" + (_settings.Translation.ImageProfileId is null ? "跟随悬浮翻译" : ProfileName(_settings.Translation.ImageProfileId))));

        panel.Children.Add(Group("连接测试"));
        var result = Ui.Muted("");
        result.Visibility = Visibility.Collapsed;
        var textTest = Command("测试文字", () => { });
        var visionTest = Command("测试图像", () => { });
        textTest.Click += async (_, _) => await TestAsync(false);
        visionTest.Click += async (_, _) => await TestAsync(true);
        panel.Children.Add(TileWith("连接", result, Ui.Row(textTest, visionTest)));
        async Task TestAsync(bool image)
        {
            textTest.IsEnabled = visionTest.IsEnabled = false;
            Ui.Paint(result, "Brush.TextMuted");
            result.Visibility = Visibility.Visible;
            result.Text = "正在测试…";
            try
            {
                Flush();
                result.Text = await _testConnection(p, image, _lifetime.Token);
            }
            catch (OperationCanceledException) { result.Text = "已取消"; }
            catch { result.Text = "连接失败，请检查地址、模型名、Key 和网络。"; Ui.Paint(result, "Brush.Danger"); }
            finally { textTest.IsEnabled = visionTest.IsEnabled = true; }
        }

        panel.Children.Add(Group("高级参数"));
        panel.Children.Add(Choice("并发请求数", p.MaxConcurrency?.ToString(CultureInfo.InvariantCulture) ?? "",
            [("自动", ""), ("1", "1"), ("2", "2"), ("4", "4"), ("8", "8"), ("16", "16")],
            v => p.MaxConcurrency = v.Length == 0 ? null : int.Parse(v, CultureInfo.InvariantCulture), "译图分批翻译时同时发出的请求数"));
        panel.Children.Add(Choice("结构化输出", p.StructuredOutput, [("仅提示词约束", "none"), ("JSON 对象", "json_object"), ("JSON Schema", "json_schema")], v => p.StructuredOutput = v));
        panel.Children.Add(Input("Temperature", p.Temperature?.ToString(CultureInfo.InvariantCulture) ?? "", v => p.Temperature = string.IsNullOrWhiteSpace(v) ? null : ParseDouble(v, 0, 2), placeholder: "默认", width: 120));
        panel.Children.Add(Number("最大输出 Token", p.MaxOutputTokens, 1, 1000000, v => p.MaxOutputTokens = (int)v));
        panel.Children.Add(Number("超时", p.TimeoutSeconds, 1, 3600, v => p.TimeoutSeconds = (int)v, "秒"));
        if (p.Kind == "anthropic")
        {
            panel.Children.Add(Choice("认证头", p.AuthHeader, [("Authorization: Bearer", "bearer"), ("x-api-key", "x-api-key")], v => p.AuthHeader = v));
            panel.Children.Add(Input("API 版本", p.AnthropicVersion, v => p.AnthropicVersion = Required(v), width: 160));
        }
        else
        {
            panel.Children.Add(Choice("输出上限字段", p.MaxTokensField, [("max_tokens", "max_tokens"), ("max_completion_tokens", "max_completion_tokens")], v => p.MaxTokensField = v));
            panel.Children.Add(Choice("图片 detail", p.ImageDetail ?? "", [("不发送", ""), ("auto", "auto"), ("low", "low"), ("high", "high")], v => p.ImageDetail = string.IsNullOrEmpty(v) ? null : v));
        }
        panel.Children.Add(Input("额外请求参数", p.ExtraBody.ToJsonString(), v => p.ExtraBody = JsonNode.Parse(v) as JsonObject ?? throw new ArgumentException("额外参数必须是 JSON 对象"), placeholder: "{\"thinking\":{\"type\":\"disabled\"}}", multiline: true));
    }

    private string ProfileName(string id) => _settings.Profiles.FirstOrDefault(p => p.Id == id)?.Name ?? "未选择";

    private void AddApiKey(StackPanel panel, Profile profile)
    {
        var password = new PasswordBox();
        var plain = new TextBox { Visibility = Visibility.Collapsed };
        var note = Ui.Muted(string.IsNullOrEmpty(profile.ApiKeyProtected) ? "未保存" : "已加密保存");
        bool updating = false;
        password.PasswordChanged += (_, _) =>
        {
            if (updating || string.IsNullOrEmpty(password.Password)) return;
            try { profile.ApiKeyProtected = _secrets.Protect(password.Password); note.Text = "已加密保存"; Changed(); }
            catch { Error("无法加密 Key，请检查当前 Windows 用户账户。"); }
        };
        plain.LostKeyboardFocus += (_, _) =>
        {
            if (plain.Visibility != Visibility.Visible) return;
            try { profile.ApiKeyProtected = string.IsNullOrEmpty(plain.Text) ? "" : _secrets.Protect(plain.Text); Changed(); }
            catch { Error("无法保存 Key，请重新输入。"); }
        };
        var show = Ui.IconButton(Ui.Glyph.View, "显示");
        show.Click += (_, _) =>
        {
            if (plain.Visibility == Visibility.Visible)
            {
                plain.Text = ""; plain.Visibility = Visibility.Collapsed; password.Visibility = Visibility.Visible; show.ToolTip = "显示";
                return;
            }
            try
            {
                plain.Text = string.IsNullOrEmpty(profile.ApiKeyProtected) ? "" : _secrets.Unprotect(profile.ApiKeyProtected);
                plain.Visibility = Visibility.Visible; password.Visibility = Visibility.Collapsed; show.ToolTip = "隐藏";
            }
            catch { Error("无法解密此 Key，它可能来自其他电脑或用户，请重新输入。"); }
        };
        var clear = Ui.IconButton(Ui.Glyph.Delete, "清除");
        clear.Click += (_, _) => { profile.ApiKeyProtected = ""; updating = true; password.Password = ""; updating = false; plain.Text = ""; note.Text = "未保存"; Changed(); };
        var field = new Grid { Width = 320 };
        field.Children.Add(password);
        field.Children.Add(plain);
        panel.Children.Add(TileWith("API Key", note, Ui.Row(field, show, clear)));
    }

    private StackPanel OcrPage()
    {
        var page = Page("文字识别");
        AddPaddleControls(page);

        page.Children.Add(Group("Windows OCR"));
        var languages = new List<(string, string)> { ("跟随系统", "auto") };
        var tags = _ocr.InstalledLanguages.Concat(["en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "fr-FR", "de-DE", "es-ES"])
            .Concat(OcrModelCatalog.Presets.SelectMany(preset => preset.Languages)).Append(_settings.Ocr.Language)
            .Where(tag => tag != "auto").Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string tag in tags) languages.Add((LanguageCatalog.Describe(tag), tag));
        page.Children.Add(Choice("识别语言", _settings.Ocr.Language, languages.ToArray(), v => _settings.Ocr.Language = v));
        page.Children.Add(Choice("小字放大", _settings.Ocr.Scale, [("自动", "auto"), ("不放大", "1"), ("2 倍", "2")], v => _settings.Ocr.Scale = v));
        var availability = Ui.Muted("");
        void Check()
        {
            try
            {
                var state = _ocr.CheckAvailability(_settings.Ocr.Language == "auto" ? null : _settings.Ocr.Language);
                availability.Text = state.IsAvailable ? "可用" : state.Reason;
                Ui.Paint(availability, state.IsAvailable ? "Brush.Success" : "Brush.Danger");
            }
            catch { availability.Text = "无法检测"; Ui.Paint(availability, "Brush.Danger"); }
        }
        page.Children.Add(TileWith("当前语言", availability, Command("重新检测", Check)));
        Check();
        const string instructions = "Get-WindowsCapability -Online | Where-Object { $_.Name -Like 'Language.OCR*zh-CN*' } | Add-WindowsCapability -Online";
        var script = new TextBox { Text = instructions, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(10, 8, 10, 8), FontSize = 12 };
        script.SetResourceReference(TextBox.FontFamilyProperty, "Font.Mono");
        page.Children.Add(StackTile("安装语言包", script,
            Command("复制", () => { System.Windows.Clipboard.SetText(instructions); SetStatus("已复制。"); }, "Button.Subtle"),
            "在管理员 PowerShell 中运行；其他语言替换 zh-CN"));
        return page;
    }

    private StackPanel ImagePage()
    {
        var page = Page("译图");
        page.Children.Add(Group("处理"));
        // AI redraw appears only while a profile that can generate images is selected for it.
        if (ImageGeneration.IsAvailable(_settings))
            page.Children.Add(Choice("方式", _settings.ImageTranslate.Engine, [("原位排版", "standard"), ("AI 重绘", "ai")], v => _settings.ImageTranslate.Engine = v));
        page.Children.Add(Choice("擦除原文", _settings.ImageTranslate.EraseMode, [("自动", "auto"), ("纯色", "solid"), ("渐变", "gradient"), ("背景标签", "label"), ("修复", "inpaint")], v => _settings.ImageTranslate.EraseMode = v));
        page.Children.Add(Toggle("复杂背景修复", _settings.ImageTranslate.EnableInpaint, v => _settings.ImageTranslate.EnableInpaint = v, "纹理或照片背景上更自然，但更慢"));

        page.Children.Add(Group("输出"));
        page.Children.Add(Toggle("自动复制", _settings.ImageTranslate.AutoCopy, v => _settings.ImageTranslate.AutoCopy = v));
        page.Children.Add(Toggle("自动保存", _settings.ImageTranslate.AutoSave, v => _settings.ImageTranslate.AutoSave = v));
        var folder = Ui.IconButton(Ui.Glyph.Folder, "打开");
        folder.Click += (_, _) => Try(() => OpenFolder(Environment.ExpandEnvironmentVariables(_settings.ImageTranslate.SaveFolder)));
        page.Children.Add(Input("保存目录", _settings.ImageTranslate.SaveFolder, v => _settings.ImageTranslate.SaveFolder = Required(v), width: 300, extra: folder));
        page.Children.Add(Input("文件名", _settings.ImageTranslate.FileNamePattern, v =>
        {
            if (v.Contains('/') || v.Contains('\\') || !v.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("文件名不能含目录，且必须以 .png 结尾");
            _settings.ImageTranslate.FileNamePattern = Required(v);
        }, width: 300));

        var capable = _settings.Profiles.Where(p => p.SupportsImageGeneration).ToArray();
        if (capable.Length > 0)
        {
            page.Children.Add(Group("AI 重绘"));
            var choices = new List<(string, string)> { ("跟随译图模型", "") };
            choices.AddRange(capable.Select(p => (p.Name, p.Id)));
            string current = capable.Any(p => p.Id == _settings.ImageTranslate.AiProfileId) ? _settings.ImageTranslate.AiProfileId! : "";
            page.Children.Add(Choice("模型", current, choices, v =>
            {
                _settings.ImageTranslate.AiProfileId = v.Length == 0 ? null : v;
                Dispatcher.BeginInvoke(() => ShowPage(rebuild: true));
            }));
            page.Children.Add(Choice("接口", _settings.ImageTranslate.AiProvider, [("OpenAI Images", "openai"), ("Gemini", "gemini")], v => _settings.ImageTranslate.AiProvider = v));
        }

        page.Children.Add(Group("调试"));
        page.Children.Add(Toggle("导出中间结果", _settings.ImageTranslate.DebugDump, v =>
        {
            if (v && MessageBox.Show(this, "调试导出会把截图、识别文字和译文保存到本机。是否开启？", "调试导出", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) throw new ArgumentException("已取消开启调试导出");
            _settings.ImageTranslate.DebugDump = v;
        }));
        return page;
    }

    private StackPanel AdvancedPage()
    {
        var page = Page("高级");
        page.Children.Add(Group("网络"));
        page.Children.Add(Choice("代理", _settings.Network.Proxy, [("系统代理", "system"), ("不使用", "none"), ("自定义", "custom")], v => _settings.Network.Proxy = v));
        page.Children.Add(Input("自定义代理", _settings.Network.CustomProxyUrl ?? "", v =>
        {
            if (string.IsNullOrWhiteSpace(v)) { _settings.Network.CustomProxyUrl = null; return; }
            if (!Uri.TryCreate(v, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "socks5")) throw new ArgumentException("代理地址需使用 http:// 或 socks5://");
            _settings.Network.CustomProxyUrl = v;
        }, placeholder: "http://127.0.0.1:7890", width: 260));
        AddHistoryControls(page);
        AddUpdateControls(page);
        AddStorageAndTransfer(page);

        page.Children.Add(Group("诊断"));
        page.Children.Add(Toggle("详细日志", _settings.General.DebugLogging, v => _settings.General.DebugLogging = v));
        page.Children.Add(Tile("打开目录", Ui.Row(
            Command("日志", () => OpenFolder(StoragePaths.LogFolder), "Button.Subtle"),
            Command("配置", () => OpenFolder(StoragePaths.SettingsFolder), "Button.Subtle"),
            Command("调试导出", () => OpenFolder(StoragePaths.DebugFolder), "Button.Subtle"))));

        page.Children.Add(Group("关于"));
        page.Children.Add(Tile("屏译 SnapTrans " + AppVersion, Command("发布页", () => OpenUrl(ReleasesUrl), "Button.Subtle")));
        return page;
    }

    // ───────────────────────────── Sections ─────────────────────────────

    private void AddGlossary(StackPanel page)
    {
        var rows = new ObservableCollection<GlossaryRow>(_settings.Translation.Glossary.Select(g => new GlossaryRow { Source = g.Source, Target = g.Target }));
        var grid = new DataGrid
        {
            ItemsSource = rows, AutoGenerateColumns = false, CanUserAddRows = false,
            CanUserDeleteRows = false, MinHeight = 110, MaxHeight = 260
        };
        grid.Columns.Add(new DataGridTextColumn { Header = "原词", Binding = new Binding(nameof(GlossaryRow.Source)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "译法", Binding = new Binding(nameof(GlossaryRow.Target)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        void Sync()
        {
            if (rows.Any(row => string.IsNullOrWhiteSpace(row.Source) || string.IsNullOrWhiteSpace(row.Target)))
            { Error("术语表有空项，请补全或删除该行。"); return; }
            if (rows.Select(row => row.Source.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
            { Error("术语表原词不能重复。"); return; }
            _settings.Translation.Glossary = rows.Select(row => new GlossaryEntry(row.Source.Trim(), row.Target.Trim())).ToList();
            Changed();
        }
        grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(Sync);
        var add = Ui.IconButton(Ui.Glyph.Add, "添加");
        add.Click += (_, _) => { var row = new GlossaryRow(); rows.Add(row); grid.SelectedItem = row; grid.ScrollIntoView(row); grid.Focus(); };
        var remove = Ui.IconButton(Ui.Glyph.Delete, "删除选中项");
        remove.Click += (_, _) => { if (grid.SelectedItem is GlossaryRow row) { rows.Remove(row); Sync(); } };
        page.Children.Add(StackTile("固定译法", grid, Ui.Row(add, remove)));
    }

    private void AddCache(StackPanel page)
    {
        page.Children.Add(Toggle("缓存译文", _settings.Cache.Enabled, v => _settings.Cache.Enabled = v, "仅保存在内存中"));
        page.Children.Add(Number("最多条目", _settings.Cache.MaxEntries, 1, 10000, v => _settings.Cache.MaxEntries = (int)v));
        page.Children.Add(Number("有效期", _settings.Cache.TtlMinutes, 1, 1440, v => _settings.Cache.TtlMinutes = (int)v, "分钟"));
        var statistics = Ui.Muted("");
        void Refresh()
        {
            var stats = _features?.CacheStatistics?.Invoke();
            statistics.Text = stats is null ? "" : $"{stats.Count} 条 · 命中 {stats.Hits} · 未命中 {stats.Misses}";
        }
        Refresh();
        var refresh = Ui.IconButton(Ui.Glyph.Refresh, "刷新");
        refresh.Click += (_, _) => Refresh();
        var actions = new List<UIElement> { refresh };
        if (_features?.Cache is { } cache)
            actions.Add(AsyncCommand("清空", async ct => { await cache.ClearAsync(ct); Refresh(); SetStatus("缓存已清空。"); }));
        page.Children.Add(TileWith("使用情况", statistics, Ui.Row(actions.ToArray())));
    }

    private void AddPaddleControls(StackPanel page)
    {
        page.Children.Add(Group("引擎"));
        var engine = new ComboBox { ItemsSource = new[] { new ChoiceItem("Windows 内置", "windows"), new ChoiceItem("PaddleOCR", "paddle") }, DisplayMemberPath = nameof(ChoiceItem.Label), SelectedValuePath = nameof(ChoiceItem.Value), SelectedValue = _settings.Ocr.Engine, Width = 200 };
        page.Children.Add(Tile("识别引擎", engine));

        page.Children.Add(Group("PaddleOCR"));
        var presets = new ComboBox { ItemsSource = OcrModelCatalog.Presets, DisplayMemberPath = nameof(OcrModelPreset.Name), SelectedValuePath = nameof(OcrModelPreset.Id), SelectedValue = _settings.Ocr.ModelPreset, Width = 280 };
        var status = Ui.Muted("");
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var install = Ui.Button("一键安装", "Button.Accent", Ui.Glyph.Download);
        var cancel = Ui.IconButton(Ui.Glyph.Close, "取消");
        cancel.Visibility = Visibility.Collapsed;
        cancel.Click += (_, _) => _paddleSetup?.Cancel();
        bool selecting = false;
        string SelectedPreset() => presets.SelectedValue as string ?? _settings.Ocr.ModelPreset;
        bool Ready()
        {
            try { return _features?.IsPaddlePluginAvailable?.Invoke() == true && _features.GetOcrModels?.Invoke().HasAllFiles(SelectedPreset()) == true; }
            catch { return false; }
        }
        void SetEngine(string value) { selecting = true; engine.SelectedValue = value; selecting = false; }
        void RefreshStatus()
        {
            bool ready = Ready(), active = ready && _settings.Ocr.Engine == "paddle";
            status.Text = active ? "已启用" : ready ? "已安装" : _features?.IsPaddlePluginAvailable?.Invoke() == true ? "模型未下载" : "未安装";
            Ui.Paint(status, ready ? "Brush.Success" : "Brush.TextMuted");
            install.Content = ready ? "启用" : Ui.WithIcon(Ui.Glyph.Download, "一键安装");
            install.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        }
        install.Click += async (_, _) =>
        {
            if (_features?.SetupPaddleAsync is not { } setup || _paddleSetup is not null) return;
            using var run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _paddleSetup = run;
            install.IsEnabled = presets.IsEnabled = engine.IsEnabled = false;
            cancel.Visibility = progress.Visibility = Visibility.Visible;
            progress.Value = 0;
            Ui.Paint(status, "Brush.TextMuted");
            try
            {
                string message = await setup(SelectedPreset(), new Progress<SnapTrans.App.Services.PaddleSetupProgress>(step =>
                {
                    status.Text = step.Message;
                    if (step.Percent is { } percent) progress.Value = percent;
                }), run.Token);
                SetEngine(_settings.Ocr.Engine);
                Changed();
                SetStatus(message);
            }
            catch (OperationCanceledException) { SetStatus("已取消安装。"); }
            catch (InvalidOperationException e) { Error(e.Message); }
            catch (InvalidDataException e) { Error(e.Message); }
            catch { Error("安装失败，请检查网络后重试。"); }
            finally
            {
                _paddleSetup = null;
                install.IsEnabled = presets.IsEnabled = engine.IsEnabled = true;
                cancel.Visibility = progress.Visibility = Visibility.Collapsed;
                RefreshStatus();
            }
        };
        engine.SelectionChanged += (_, _) =>
        {
            if (selecting || engine.SelectedValue is not string value) return;
            if (value == "paddle" && !Ready())
            {
                SetEngine(_settings.Ocr.Engine);
                Error("PaddleOCR 尚未安装，请先点击“一键安装”。");
                return;
            }
            _settings.Ocr.Engine = value;
            Changed();
            RefreshStatus();
        };
        presets.SelectionChanged += (_, _) =>
        {
            if (presets.SelectedValue is not string value) return;
            _settings.Ocr.ModelPreset = value;
            // An engine must never point at a model that is not on disk.
            if (_settings.Ocr.Engine == "paddle" && !Ready()) { _settings.Ocr.Engine = "windows"; SetEngine("windows"); }
            Changed();
            RefreshStatus();
        };
        var body = new StackPanel();
        body.Children.Add(Ui.Row(presets, install, cancel));
        body.Children.Add(progress);
        page.Children.Add(StackTile("模型", body, status));
        if (_features?.InstallPaddleFromFileAsync is { } fromFile)
        {
            page.Children.Add(Tile("离线安装", AsyncCommand("选择组件 ZIP…", async ct =>
            {
                SetStatus(await fromFile(ct));
                RefreshStatus();
            })));
        }
        page.Children.Add(Input("模型目录", _settings.Ocr.ModelFolder ?? "", value =>
        {
            string? folder = string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(value));
            if (string.Equals(_settings.Ocr.ModelFolder, folder, StringComparison.OrdinalIgnoreCase)) return;
            _settings.Ocr.ModelFolder = folder;
            if (_settings.Ocr.Engine == "paddle" && !Ready()) { _settings.Ocr.Engine = "windows"; SetEngine("windows"); }
            RefreshStatus();
        }, placeholder: "默认", width: 280));
        RefreshStatus();
    }

    private void AddHistoryControls(StackPanel page)
    {
        page.Children.Add(Group("翻译历史"));
        page.Children.Add(Toggle("保存历史", _settings.History.Enabled, value =>
        {
            if (value && MessageBox.Show(this, "历史会把原文和译文保存在本机，不含截图。是否开启？", "保存翻译历史", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                throw new ArgumentException("用户取消");
            _settings.History.Enabled = value;
        }, "只保存文字"));
        page.Children.Add(Number("最多保留", _settings.History.Limit, 1, 5000, value => _settings.History.Limit = (int)value, "条"));
        if (_features?.History is { } history)
        {
            page.Children.Add(Tile("已保存的记录", Ui.Row(
                Command("查看", () => new HistoryWindow(history) { Owner = this }.Show()),
                AsyncCommand("清空", async ct =>
                {
                    if (MessageBox.Show(this, "清空所有翻译历史？此操作无法撤销。", "清空历史", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    await history.ClearAsync(ct); SetStatus("历史已清空。");
                }, "Button.Danger"))));
        }
    }

    private SettingsPaths StoragePaths => _features?.StoragePaths ?? SettingsPaths.Resolve(AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private void AddStorageAndTransfer(StackPanel page)
    {
        page.Children.Add(Group("数据"));
        page.Children.Add(Tile("存储位置", AsyncCommand("切换…", async ct =>
        {
            string marker = Path.Combine(AppContext.BaseDirectory, "portable.txt");
            bool portable = !File.Exists(marker);
            string description = portable ? "下次启动将改为便携模式：配置和数据保存在程序目录。" : "下次启动将改为使用当前用户的 AppData 目录。";
            if (MessageBox.Show(this, description + "\n\n现有配置、Key 和历史不会自动迁移，建议先导出配置。是否继续？", "切换存储位置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (portable)
            {
                await using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                await stream.FlushAsync(ct);
            }
            else { ct.ThrowIfCancellationRequested(); File.Delete(marker); }
            SetStatus("存储位置将在下次启动时切换。");
        }), (StoragePaths.IsPortable ? "便携模式 · " : "") + StoragePaths.SettingsFolder));
        var transfer = new List<UIElement>
        {
            AsyncCommand("导出…", async ct =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出屏译配置", Filter = "JSON 配置|*.json", FileName = "SnapTrans-settings.json", DefaultExt = ".json" };
                if (dialog.ShowDialog(this) != true) return;
                System.Windows.Input.Keyboard.ClearFocus(); Flush();
                await ConfigurationTransfer.ExportAsync(_settings, dialog.FileName, ct);
                SetStatus("配置已导出。");
            })
        };
        if (_features?.ApplyImportedSettingsAsync is { } apply)
        {
            transfer.Add(AsyncCommand("导入…", async ct =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入屏译配置", Filter = "JSON 配置|*.json", CheckFileExists = true };
                if (dialog.ShowDialog(this) != true) return;
                var imported = await ConfigurationTransfer.ImportAsync(dialog.FileName, ct);
                string preview =
                    $"模型配置：{imported.Profiles.Count} 个\n" +
                    $"目标语言：{LanguageCatalog.Describe(imported.Translation.TargetLanguage)}\n" +
                    $"术语：{imported.Translation.Glossary.Count} 项\n" +
                    $"快捷键：{imported.Hotkeys.TranslateFloating} / {imported.Hotkeys.TranslateImage}\n" +
                    $"保存历史：{(imported.History.Enabled ? "开" : "关")}　开机自启：{(imported.General.LaunchAtStartup ? "开" : "关")}　检查更新：{(imported.Updates.AutoCheck ? "开" : "关")}\n" +
                    $"更新源：{imported.Updates.FeedUrl}\n\n" +
                    "将替换当前全部设置，Key 和额外参数需重新填写。是否继续？";
                if (MessageBox.Show(this, preview, "导入配置", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _saveTimer.Stop(); _dirty = false;
                await apply(imported, ct);
                Close();
            }));
        }
        page.Children.Add(Tile("配置迁移", Ui.Row(transfer.ToArray()), "导出文件不含 API Key"));
    }

    private void AddUpdateControls(StackPanel page)
    {
        page.Children.Add(Group("更新"));
        page.Children.Add(Toggle("启动时检查更新", _settings.Updates.AutoCheck, value => _settings.Updates.AutoCheck = value));
        page.Children.Add(Toggle("包含预发布版本", _settings.Updates.IncludePrerelease, value => _settings.Updates.IncludePrerelease = value));
        page.Children.Add(Input("更新源", _settings.Updates.FeedUrl, value =>
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("更新源必须是不含凭证、查询参数或片段的 HTTPS 地址");
            _settings.Updates.FeedUrl = value;
        }, width: 300));
        var result = Ui.Muted("");
        result.Visibility = Visibility.Collapsed;
        void Show(string text) { result.Text = text; result.Visibility = Visibility.Visible; }
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var commands = new List<UIElement>();
        if (_features?.CheckForUpdatesAsync is { } check)
            commands.Add(AsyncCommand("检查", async ct => { Show("正在检查…"); Show(await check(ct)); }));
        if (_features?.InstallUpdateAsync is { } install)
        {
            commands.Add(AsyncCommand("安装", async ct =>
            {
                if (_features.HasAvailableUpdate?.Invoke() != true) { Show("请先检查更新。"); return; }
                if (MessageBox.Show(this, "下载后会重启屏译，进行中的翻译会停止。是否继续？", "安装更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                progress.Visibility = Visibility.Visible;
                try { Show(await install(new Progress<int>(value => { progress.Value = value; Show($"正在下载：{value}%"); }), ct)); }
                finally { progress.Visibility = Visibility.Collapsed; }
            }, "Button.Accent"));
        }
        var description = new StackPanel();
        description.Children.Add(result);
        description.Children.Add(progress);
        page.Children.Add(TileWith("软件更新", description, Ui.Row(commands.ToArray())));
    }

    // ───────────────────────────── Building blocks ─────────────────────────────

    private static StackPanel Page(string title)
    {
        var panel = new StackPanel { MaxWidth = 820, Margin = new Thickness(36, 28, 36, 36) };
        panel.Children.Add(Ui.Text(title, 26, FontWeights.SemiBold));
        return panel;
    }

    private static TextBlock Group(string title)
    {
        var text = Ui.Text(title, 14, FontWeights.SemiBold);
        text.Margin = new Thickness(2, 26, 0, 8);
        return text;
    }

    private static TextBlock Note(string text)
    {
        var block = Ui.Muted(text);
        block.Margin = new Thickness(2, 4, 0, 6);
        return block;
    }

    private static Border Badge(string text)
    {
        var label = Ui.Text(text, 12, FontWeights.SemiBold, "Brush.Accent", wrap: false);
        var badge = new Border { Child = label, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center };
        badge.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        return badge;
    }

    /// <summary>One setting: title (and an optional short note) on the left, the control on the right.</summary>
    private static Border Tile(string title, UIElement? control = null, string? note = null)
        => TileWith(title, note is null ? null : Ui.Muted(note), control);

    /// <summary>Like <see cref="Tile"/>, with a caller-owned element under the title, such as a live status line.</summary>
    private static Border TileWith(string title, UIElement? description, UIElement? control)
    {
        var card = Ui.Card(TileHeader(title, description, control));
        card.Margin = new Thickness(0, 0, 0, 4);
        return card;
    }

    private static Grid TileHeader(string title, UIElement? description, UIElement? control)
    {
        var grid = new Grid { MinHeight = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 140 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        text.Children.Add(Ui.Text(title));
        if (description is FrameworkElement element)
        {
            element.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(element);
        }
        grid.Children.Add(text);
        if (control is FrameworkElement right)
        {
            right.VerticalAlignment = VerticalAlignment.Center;
            right.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);
        }
        return grid;
    }

    /// <summary>A setting whose control needs the full width, such as a list or a multi-line editor.</summary>
    private static Border StackTile(string title, UIElement content, UIElement? actions = null, string? note = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(TileHeader(title, note is null ? null : Ui.Muted(note), actions));
        if (content is FrameworkElement element) element.Margin = new Thickness(0, 12, 0, 2);
        panel.Children.Add(content);
        var card = Ui.Card(panel);
        card.Margin = new Thickness(0, 0, 0, 4);
        return card;
    }

    private Border Input(string label, string value, Action<string> update, string? note = null, string? placeholder = null, bool multiline = false, double width = 240, UIElement? extra = null)
    {
        var text = new TextBox { Text = value, Tag = placeholder, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, AcceptsReturn = multiline };
        if (multiline)
        {
            text.MinHeight = 80; text.VerticalContentAlignment = VerticalAlignment.Top; text.Padding = new Thickness(8, 6, 8, 6);
            text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            text.SetResourceReference(TextBox.FontFamilyProperty, "Font.Mono");
        }
        else text.Width = width;
        text.LostKeyboardFocus += (_, _) =>
        {
            try { update(text.Text.Trim()); ClearInvalid(text); Changed(); }
            catch (Exception ex) when (ex is ArgumentException or FormatException or System.Text.Json.JsonException)
            {
                MarkInvalid(text);
                Error(label + "格式不正确。");
            }
        };
        if (multiline) return StackTile(label, text, note: note);
        return Tile(label, extra is null ? text : Ui.Row(text, extra), note);
    }

    private Border Number(string label, double value, double min, double max, Action<double> update, string? note = null)
        => Input(label, value.ToString(CultureInfo.InvariantCulture), text => update(ParseDouble(text, min, max)), note, width: 100);

    private Border Toggle(string label, bool value, Action<bool> update, string? note = null)
    {
        var box = new CheckBox { IsChecked = value, ToolTip = label };
        box.Click += (_, _) =>
        {
            try { update(box.IsChecked == true); Changed(); }
            catch { box.IsChecked = !(box.IsChecked == true); Error("此修改未应用。"); }
        };
        var tile = Tile(label, box, note);
        tile.Cursor = System.Windows.Input.Cursors.Hand;
        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsWithin(source, box)) return;
            box.IsChecked = box.IsChecked != true;
            box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, box));
        };
        return tile;
    }

    /// <summary>A searchable drop-down for long lists such as languages and fonts.</summary>
    private Border Picker(string label, IEnumerable<PickerItem> items, string value, Action<string> update, string? note = null)
    {
        var picker = new SearchPicker(items, value) { Width = 240 };
        picker.ValueChanged += selected =>
        {
            try { update(selected); Changed(); }
            catch { Error("无法应用此选项。"); }
        };
        return Tile(label, picker, note);
    }

    private static bool IsWithin(DependencyObject source, DependencyObject ancestor)
    {
        for (var node = source; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node == ancestor) return true;
        return false;
    }

    private Border Choice(string label, string value, IReadOnlyList<(string Label, string Value)> choices, Action<string> update, string? note = null)
    {
        // ValueTuple exposes fields, so present stable wrapper records for WPF property binding.
        var combo = new ComboBox
        {
            ItemsSource = choices.Select(c => new ChoiceItem(c.Label, c.Value)).ToArray(),
            DisplayMemberPath = nameof(ChoiceItem.Label),
            SelectedValuePath = nameof(ChoiceItem.Value),
            MinWidth = 200, MaxDropDownHeight = 360
        };
        combo.SelectedValue = value;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedValue is not string selected) return;
            try { update(selected); Changed(); }
            catch { Error("无法应用此选项。"); }
        };
        return Tile(label, combo, note);
    }

    private Button Command(string text, Action action, string? style = null)
    {
        var button = Ui.Button(text, style);
        button.Click += (_, _) => Try(action);
        return button;
    }

    private Button AsyncCommand(string text, Func<CancellationToken, Task> action, string? style = null)
    {
        var button = Ui.Button(text, style);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await action(_lifetime.Token); }
            catch (OperationCanceledException) { SetStatus("操作已取消。"); }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            { Error("操作失败：文件或输入未通过校验。"); }
            catch { Error("操作失败，请检查网络、组件和文件权限后重试。"); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private void Try(Action action)
    {
        try { action(); }
        catch { Error("操作失败，请检查设置或目录权限。"); }
    }

    private static void MarkInvalid(Control control) => control.SetResourceReference(Control.BorderBrushProperty, "Brush.Danger");
    private static void ClearInvalid(Control control) => control.ClearValue(Control.BorderBrushProperty);

    private void SetStatus(string message) => ShowStatus(message, Ui.Glyph.Info, "Brush.TextMuted");
    private void Error(string message) => ShowStatus(message, "", "Brush.Danger");

    private void ShowStatus(string message, string glyph, string brush)
    {
        _status.Text = message;
        _statusIcon.Text = glyph;
        Ui.Paint(_status, brush);
        Ui.Paint(_statusIcon, brush);
    }

    private static string AppVersion => typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(SettingsWindow).Assembly.GetName().Version?.ToString() ?? "";

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private sealed class GlossaryRow { public string Source { get; set; } = ""; public string Target { get; set; } = ""; }

    private void Changed(bool notify = true)
    {
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        ShowStatus("正在保存…", "", "Brush.TextMuted");
        if (notify)
        {
            try { _settingsChanged?.Invoke(); }
            catch { Error("应用设置时出错，请检查快捷键、自启和代理设置。"); }
        }
    }

    private void Flush()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        try
        {
            var errors = SettingsValidator.Validate(_settings);
            if (errors.Count > 0) { Error("未保存：" + string.Join("；", errors)); return; }
            _save();
            _dirty = false;
            ShowStatus("已保存", Ui.Glyph.Check, "Brush.Success");
        }
        catch { Error("保存失败，请检查配置目录的写入权限。"); }
    }

    private static string Required(string text) => string.IsNullOrWhiteSpace(text) ? throw new ArgumentException("值不可为空") : text;
    private static double ParseDouble(string text, double min, double max)
    {
        var value = double.Parse(text, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value) || value < min || value > max) throw new ArgumentOutOfRangeException(nameof(text));
        return value;
    }
    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
    private sealed record ChoiceItem(string Label, string Value);
}
