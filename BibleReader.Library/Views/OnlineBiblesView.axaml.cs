using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BibleReader.Library.Downloads;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BibleReader.Library.UI;

/// <summary>One row of <see cref="OnlineBiblesView"/>: an eBible plus its install state.</summary>
public partial class OnlineBibleItem : ObservableObject
{
    public OnlineBibleItem(InstallableBook bible, OnlineBiblesView view)
    {
        Bible = bible;
        IsInstalled = CheckInstalled(bible);
        View = view;
    }

    OnlineBiblesView View { get; set; }
    public InstallableBook Bible { get; }

    private Avalonia.Media.IImage? _cover;
    private bool _coverLoaded;
    private static bool _fontsAdded;

    /// <summary>
    /// Gives the three files of the Bible Roman font to the renderer of the SVG: it looks fonts up in the system only,
    /// so without this the generated covers (<see cref="CoverFallback"/>) show a serif of the system unless the font is installed.
    /// </summary>
    private static void AddFonts()
    {
        if (_fontsAdded) return;
        _fontsAdded = true;
        foreach (var file in new[] { "BibleRoman", "BibleRomanBold", "BibleRomanItalic" })
        {
            try
            {
                using var asset = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://BibleReader.Library/Assets/{file}.ttf"));
                var data = new MemoryStream();
                asset.CopyTo(data);
                data.Position = 0;
                Avalonia.Svg.Skia.SvgSource.s_skiaModel.Settings.TypefaceProviders.Insert(0,
                    new Svg.Skia.TypefaceProviders.CustomTypefaceProvider(data, 0));
            }
            catch (Exception) { /* no such font file: the serif of the system is used */ }
        }
    }

    /// <summary>
    /// The cover of the Bible (<see cref="InstallableBook.Image"/> is the path of its file), a bitmap decoded small or
    /// an SVG (drawn as vectors, so sharp at any size). It is loaded when the row first asks for it, so only the rows
    /// that are shown cost anything; null if there is none.
    /// </summary>
    public Avalonia.Media.IImage? Cover
    {
        get
        {
            if (!_coverLoaded)
            {
                _coverLoaded = true;
                try
                {
                    var generated = CoverFallback.FromDataUri(Bible.Image);
                    if (generated != null)
                    {
                        AddFonts();
                        _cover = new Avalonia.Svg.Skia.SvgImage { Source = Avalonia.Svg.Skia.SvgSource.LoadFromSvg(generated) };
                    }
                    else if (!string.IsNullOrEmpty(Bible.Image) && File.Exists(Bible.Image))
                    {
                        if (Path.GetExtension(Bible.Image).Equals(".svg", StringComparison.OrdinalIgnoreCase))
                            _cover = new Avalonia.Svg.Skia.SvgImage { Source = Avalonia.Svg.Skia.SvgSource.Load(Bible.Image, null) };
                        else
                            using (var stream = File.OpenRead(Bible.Image))
                                _cover = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 360);
                    }
                }
                catch (Exception) { /* a broken image file: the row has no cover */ }
            }
            return _cover;
        }
    }

    public string Description =>
        string.Join(" · ", new[] { Bible.LanguageEnglishName, Bible.Language, Bible.CountryName, Bible.Origin }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall)), NotifyPropertyChangedFor(nameof(CanDelete)), NotifyPropertyChangedFor(nameof(IsConfirmingDelete)),
        NotifyPropertyChangedFor(nameof(IsInstalledAndWide))]
    private bool _isInstalled;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanInstall)), NotifyPropertyChangedFor(nameof(CanDelete)), NotifyPropertyChangedFor(nameof(IsConfirmingDelete))]
    private bool _isBusy;

    /// <summary>Delete was clicked once: the row asks for a confirmation before it removes the Bible's files.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanDelete)), NotifyPropertyChangedFor(nameof(IsConfirmingDelete))]
    private bool _confirmDelete;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool CanInstall => !IsInstalled && !IsBusy;
    public bool CanDelete => IsInstalled && !IsBusy && !ConfirmDelete;
    public bool IsConfirmingDelete => IsInstalled && !IsBusy && ConfirmDelete;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsInstalledAndWide => IsInstalled && View.IsWide;

    /// <summary>Called by the view when <see cref="OnlineBiblesView.IsWide"/> changed: the bindings read it again.</summary>
    public void NotifyWideChanged() => OnPropertyChanged(nameof(IsInstalledAndWide));

    /// <summary>Installed means the Bible's folder holds converted books (the folder alone is created on access).</summary>
    private static bool CheckInstalled(InstallableBook bible)
    {
        if (string.IsNullOrEmpty(bible.EnglishName)) return false;
        var dir = bible.ConfigPath;
        if (!Directory.Exists(dir)) return false;
        // A Strong's dictionary has no books: it is installed when its database is.
        return bible is IStrongDictionary ? File.Exists(Path.Combine(dir, BibleMarkdown.StrongsDatabase.FileName))
            : Directory.EnumerateFiles(dir, "*.md").Any();
    }
}

/// <summary>
/// Lists the Bibles offered by ebible.org, filtered by language (and a name search), and installs the chosen
/// one with <see cref="eBible.Install"/>. The layout is in OnlineBiblesView.axaml.
/// </summary>
public partial class OnlineBiblesView : UserControl
{
    public const bool ShowExceptions = true;
    private static string AllLanguages => Localizer.Get("AllLanguages");

    private List<OnlineBibleItem> _items = new();
    private List<OnlineBibleItem> _installed = new(); // the rows of the installed Bibles expander
    private bool _loading;

    /// <summary>Raised after a Bible was installed, so the host can add it to its list.</summary>
    public event EventHandler<InstallableBook>? BibleInstalled;

    /// <summary>Raised after a Bible was deleted, so the host can remove it from its list.</summary>
    public event EventHandler<InstallableBook>? BibleDeleted;

    /// <summary>The width above which the rows show the "Install" caption next to the button's icon.</summary>
    private const double WideWidth = 500;

    /// <summary>True while the view is wider than <see cref="WideWidth"/>.</summary>
    public static readonly StyledProperty<bool> IsWideProperty =
        AvaloniaProperty.Register<OnlineBiblesView, bool>(nameof(IsWide));

    public bool IsWide { get => GetValue(IsWideProperty); private set => SetValue(IsWideProperty, value); }

    static OnlineBiblesView()
    {
        // The rows' "Installed" caption depends on it through IsInstalledAndWide, which has to be announced per row.
        IsWideProperty.Changed.AddClassHandler<OnlineBiblesView>((view, _) =>
        {
            foreach (var item in view._items.Concat(view._installed)) item.NotifyWideChanged();
        });
    }

    public OnlineBiblesView()
    {
        InitializeComponent();
        FlowDirection = Localizer.FlowDirection; // mirrored for Arabic and Hebrew
        DataContext = this;
        LanguageBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == ComboBox.TextProperty) ApplyFilter();
        };
        ClassPopup.PlacementTarget = ClassButton;
        SizeChanged += (_, e) => IsWide = e.NewSize.Width > WideWidth;
        AttachedToVisualTree += (_, _) => OnShown();
        // The page is kept while it is hidden, so what is installed may have changed since it was last shown.
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) OnShown(); };
    }

    private bool _loadStarted;

    /// <summary>
    /// Called when the view is attached or becomes visible. The view is created (and attached) hidden together with
    /// the reader, so the list is only fetched the first time it is actually shown.
    /// </summary>
    private async void OnShown()
    {
        if (!IsVisible || TopLevel.GetTopLevel(this) is null) return;
        UpdateInstalled();
        // The setting is changed in the settings page, so it is read again every time this view is shown.
        ApplyFilter(); // without the confirmation this shows the hint instead of a list
        if (!Settings.Current.InMyCountryThereIsNoPersecution && NoneInstalled && !_persecutionAsked)
        {
            // Nothing to read yet and the answer is not known: ask once, then the list is shown or not.
            _persecutionAsked = true;
            PersecutionPanel.IsVisible = true;
            return;
        }
        if (!Settings.Current.InMyCountryThereIsNoPersecution || _loadStarted || _items.Count > 0) return;
        _loadStarted = true;
        await LoadAsync();
    }

    private bool _persecutionAsked;

    /// <summary>True while no Bible is installed (the Bibles of the "Installed Bibles" list).</summary>
    public static bool NoneInstalled =>
        !(Settings.Current.Bibles ?? new List<BookText>()).OfType<InstallableBook>().Any(b => !string.IsNullOrEmpty(b.EnglishName));

    /// <summary>"Yes, there is persecution": the online Bibles stay unlisted (the setting stays off).</summary>
    private void OnPersecutionYesClick(object? sender, RoutedEventArgs e) => AnswerPersecution(noPersecution: false);

    /// <summary>"No persecution": the setting is switched on and the list is loaded.</summary>
    private void OnPersecutionNoClick(object? sender, RoutedEventArgs e) => AnswerPersecution(noPersecution: true);

    private void AnswerPersecution(bool noPersecution)
    {
        PersecutionPanel.IsVisible = false;
        Settings.Current.InMyCountryThereIsNoPersecution = noPersecution;
        try { Settings.Current.Save(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Could not save the settings: {ex}"); }
        OnShown(); // shows the hint, or loads the list
    }

    /// <summary>Raised when the close button is clicked, so the host can show the text again.</summary>
    public event EventHandler? CloseRequested;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Fills the "Installed Bibles" expander from <see cref="Settings.Bibles"/>.</summary>
    private void UpdateInstalled()
    {
        // The same rows as the online list: an installed Bible can be deleted from here too.
        var items = (Settings.Current.Bibles ?? new List<BookText>())
            .OfType<InstallableBook>()
            .Where(b => !string.IsNullOrEmpty(b.EnglishName))
            .OrderBy(b => string.IsNullOrWhiteSpace(b.Name) ? b.EnglishName : b.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(b => new OnlineBibleItem(b, this))
            .ToList();
        _installed = items;
        InstalledList.ItemsSource = items;
        InstalledHeader.Text = Localizer.Format("InstalledBiblesCount", items.Count);
    }

    // ---- Install from a file ------------------------------------------------------------------------

    private bool _installingFromFile;
    private TaskCompletionSource<string?>? _nameRequest;

    /// <summary>
    /// Picks a zip, xml file, asks for a name and installs it as a <see cref="BookArchive"/>: the books are
    /// imported into the Bible's folder and the Bible is added to the settings like an online one.
    /// </summary>
    private async void OnInstallFromFileClick(object? sender, RoutedEventArgs e)
    {
        if (_installingFromFile || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        _installingFromFile = true;
        InstallFromFileButton.IsEnabled = false;
        string? temporary = null;
        BookArchive? bible = null;
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localizer.Get("InstallBibleFromFileTitle"),
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(Localizer.Get("BibleArchives")) { Patterns = new[] { "*.zip", "*.xml" } } },
            });
            if (files.Count == 0) return;
            var file = files[0];

            var name = await AskNameAsync(Path.GetFileNameWithoutExtension(file.Name));
            if (name is null) return;

            // Some platforms (Android) give a content address instead of a path: copy that to a temporary file.
            var path = file.TryGetLocalPath();
            if (path is null)
            {
                temporary = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + Path.GetExtension(file.Name));
                await using var source = await file.OpenReadAsync();
                await using var target = System.IO.File.Create(temporary);
                await source.CopyToAsync(target);
                path = temporary;
            }

            LoadSpinner.IsVisible = true;
            StatusText.Text = Localizer.Format("Installing", name);
            bible = new BookArchive { File = path, Name = name, EnglishName = name, Language = "", LanguageEnglishName = "" };
            await bible.Install();
            if (!Directory.EnumerateFiles(bible.ConfigPath, "*.md").Any() &&
                !Directory.EnumerateFiles(bible.ConfigPath, "*.db").Any())
                throw new InvalidDataException(Localizer.Get("NoBooksImported"));

            var settings = Settings.Current;
            settings.Bibles ??= new List<BookText>();
            settings.Bibles.Add(bible);
            settings.Save();
            UpdateInstalled();
            InstalledExpander.IsExpanded = true;
            StatusText.Text = Localizer.Format("NameInstalled", name);
            BibleInstalled?.Invoke(this, bible);
            bible = null; // installed: keep its folder
        }
        catch (Exception ex)
        {
            StatusText.Text = Localizer.Format("InstallFromFileFailed", ShowExceptions ? ex.ToString() : ex.Message);
        }
        finally
        {
            // A failed install leaves a half filled folder behind: the name was free, so the folder is new.
            if (bible is not null)
                try { if (Directory.Exists(bible.ConfigPath)) Directory.Delete(bible.ConfigPath, true); } catch { }
            if (temporary is not null) try { System.IO.File.Delete(temporary); } catch { }
            LoadSpinner.IsVisible = _loading;
            InstallFromFileButton.IsEnabled = true;
            _installingFromFile = false;
        }
    }

    /// <summary>Shows the name panel; completes with the name, or null when it is cancelled.</summary>
    private Task<string?> AskNameAsync(string suggestion)
    {
        NameBox.Text = suggestion;
        NameError.IsVisible = false;
        NamePanel.IsVisible = true;
        NameBox.Focus();
        NameBox.SelectAll();
        _nameRequest = new TaskCompletionSource<string?>();
        return _nameRequest.Task;
    }

    private void FinishName(string? name)
    {
        NamePanel.IsVisible = false;
        var request = _nameRequest;
        _nameRequest = null;
        request?.TrySetResult(name);
    }

    private void OnNameOkClick(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim();
        string? error =
            string.IsNullOrEmpty(name) ? Localizer.Get("EnterName")
            : Settings.Current.Bibles?.Any(b => string.Equals(b.EnglishName, name, StringComparison.OrdinalIgnoreCase)) == true
                ? Localizer.Get("NameExists")
            : null;
        NameError.Text = error;
        NameError.IsVisible = error is not null;
        if (error is null) FinishName(name);
    }

    private void OnNameCancelClick(object? sender, RoutedEventArgs e) => FinishName(null);

    private void OnNameKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter) OnNameOkClick(sender, e);
        else if (e.Key == Avalonia.Input.Key.Escape) FinishName(null);
    }

    private async void OnReloadClick(object? sender, RoutedEventArgs e) => await LoadAsync();

    public async Task LoadAsync()
    {
        if (_loading || !Settings.Current.InMyCountryThereIsNoPersecution) return;
        _loading = true;
        LoadSpinner.IsVisible = true;
        StatusText.Text = Localizer.Get("LoadingModules");
        try
        {
            var bibles = await Task.Run(LoadSourcesAsync);
            await BibliaLibreBible.GetImagesAsync(bibles);
            _items = bibles.Select(b => new OnlineBibleItem(b, this)).ToList();
            FillLanguages();
            FillClasses();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            StatusText.Text = Localizer.Format("CouldNotLoadList", ShowExceptions ? ex.ToString() : ex.Message);
        }
        finally
        {
            _loading = false;
            LoadSpinner.IsVisible = false;
        }
    }

    /// <summary>
    /// The modules of all the sources, in one list sorted by language and name. A source that cannot be reached is
    /// left out (the others are still listed); only when none can be reached is that an error.
    /// </summary>
    private static async Task<List<InstallableBook>> LoadSourcesAsync()
    {
        var sources = new Func<Task<IEnumerable<InstallableBook>>>[]
        {
            async () => await eBible.GetBiblesAsync(),
            async () => await CrossWireBook.GetBiblesAsync(),
            async () => await BibliaLibreBible.GetBooksAsync(),
            async () => await CrossWireDictionary.GetStrongsDictionariesAsync(),
        };
        var results = await Task.WhenAll(sources.Select(async load =>
        {
            try { return (Books: await load(), Error: (Exception?)null); }
            catch (Exception ex) { return (Books: Enumerable.Empty<InstallableBook>(), Error: ex); }
        }));

        var books = results.SelectMany(r => r.Books)
            .OrderBy(b => b.LanguageEnglishName ?? "", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(b => b.Name ?? b.EnglishName ?? "", StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (books.Count == 0 && results.FirstOrDefault(r => r.Error != null).Error is { } error) throw error;
        return books;
    }

    private InstallableBook.Classes _classFilter = InstallableBook.Classes.All;
    private DateTime _classClosedAt = DateTime.MinValue;

    /// <summary>The kinds of modules (<see cref="InstallableBook.Classes"/>) that are in the list: a choice of one, or all.</summary>
    private void FillClasses()
    {
        var present = _items.Select(i => i.Bible.Class).Distinct().Where(c => c != InstallableBook.Classes.All).Order().ToList();
        if (_classFilter != InstallableBook.Classes.All && !present.Contains(_classFilter)) _classFilter = InstallableBook.Classes.All;

        // A list with one selected entry (radio buttons do not exclude each other while the popup is closed, which
        // left "All" checked for good).
        _fillingClasses = true;
        try
        {
            var entries = present.Prepend(InstallableBook.Classes.All).Select(kind => new ListBoxItem
            {
                Tag = kind,
                Content = kind == InstallableBook.Classes.All ? $"{Localizer.Get("All")} ({_items.Count})" : $"{Localizer.ClassName(kind)} ({_items.Count(i => i.Bible.Class == kind)})",
            }).ToList();
            ClassList.ItemsSource = entries;
            ClassList.SelectedItem = entries.First(entry => (InstallableBook.Classes)entry.Tag! == _classFilter);
        }
        finally { _fillingClasses = false; }
        UpdateClassButton();
    }

    private bool _fillingClasses;

    /// <summary>A kind was chosen; choosing the chosen one again (nothing selected) or "All" turns the filter off.</summary>
    private void OnClassSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_fillingClasses) return;
        var kind = (ClassList.SelectedItem as ListBoxItem)?.Tag is InstallableBook.Classes chosen ? chosen : InstallableBook.Classes.All;
        if (ClassList.SelectedItem is null) // toggled off: "All" is the entry that shows it
        {
            _fillingClasses = true;
            ClassList.SelectedIndex = 0;
            _fillingClasses = false;
        }
        ClassPopup.IsOpen = false;
        if (kind == _classFilter) return;
        _classFilter = kind;
        UpdateClassButton();
        ApplyFilter();
    }

    /// <summary>The filter icon is tinted while a kind is chosen, so a shortened list is not a surprise.</summary>
    private void UpdateClassButton()
    {
        bool active = _classFilter != InstallableBook.Classes.All;
        ClassButton.Background = active ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#664C8BF5")) : Avalonia.Media.Brushes.Transparent;
        ToolTip.SetTip(ClassButton, active ? Localizer.Format("ShowingKind", Localizer.ClassName(_classFilter)) : Localizer.Get("FilterByKind"));
    }

    private void OnClassFilterClick(object? sender, RoutedEventArgs e)
    {
        if (ClassPopup.IsOpen) { ClassPopup.IsOpen = false; return; }
        // Pressing the button already light-dismissed the open popup; don't reopen it with the same click.
        if ((DateTime.UtcNow - _classClosedAt).TotalMilliseconds < 300) return;
        ClassPopup.IsOpen = true;
    }

    private void OnClassPopupClosed(object? sender, EventArgs e) => _classClosedAt = DateTime.UtcNow;

    private void FillLanguages()
    {
        var languages = _items
            .Select(i => LanguageOf(i.Bible))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct()
            .OrderBy(l => l, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        int i = 0;
        // Remove duplicates
        while (i < languages.Count-1)
        {
            while (i < languages.Count - 1 && languages[i + 1].Contains(languages[i], StringComparison.OrdinalIgnoreCase)) languages.RemoveAt(i + 1);
            i++;
        }
        var selected = LanguageBox.SelectedItem as string;
        LanguageBox.ItemsSource = new[] { AllLanguages }.Concat(languages).ToList();
        if (selected is not null && ((IEnumerable<string>)LanguageBox.ItemsSource).Contains(selected)) LanguageBox.SelectedItem = selected;
        else LanguageBox.Text = "";
    }

    private static string LanguageOf(InstallableBook bible) =>
        string.IsNullOrWhiteSpace(bible.LanguageEnglishName) ? bible.Language : bible.LanguageEnglishName;

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (LanguageBox is null || SearchBox is null || StatusText is null) return; // during InitializeComponent
        if (!Settings.Current.InMyCountryThereIsNoPersecution)
        {
            List.ItemsSource = Array.Empty<OnlineBibleItem>();
            StatusText.Text = Localizer.Format("PersecutionHint", Localizer.Get("NoPersecution"));
            return;
        }
        var language = LanguageBox.Text?.Trim();
        if (string.IsNullOrEmpty(language)) language = AllLanguages;
        string langenglish = null;
        if (Regex.IsMatch(language, "[a-z]{2,3}(?:-[A-Z]{2})?"))
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(language);
                language = Regex.Replace(culture.NativeName, @"\s+|\(.*?\)", "", RegexOptions.Singleline);
                langenglish = Regex.Replace(culture.EnglishName, @"\s+|\(.*?\)", "", RegexOptions.Singleline);
            }
            catch { }
        }
        var text = SearchBox.Text?.Trim();
        var filtered = _items.Where(i =>
            (_classFilter == InstallableBook.Classes.All || i.Bible.Class == _classFilter) &&
            (language == AllLanguages || i.Bible.Language.Contains(language, StringComparison.CurrentCultureIgnoreCase) ||
            i.Bible.LanguageEnglishName.Contains(language, StringComparison.CurrentCultureIgnoreCase) ||
            langenglish != null && 
            (i.Bible.Language.Contains(langenglish, StringComparison.CurrentCultureIgnoreCase) ||
            i.Bible.LanguageEnglishName.Contains(langenglish, StringComparison.CurrentCultureIgnoreCase))) &&
            (string.IsNullOrEmpty(text) ||
             (i.Bible.Name?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
             (i.Bible.EnglishName?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false)))
            .ToList();
        List.ItemsSource = filtered;
        if (_items.Count > 0) StatusText.Text = Localizer.Format("ModulesCount", filtered.Count, _items.Count);
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is OnlineBibleItem { CanDelete: true } item) item.ConfirmDelete = true;
    }

    private void OnCancelDeleteClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is OnlineBibleItem item) item.ConfirmDelete = false;
    }

    /// <summary>Removes the Bible's folder (the converted books and the downloaded source) and its entry in the settings.</summary>
    private async void OnConfirmDeleteClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not OnlineBibleItem { IsConfirmingDelete: true } item) return;
        item.Error = null;
        item.IsBusy = true;
        try
        {
            await Task.Run(() => item.Bible.Uninstall());
            var settings = Settings.Current;
            settings.Bibles?.RemoveAll(b => b.EnglishName == item.Bible.EnglishName);
            settings.Save();
            StrongsDictionaries.Refresh();
            item.IsInstalled = false;
            // The Bible may be shown in both lists (the rows are separate objects): the online one is no longer installed.
            foreach (var other in _items.Where(o => o.Bible.EnglishName == item.Bible.EnglishName)) other.IsInstalled = false;
            UpdateInstalled();
            BibleDeleted?.Invoke(this, item.Bible);
        }
        catch (Exception ex)
        {
            item.Error = Localizer.Format("DeleteFailed", ShowExceptions ? ex.ToString() : ex.Message);
        }
        finally
        {
            item.ConfirmDelete = false;
            item.IsBusy = false;
        }
    }

    private async void OnInstallClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not OnlineBibleItem item || !item.CanInstall) return;
        item.Error = null;
        item.IsBusy = true;
        try
        {
            await item.Bible.Install();
            item.IsInstalled = true;
            var settings = Settings.Current;
            settings.Bibles ??= new List<BookText>();
            if (!settings.Bibles.OfType<InstallableBook>().Any(b => b.Origin == item.Bible.Origin && b.EnglishName == item.Bible.EnglishName))
                settings.Bibles.Add(item.Bible);
            settings.Save();
            StrongsDictionaries.Refresh(); // a dictionary that was installed counts only once it is in the list
            UpdateInstalled();
            BibleInstalled?.Invoke(this, item.Bible);
        }
        catch (Exception ex)
        {
            item.Error = Localizer.Format("InstallFailed", ShowExceptions ? ex.ToString() : ex.Message);
        }
        finally
        {
            item.IsBusy = false;
        }
    }
}
