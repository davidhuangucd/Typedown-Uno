using Typedown.Uno.Services;
using Windows.Storage.Pickers;

namespace Typedown.Uno.Views;

public sealed partial class SettingsDialog : ContentDialog
{
    private readonly AppSettings settings;
    private bool loading = true;

    /// <summary>System first, then English and every table in <see cref="Services.LocaleTables"/>. Built
    /// fresh each time the dialog is (re)built, not as a static field: the "System" label comes from Loc.Get,
    /// which must reflect the language currently in force, and a static field froze it — and, if evaluated
    /// before the language was applied, showed it blank (the empty entry in the dropdown).</summary>
    private static (string value, string label)[] BuildLanguages() =>
        new[] { ("", Loc.Get("LangSystem")), ("en", "English") }
            .Concat(Services.LocaleTables.Names.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)))
            .ToArray();
    private static readonly string[] Indentations = { "1", "2", "4", "tab" };
    private static readonly string[] Directions = { "auto", "ltr", "rtl" };

    private readonly Dictionary<string, List<FrameworkElement>> sections = new();
    private string currentSection = "";

    public SettingsDialog(AppSettings settings)
    {
        this.settings = settings;
        this.InitializeComponent();
        Title = Loc.Get("SettingsTitle");
        CloseButtonText = Loc.Get("Close");
        Rebuild();
        // Every label in here came from the language in force when it was built, so a change of language has to
        // build them again — otherwise the dialog you changed it in is the one place still in the old language.
        settings.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) => settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.Language)) return;
        // The language is applied by the page that owns the settings; by the time this runs Loc already has it.
        DispatcherQueue.TryEnqueue(Rebuild);
    }

    private void Rebuild()
    {
        var section = Math.Max(0, Categories.SelectedIndex);
        loading = true;
        Title = Loc.Get("SettingsTitle");
        CloseButtonText = Loc.Get("Close");
        sections.Clear();
        Categories.Items.Clear();
        Container.Children.Clear();
        Build();
        foreach (var key in sections.Keys) Categories.Items.Add(Loc.Get(key));
        loading = false;
        Categories.SelectedIndex = Math.Min(section, sections.Count - 1);
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Categories.SelectedIndex < 0) return;
        currentSection = sections.Keys.ElementAt(Categories.SelectedIndex);
        Container.Children.Clear();
        foreach (var row in sections[currentSection]) Container.Children.Add(row);
        Scroller.ChangeView(null, 0, null, true);
    }

    private void Build()
    {
        Section("General");
        var languages = BuildLanguages();
        Combo("Language", languages.Select(l => l.label), Math.Max(0, Array.FindIndex(languages, l => l.value == settings.Language)),
            i => settings.Language = languages[i].value);
        // Built-in themes first, then whatever CSS files the themes folder holds (see docs/custom-theme.md).
        var builtIn = Enum.GetValues<AppTheme>();
        var custom = Services.ThemeFiles.List();
        var themeIndex = string.IsNullOrEmpty(settings.CustomTheme)
            ? (int)settings.Theme
            : builtIn.Length + Math.Max(0, custom.ToList().FindIndex(t => t.Id == settings.CustomTheme));
        Combo("Theme",
            builtIn.Select(t => Loc.Get("Theme" + t)).Concat(custom.Select(t => t.Name)),
            themeIndex,
            i =>
            {
                if (i < builtIn.Length)
                {
                    settings.CustomTheme = "";
                    settings.Theme = (AppTheme)i;
                }
                else
                {
                    // A custom theme also sets the built-in theme it builds on: one choice, not two.
                    var picked = custom[i - builtIn.Length];
                    settings.Theme = picked.Base;
                    settings.CustomTheme = picked.Id;
                }
            });
        ThemeFolderRow();
        Combo("StartupAction", Enum.GetValues<FileStartupAction>().Select(a => Loc.Get("Startup" + a)), (int)settings.FileStartupAction, i => settings.FileStartupAction = (FileStartupAction)i);
        Combo("FolderStartup", Enum.GetValues<FolderStartupAction>().Select(a => Loc.Get("FolderStartup" + a)), (int)settings.FolderStartupAction, i => settings.FolderStartupAction = (FolderStartupAction)i);
        FolderRow("StartupFolder", () => settings.StartupFolder, v => settings.StartupFolder = v);
        Toggle("OpenFilesInNewTab", settings.OpenFilesInNewTab, v => settings.OpenFilesInNewTab = v);
        Toggle("AlwaysShowTabBar", settings.AlwaysShowTabBar, v => settings.AlwaysShowTabBar = v);
        Toggle("StatusBar", settings.StatusBarOpen, v => settings.StatusBarOpen = v);
        Combo("WordCountMethod", Enum.GetValues<WordCountMethod>().Select(m => Loc.Get("Count" + m)), (int)settings.WordCountMethod, i => settings.WordCountMethod = (WordCountMethod)i);

        Section("FilesSection");
        Toggle("AutoSave", settings.AutoSave, v => settings.AutoSave = v);
        Toggle("RememberPosition", settings.RememberPosition, v => settings.RememberPosition = v);
        Toggle("AutoReload", settings.AutoReload, v => settings.AutoReload = v, "AutoReloadDescription");
        Toggle("AskBeforeReload", settings.AskBeforeReload, v => settings.AskBeforeReload = v);
        Toggle("OpenFolderAfterExport", settings.OpenFolderAfterExport, v => settings.OpenFolderAfterExport = v);

        Section("Editor");
        Number("FontSize", settings.FontSize, 8, 48, 1, v => settings.FontSize = (int)v);
        Number("LineHeight", settings.LineHeight, 1, 3, 0.1, v => settings.LineHeight = Math.Round(v, 2));
        Text("EditorWidth", settings.EditorAreaWidth, v => settings.EditorAreaWidth = v);
        Text("FontFamily", settings.FontFamily, v => settings.FontFamily = v, "FontFamilyPlaceholder");
        Number("TabSize", settings.TabSize, 1, 8, 1, v => settings.TabSize = (int)v);
        Combo("TextDirection", Directions.Select(d => Loc.Get("Dir" + d)), Math.Max(0, Array.IndexOf(Directions, settings.TextDirection)), i => settings.TextDirection = Directions[i]);
        Combo("ListIndentation", Indentations, Math.Max(0, Array.IndexOf(Indentations, settings.ListIndentation)), i => settings.ListIndentation = Indentations[i]);
        Toggle("TableAlign", settings.TableAlignColumns, v => settings.TableAlignColumns = v);
        Toggle("LooseList", settings.PreferLooseListItem, v => settings.PreferLooseListItem = v);
        Toggle("TrimCodeBlock", settings.TrimUnnecessaryCodeBlockEmptyLines, v => settings.TrimUnnecessaryCodeBlockEmptyLines = v);
        Toggle("ParagraphMarker", settings.ShowParagraphMarker, v => settings.ShowParagraphMarker = v);
        Toggle("Spellcheck", settings.SpellcheckEnabled, v => settings.SpellcheckEnabled = v);
        Toggle("AutoPairBracket", settings.AutoPairBracket, v => settings.AutoPairBracket = v);
        Toggle("AutoPairQuote", settings.AutoPairQuote, v => settings.AutoPairQuote = v);
        Toggle("AutoPairMarkdown", settings.AutoPairMarkdownSyntax, v => settings.AutoPairMarkdownSyntax = v);
        MultilineText("CustomCss", settings.CustomCss, v => settings.CustomCss = v);

        Section("ImageSection");
        Combo("ImageAction", new[] { Loc.Get("ImageCopy"), Loc.Get("ImageKeep") }, (int)settings.ImageAction, i => settings.ImageAction = (ImageInsertAction)i);
        Text("ImageCopyPath", settings.ImageCopyPath, v => settings.ImageCopyPath = v, hint: "./${filename}.assets");
        Add(new TextBlock { Text = Loc.Get("ImageCopyPathHint"), Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        Toggle("PreferRelativeImagePaths", settings.PreferRelativeImagePaths, v => settings.PreferRelativeImagePaths = v);
        Toggle("EncodeImageLinks", settings.EncodeImageLinks, v => settings.EncodeImageLinks = v);

        Section("FindSection");
        Toggle("FindCaseSensitive", settings.FindCaseSensitive, v => settings.FindCaseSensitive = v);
        Toggle("FindWholeWord", settings.FindWholeWord, v => settings.FindWholeWord = v);
        Toggle("FindRegex", settings.FindRegex, v => settings.FindRegex = v);

        Section("ShortcutSection");
        Add(new TextBlock { Text = Loc.Get("ShortcutHint"), Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        foreach (var command in Enum.GetValues<ShortcutCommand>()) ShortcutRow(command);

        Section("HedgeDoc");
        Text("Server", settings.HedgeDocServer, v => settings.HedgeDocServer = v, placeholder: null, hint: "https://md.example.com");
        Text("Email", settings.HedgeDocEmail, v => settings.HedgeDocEmail = v);
        Password("Password", settings.HedgeDocPassword, v => settings.HedgeDocPassword = v);
        Toggle("PublishReadOnly", settings.HedgeDocPublishReadOnly, v => settings.HedgeDocPublishReadOnly = v);
        TestRow();
    }

    // ---- row builders -------------------------------------------------------------------------------------

    /// <summary>Starts a category; following rows are added to it.</summary>
    private void Section(string key)
    {
        currentSection = key;
        sections[key] = new List<FrameworkElement>();
    }

    private void Add(FrameworkElement row) => sections[currentSection].Add(row);

    private void Toggle(string key, bool value, Action<bool> set, string? descriptionKey = null)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.Toggled += (_, _) => { if (!loading) set(toggle.IsOn); };
        Add(Row(key, toggle, descriptionKey));
    }

    private void Combo(string key, IEnumerable<string> items, int selected, Action<int> set)
    {
        var combo = new ComboBox { MinWidth = 200 };
        foreach (var item in items) combo.Items.Add(item);
        combo.SelectedIndex = selected;
        combo.SelectionChanged += (_, _) => { if (!loading && combo.SelectedIndex >= 0) set(combo.SelectedIndex); };
        Add(Row(key, combo));
    }

    private void Number(string key, double value, double min, double max, double step, Action<double> set)
    {
        var box = new NumberBox { Value = value, Minimum = min, Maximum = max, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 140 };
        box.ValueChanged += (_, args) => { if (!loading && !double.IsNaN(args.NewValue)) set(args.NewValue); };
        Add(Row(key, box));
    }

    private void Text(string key, string value, Action<string> set, string? placeholder = null, string? hint = null)
    {
        var box = new TextBox { Text = value, MinWidth = 220, PlaceholderText = hint ?? (placeholder != null ? Loc.Get(placeholder) : "") };
        box.TextChanged += (_, _) => { if (!loading) set(box.Text.Trim()); };
        Add(Row(key, box));
    }

    private void MultilineText(string key, string value, Action<string> set)
    {
        var box = new TextBox { Text = value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 90, MinWidth = 220 };
        box.TextChanged += (_, _) => { if (!loading) set(box.Text); };
        Add(Row(key, box, stacked: true));
    }

    private void Password(string key, string value, Action<string> set)
    {
        var box = new PasswordBox { Password = value, MinWidth = 220 };
        box.PasswordChanged += (_, _) => { if (!loading) set(box.Password); };
        Add(Row(key, box));
    }

    /// <summary>Where custom themes live, with a button that opens the folder in the file manager.</summary>
    private void ThemeFolderRow()
    {
        Services.ThemeFiles.EnsureFolder();
        var path = new TextBlock
        {
            Text = Services.ThemeFiles.Folder,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 220,
            Opacity = 0.7,
        };
        var button = new Button { Content = Loc.Get("OpenFolder") };
        button.Click += (_, _) =>
        {
            try
            {
                Services.ThemeFiles.EnsureFolder();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Services.ThemeFiles.Folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Services.Log.Error("open theme folder", ex);
            }
        };
        var document = new Button { Content = Loc.Get("ThemeDocument") };
        document.Click += (_, _) => MainPage.OpenThemeDocument();
        Add(Row("ThemeFolder", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { path, button, document } }));
    }

    private void FolderRow(string key, Func<string?> get, Action<string?> set)
    {
        var text = new TextBlock { Text = get() ?? Loc.Get("NoFolder"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 };
        var button = new Button { Content = Loc.Get("Browse") };
        button.Click += async (_, _) =>
        {
            // Same reason as in MainPage: the platform folder picker needs a desktop portal on Linux.
            string? path;
            if (OperatingSystem.IsLinux())
            {
                var dialog = new FilePickerDialog(FilePickerDialog.PickerMode.Folder, get() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) { XamlRoot = XamlRoot, RequestedTheme = RequestedTheme };
                await dialog.ShowAsync();
                path = dialog.SelectedPath;
            }
            else
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
                picker.FileTypeFilter.Add("*");
                path = (await picker.PickSingleFolderAsync())?.Path;
            }
            if (path == null) return;
            set(path);
            text.Text = path;
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { text, button } };
        Add(Row(key, panel));
    }

    /// <summary>One binding: click the box, press the combination, Esc clears it and the arrow restores the default.</summary>
    private void ShortcutRow(ShortcutCommand command)
    {
        var box = new TextBox
        {
            Text = settings.Shortcuts.Get(command).ToString(),
            IsReadOnly = true,
            MinWidth = 170,
            PlaceholderText = Loc.Get("ShortcutNone"),
        };
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key;
            if (key is Windows.System.VirtualKey.Control or Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.Menu) return;
            if (key == Windows.System.VirtualKey.Escape)
            {
                settings.Shortcuts.Set(command, Shortcut.None);
                box.Text = "";
                settings.NotifyShortcutsChanged();
                return;
            }
            var ctrl = IsDown(Windows.System.VirtualKey.Control);
            var shift = IsDown(Windows.System.VirtualKey.Shift);
            var alt = IsDown(Windows.System.VirtualKey.Menu);
            var name = KeyName(key);
            if (name == null) return;
            var shortcut = new Shortcut(ctrl, shift, alt, name);
            settings.Shortcuts.Set(command, shortcut);
            box.Text = shortcut.ToString();
            settings.NotifyShortcutsChanged();
        };
        var reset = new Button { Content = "↺" };
        ToolTipService.SetToolTip(reset, Loc.Get("ShortcutReset"));
        reset.Click += (_, _) =>
        {
            settings.Shortcuts.Reset(command);
            box.Text = ShortcutMap.Default(command).ToString();
            settings.NotifyShortcutsChanged();
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { box, reset } };
        Add(Row("Cmd" + command, panel));
    }

    private static bool IsDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static string? KeyName(Windows.System.VirtualKey key) => key switch
    {
        >= Windows.System.VirtualKey.A and <= Windows.System.VirtualKey.Z => key.ToString().ToLowerInvariant(),
        >= Windows.System.VirtualKey.Number0 and <= Windows.System.VirtualKey.Number9 => ((int)key - (int)Windows.System.VirtualKey.Number0).ToString(),
        >= Windows.System.VirtualKey.F1 and <= Windows.System.VirtualKey.F12 => key.ToString().ToLowerInvariant(),
        Windows.System.VirtualKey.Tab => "tab",
        (Windows.System.VirtualKey)188 => ",",
        (Windows.System.VirtualKey)191 => "/",
        _ => null,
    };

    private void TestRow()
    {
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 };
        var button = new Button { Content = Loc.Get("Test") };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            status.Text = "…";
            try { status.Text = await HedgeDocService.TestAsync(settings.HedgeDocServer, settings.HedgeDocEmail, settings.HedgeDocPassword); }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { button.IsEnabled = true; }
        };
        Add(Row("TestConnection", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { button, status } }));
    }

    private static FrameworkElement Row(string key, FrameworkElement control, string? descriptionKey = null, bool stacked = false)
    {
        var label = new TextBlock { Text = Loc.Get(key), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        if (stacked)
        {
            var stack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };
            stack.Children.Add(label);
            stack.Children.Add(control);
            return stack;
        }
        var grid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (descriptionKey != null)
        {
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(label);
            text.Children.Add(new TextBlock { Text = Loc.Get(descriptionKey), Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            grid.Children.Add(text);
        }
        else
        {
            grid.Children.Add(label);
        }
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(control);
        return grid;
    }
}
