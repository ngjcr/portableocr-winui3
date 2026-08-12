using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PortableOCR.WinUI3.Models;
using PortableOCR.WinUI3.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Microsoft.Windows.Storage.Pickers;

namespace PortableOCR.WinUI3;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<OcrDocument> Documents { get; } = [];
    private readonly OcrEngine _engine = new();
    private UiSettings _settings = SettingsStore.Load();
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _previewCts;
    private OcrDocument? _selected;
    private int _page = 1;
    private double _zoom = 1;
    private bool _prepared;
    private bool _overlay;
    private bool _busy;
    private readonly string _desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1380, 860));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "PortableOCR.ico"));
        AppWindow.Closing += AppWindow_Closing;
        RootGrid.KeyDown += RootGrid_KeyDown;
        ApplySettingsToUi();
        UpdateUiState();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_busy) return;
        args.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "OCR is still running",
            Content = "Stop the current OCR job and close PortableOCR?",
            PrimaryButtonText = "Stop and close",
            CloseButtonText = "Keep working",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _runCts?.Cancel(); _engine.Cancel(); _busy = false; Close();
        }
    }

    private void ApplySettingsToUi()
    {
        SetQuality(_settings.Quality, save: false);
        TextOutputToggle.IsChecked = _settings.Text;
        PdfOutputToggle.IsChecked = _settings.Pdf;
        AutoClearToggle.IsOn = _settings.AutoClear;
        RootGrid.RequestedTheme = _settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    }

    private void SaveSettings()
    {
        _settings = new UiSettings(_settings.Quality, TextOutputToggle.IsChecked == true, PdfOutputToggle.IsChecked == true, AutoClearToggle.IsOn, _settings.Theme);
        SettingsStore.Save(_settings);
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = "Add selected files"
        };
        foreach (var ext in new[] { ".pdf", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp", ".gif" }) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        await AddPathsAsync(files.Select(f => f.Path));
    }

    private async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var existing = Documents.Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = paths.Where(p => !string.IsNullOrWhiteSpace(p) && OcrEngine.IsSupported(p) && !existing.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (unique.Length == 0) return;
        Status("Reading files…", "Checking pages and preparing previews.");
        foreach (var path in unique)
        {
            try
            {
                var info = await _engine.InspectAsync(path);
                var item = new OcrDocument { Path = info.Path, Name = info.Name, Extension = info.Extension, Size = info.Size, SizeLabel = info.SizeLabel, Pages = info.Pages };
                Documents.Add(item);
                _ = LoadThumbnailAsync(item);
            }
            catch (Exception ex) { await ShowErrorAsync("Could not add file", $"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        if (_selected is null && Documents.Count > 0) QueueList.SelectedIndex = 0;
        UpdateUiState();
        Status("Ready", $"{Documents.Count} {(Documents.Count == 1 ? "file" : "files")} · {Documents.Sum(x => x.Pages)} pages ready.");
    }

    private async Task LoadThumbnailAsync(OcrDocument item)
    {
        try
        {
            item.ThumbnailPath = await _engine.GetPreviewAsync(item.Path, 1, false, "fast", 0);
            item.ThumbnailSource = new BitmapImage(new Uri(item.ThumbnailPath));
        }
        catch { }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add documents to PortableOCR";
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void RootGrid_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        await AddPathsAsync(items.OfType<StorageFile>().Select(f => f.Path));
    }

    private void QueueList_ItemClick(object sender, ItemClickEventArgs e) => QueueList.SelectedItem = e.ClickedItem;
    private async void QueueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = QueueList.SelectedItem as OcrDocument;
        _page = 1; _zoom = 1; _prepared = false; _overlay = false;
        PreparedToggle.IsChecked = false; OverlayToggle.IsChecked = false;
        SyncFileQualityCombo();
        await RefreshPreviewAsync();
        UpdateUiState();
    }

    private async Task RefreshPreviewAsync()
    {
        _previewCts?.Cancel(); _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;
        if (_selected is null)
        {
            PreviewEmpty.Visibility = Visibility.Visible; PreviewActive.Visibility = Visibility.Collapsed; return;
        }
        PreviewEmpty.Visibility = Visibility.Collapsed; PreviewActive.Visibility = Visibility.Visible;
        PreviewName.Text = _selected.Name;
        PreviewMeta.Text = _selected.Meta;
        PageNowRun.Text = _page.ToString(); PageTotalRun.Text = _selected.Pages.ToString();
        PreviewBusy.IsActive = true; PreviewBusy.Visibility = Visibility.Visible;
        try
        {
            var quality = EffectiveQuality(_selected);
            var path = await _engine.GetPreviewAsync(_selected.Path, _page, _prepared || _overlay, quality, _selected.Rotation, token);
            if (token.IsCancellationRequested) return;
            PreviewImage.Source = new BitmapImage(new Uri(path));
            ApplyPreviewTransform();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await ShowErrorAsync("Preview unavailable", ex.Message); }
        finally { if (!token.IsCancellationRequested) { PreviewBusy.IsActive = false; PreviewBusy.Visibility = Visibility.Collapsed; } }
    }

    private void PreviewImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        OverlayCanvas.Width = PreviewImage.ActualWidth;
        OverlayCanvas.Height = PreviewImage.ActualHeight;
        RenderOverlay();
    }

    private void RenderOverlay()
    {
        OverlayCanvas.Children.Clear();
        if (!_overlay || _selected?.Result is null || _page < 1 || _page > _selected.Result.Overlays.Count) return;
        var data = _selected.Result.Overlays[_page - 1];
        if (data.Width <= 0 || data.Height <= 0 || PreviewImage.Source is not BitmapImage bitmap || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0) return;
        var hostW = PreviewImage.ActualWidth; var hostH = PreviewImage.ActualHeight;
        var scale = Math.Min(hostW / bitmap.PixelWidth, hostH / bitmap.PixelHeight);
        var shownW = bitmap.PixelWidth * scale; var shownH = bitmap.PixelHeight * scale;
        var ox = (hostW - shownW) / 2; var oy = (hostH - shownH) / 2;
        foreach (var word in data.Words)
        {
            var rect = new Border
            {
                Width = word.Width / (double)data.Width * shownW,
                Height = word.Height / (double)data.Height * shownH,
                BorderBrush = new SolidColorBrush(Colors.DodgerBlue),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 30, 144, 255)),
                CornerRadius = new CornerRadius(2)
            };
            Canvas.SetLeft(rect, ox + word.Left / (double)data.Width * shownW);
            Canvas.SetTop(rect, oy + word.Top / (double)data.Height * shownH);
            OverlayCanvas.Children.Add(rect);
        }
    }

    private void ApplyPreviewTransform()
    {
        var rotate = (_prepared || _overlay) ? 0 : _selected?.Rotation ?? 0;
        PreviewSurface.RenderTransformOrigin = new Windows.Foundation.Point(.5, .5);
        PreviewSurface.RenderTransform = new CompositeTransform { ScaleX = _zoom, ScaleY = _zoom, Rotation = rotate };
        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || Documents.Count == 0 || (TextOutputToggle.IsChecked != true && PdfOutputToggle.IsChecked != true)) return;
        _busy = true; _runCts = new CancellationTokenSource(); UpdateUiState();
        var items = Documents.ToArray();
        var totalWeight = items.Sum(x => Math.Max(1, x.Pages));
        var progressById = items.ToDictionary(x => x.Id, _ => 0d);
        var workers = ChooseWorkerCount(items);
        _engine.BeginBatch(workers);
        var workerLabel = workers == 1 ? "worker" : "workers";
        Status("Starting OCR…", $"{items.Length} files · {totalWeight} pages · {workers} {workerLabel}.");

        foreach (var item in items) { item.Status = "Ready"; item.Progress = 0; item.Error = null; item.Message = "Ready"; }
        var next = -1;
        try
        {
            async Task WorkerAsync()
            {
                while (true)
                {
                    _runCts.Token.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref next);
                    if (index >= items.Length) return;
                    var item = items[index];
                    item.Status = "Processing"; item.Progress = 0; item.Error = null;
                    var progress = new Progress<OcrProgress>(p =>
                    {
                        item.Status = "Processing"; item.Message = p.Message; item.Progress = p.Percent;
                        progressById[item.Id] = p.Percent;
                        var weighted = items.Sum(d => progressById[d.Id] * Math.Max(1, d.Pages));
                        OverallProgress.Value = weighted / totalWeight;
                        ProgressText.Text = $"{Math.Round(OverallProgress.Value)}%";
                        Status("Reading document", p.Message);
                    });
                    try
                    {
                        var result = await _engine.ProcessFileAsync(item, new OcrSettings(_settings.Quality, TextOutputToggle.IsChecked == true, PdfOutputToggle.IsChecked == true), _desktop, progress, _runCts.Token);
                        item.Result = result; item.Status = "Done"; item.Progress = 100; item.Message = "Complete"; progressById[item.Id] = 100;
                        if (ReferenceEquals(item, _selected)) { PreviewMeta.Text = item.Meta; RenderOverlay(); }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { item.Status = "Error"; item.Error = ex.Message; item.Progress = 0; progressById[item.Id] = 100; }
                    var total = items.Sum(d => progressById[d.Id] * Math.Max(1, d.Pages));
                    OverallProgress.Value = total / totalWeight;
                    ProgressText.Text = $"{Math.Round(OverallProgress.Value)}%";
                }
            }

            await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => WorkerAsync()));
            OverallProgress.Value = 100; ProgressText.Text = "100%";
            var completedCount = items.Count(x => x.Status == "Done");
            Status("Complete", $"{completedCount} files · {totalWeight} pages.");
            if (AutoClearToggle.IsOn)
            {
                foreach (var done in Documents.Where(x => x.Status == "Done").ToArray()) Documents.Remove(done);
                QueueList.SelectedIndex = Documents.Count > 0 ? 0 : -1;
            }
        }
        catch (OperationCanceledException) { Status("Cancelled", "The current OCR job was stopped."); OverallProgress.Value = 0; ProgressText.Text = string.Empty; }
        finally { _busy = false; _runCts.Dispose(); _runCts = null; UpdateUiState(); }
    }

    private int ChooseWorkerCount(IEnumerable<OcrDocument> items)
    {
        var cpus = Math.Max(1, Environment.ProcessorCount);
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var hasBest = items.Any(x => EffectiveQuality(x) == "best");
        var workers = 1;
        if (cpus >= 10 && memory >= 8L * 1024 * 1024 * 1024) workers = 2;
        if (!hasBest && cpus >= 18 && memory >= 16L * 1024 * 1024 * 1024) workers = 3;
        if (hasBest && cpus < 16) workers = 1;
        return Math.Min(workers, Math.Max(1, Documents.Count));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) { Status("Cancelling…", "Stopping the active OCR process."); _runCts?.Cancel(); _engine.Cancel(); }
    private void ClearQueue_Click(object sender, RoutedEventArgs e) { if (_busy) return; Documents.Clear(); _selected = null; QueueList.SelectedIndex = -1; Status("Ready", "Add an image or PDF to begin."); OverallProgress.Value = 0; UpdateUiState(); _ = RefreshPreviewAsync(); }

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton t && t.Tag is string q) SetQuality(q);
    }
    private void SetQuality(string quality, bool save = true)
    {
        _settings = _settings with { Quality = quality };
        FastProfile.IsChecked = quality == "fast"; BalancedProfile.IsChecked = quality == "balanced"; BestProfile.IsChecked = quality == "best";
        QualityBadge.Text = quality.ToUpperInvariant();
        QualityDescription.Text = quality switch { "fast" => "Fast · optimized for clean pages and larger batches", "best" => "Best · deskews, enhances, uses 400 DPI, and adaptively compares OCR layouts", _ => "Balanced · cleans scans and uses 300 DPI for PDFs" };
        if (save) { SaveSettings(); _ = RefreshPreviewAsync(); }
    }
    private void Output_Click(object sender, RoutedEventArgs e) { SaveSettings(); UpdateUiState(); }
    private void AutoClear_Toggled(object sender, RoutedEventArgs e) => SaveSettings();

    private async void Prepared_Click(object sender, RoutedEventArgs e) { _prepared = PreparedToggle.IsChecked == true; await RefreshPreviewAsync(); }
    private async void Overlay_Click(object sender, RoutedEventArgs e) { _overlay = OverlayToggle.IsChecked == true; if (_overlay) _prepared = true; await RefreshPreviewAsync(); }
    private async void Rotate_Click(object sender, RoutedEventArgs e) { if (_selected is null) return; _selected.Rotation = (_selected.Rotation + 90) % 360; await RefreshPreviewAsync(); }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { _zoom = Math.Min(2.5, _zoom + .15); ApplyPreviewTransform(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { _zoom = Math.Max(.35, _zoom - .15); ApplyPreviewTransform(); }
    private async void PreviousPage_Click(object sender, RoutedEventArgs e) { if (_selected is null || _page <= 1) return; _page--; await RefreshPreviewAsync(); }
    private async void NextPage_Click(object sender, RoutedEventArgs e) { if (_selected is null || _page >= _selected.Pages) return; _page++; await RefreshPreviewAsync(); }

    private async void FileQuality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selected is null || FileQualityCombo.SelectedItem is not ComboBoxItem c || c.Tag is not string q) return;
        _selected.Quality = q; await RefreshPreviewAsync();
    }
    private void SyncFileQualityCombo()
    {
        if (_selected is null) return;
        for (var i = 0; i < FileQualityCombo.Items.Count; i++)
            if (FileQualityCombo.Items[i] is ComboBoxItem c && string.Equals(c.Tag as string, _selected.Quality, StringComparison.OrdinalIgnoreCase)) { FileQualityCombo.SelectedIndex = i; return; }
        FileQualityCombo.SelectedIndex = 0;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var path = _selected?.Result?.TextPath; if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var package = new DataPackage(); package.SetText(await File.ReadAllTextAsync(path)); Clipboard.SetContent(package); Clipboard.Flush();
        Status("Text copied", "Recognized text is on the clipboard.");
    }
    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var p = _selected?.Result?.PdfPath ?? _selected?.Result?.TextPath; if (!string.IsNullOrWhiteSpace(p)) OpenPath(p);
    }
    private void OpenDesktop_Click(object sender, RoutedEventArgs e) => OpenPath(_desktop);

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var theme = new ComboBox { Width = 220 };
        theme.Items.Add(new ComboBoxItem { Content = "Use Windows setting", Tag = "system" });
        theme.Items.Add(new ComboBoxItem { Content = "Light", Tag = "light" });
        theme.Items.Add(new ComboBoxItem { Content = "Dark", Tag = "dark" });
        for (var i = 0; i < theme.Items.Count; i++) if ((theme.Items[i] as ComboBoxItem)?.Tag as string == _settings.Theme) theme.SelectedIndex = i;
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "Appearance", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = "PortableOCR follows Fluent design and Mica by default.", Opacity = .72 });
        content.Children.Add(theme);
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "Settings", Content = content, PrimaryButtonText = "Apply", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && theme.SelectedItem is ComboBoxItem c && c.Tag is string value)
        {
            _settings = _settings with { Theme = value }; SaveSettings(); RootGrid.RequestedTheme = value switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        }
    }

    private void UpdateUiState()
    {
        var has = Documents.Count > 0;
        EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        QueueState.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        QueueCountText.Text = $"{Documents.Count} {(Documents.Count == 1 ? "file" : "files")} · {Documents.Sum(x => x.Pages)} pages";
        RunButton.IsEnabled = has && !_busy && (TextOutputToggle.IsChecked == true || PdfOutputToggle.IsChecked == true);
        RunButton.Visibility = _busy ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        QueueList.IsEnabled = !_busy;
        PreparedToggle.IsEnabled = _selected is not null && EffectiveQuality(_selected) != "fast";
        OverlayToggle.IsEnabled = _selected?.Result?.Overlays.Count > 0;
        CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(_selected?.Result?.TextPath);
        OpenOutputButton.IsEnabled = !string.IsNullOrWhiteSpace(_selected?.Result?.PdfPath ?? _selected?.Result?.TextPath);
    }

    private string EffectiveQuality(OcrDocument item) => item.Quality is "fast" or "balanced" or "best" ? item.Quality : _settings.Quality;
    private void Status(string title, string detail) { StatusTitle.Text = title; StatusDetail.Text = detail; }
    private static void OpenPath(string path) { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { } }
    private async Task ShowErrorAsync(string title, string message) => await new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = title, Content = message, CloseButtonText = "OK" }.ShowAsync();

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == Windows.System.VirtualKey.O) { e.Handled = true; AddFiles_Click(this, new RoutedEventArgs()); }
        else if (ctrl && e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; Run_Click(this, new RoutedEventArgs()); }
        else if (e.Key == Windows.System.VirtualKey.Delete && !_busy && _selected is not null) { e.Handled = true; Documents.Remove(_selected); _selected = null; QueueList.SelectedIndex = Documents.Count > 0 ? 0 : -1; UpdateUiState(); _ = RefreshPreviewAsync(); }
    }
}
