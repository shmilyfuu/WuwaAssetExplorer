using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WuwaAssetExplorer.Core.Models;
using WuwaAssetExplorer.Core.Services;

namespace WuwaAssetExplorer;

public sealed partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly AesEndpointService _aesService = new();
    private readonly WuwaArchiveService _archiveService = new();
    private CancellationTokenSource? _searchCts;
    private AppSettings _settings = AppSettings.Default;
    private bool _loadInProgress;

    public MainWindow()
    {
        StartupLog.Write("MainWindow constructor entered");
        StartupLog.Write("MainWindow InitializeComponent starting");
        InitializeComponent();
        StartupLog.Write("MainWindow XAML initialized");

        Title = "WuwaAssetExplorer";
        StartupLog.Write("MainWindow title set");

        SystemBackdrop = new MicaBackdrop();
        StartupLog.Write("MainWindow Mica configured");

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        StartupLog.Write("MainWindow custom title bar configured");

        AppWindow.Resize(new SizeInt32(1120, 760));
        StartupLog.Write("MainWindow resized");

        RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
        StartupLog.Write("MainWindow initial navigation selected");

        _ = InitializeAsync();
        StartupLog.Write("MainWindow async initialization queued");
    }

    private async Task InitializeAsync()
    {
        try
        {
            _settings = await _settingsService.LoadAsync();
            PaksPathTextBox.Text = _settings.PaksPath ?? string.Empty;
            AesEndpointTextBox.Text = _settings.AesEndpoint;
            StartupLog.Write("MainWindow settings loaded");
        }
        catch (Exception ex)
        {
            StartupLog.Write("MainWindow settings load failed", ex);
            ShowLoadMessage("便携数据目录不可用", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void RootNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.SelectedItemContainer?.Tag?.ToString();
        AssetsView.Visibility = tag == "Assets" ? Visibility.Visible : Visibility.Collapsed;
        ReferencesView.Visibility = tag == "References" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ChooseGameDirectory_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        try
        {
            var resolved = WuwaPathResolver.ResolvePaksDirectory(folder.Path);
            PaksPathTextBox.Text = resolved;
            _settings = _settings with { PaksPath = resolved };
            await _settingsService.SaveAsync(_settings);
            StatusText.Text = "已选择游戏目录";
        }
        catch (Exception ex)
        {
            ShowLoadMessage("目录无效", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async void LoadGame_Click(object sender, RoutedEventArgs e)
    {
        await LoadGameAsync();
    }

    private async Task LoadGameAsync()
    {
        var paksPath = PaksPathTextBox.Text?.Trim() ?? string.Empty;
        var endpoint = EffectiveEndpoint();

        if (string.IsNullOrWhiteSpace(paksPath))
        {
            ShowLoadMessage("尚未选择游戏目录", "请选择鸣潮游戏目录或 Client\\Content\\Paks。", InfoBarSeverity.Warning);
            return;
        }

        _loadInProgress = true;
        LoadProgressBar.Visibility = Visibility.Visible;
        LoadProgressBar.IsIndeterminate = true;
        SetBusy(true, "正在读取包索引…");

        try
        {
            var resolved = WuwaPathResolver.ResolvePaksDirectory(paksPath);
            StatusText.Text = "正在获取 AES…";
            var aes = await _aesService.GetKeysAsync(endpoint);

            var progress = new Progress<ArchiveLoadProgress>(UpdateArchiveLoadProgress);
            var result = await _archiveService.LoadAsync(resolved, aes.Keys, progress);

            PaksPathTextBox.Text = resolved;
            AssetCountText.Text = $"已加载 {result.AssetCount:N0} 个资源 · {result.Elapsed.TotalSeconds:F1}s";
            SearchCountText.Text = string.Empty;
            StatusText.Text = aes.UsedCache ? "就绪 · AES 使用缓存" : "就绪";
            ShowLoadMessage("鸣潮资源已加载", $"资源目录共 {result.AssetCount:N0} 项。现在可以直接搜索名称或完整路径。", InfoBarSeverity.Success);

            _settings = new AppSettings(resolved, endpoint);
            await _settingsService.SaveAsync(_settings);

            if (!string.IsNullOrWhiteSpace(AssetSearchBox.Text))
            {
                await SearchAsync(AssetSearchBox.Text);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败";
            ShowLoadMessage("无法加载鸣潮资源", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _loadInProgress = false;
            LoadProgressBar.IsIndeterminate = false;
            LoadProgressBar.Visibility = Visibility.Collapsed;
            SetBusy(false, null);
        }
    }

    private void UpdateArchiveLoadProgress(ArchiveLoadProgress progress)
    {
        if (!_loadInProgress) return;

        LoadProgressBar.Visibility = Visibility.Visible;

        switch (progress.Stage)
        {
            case ArchiveLoadStage.ScanningArchives:
                LoadProgressBar.IsIndeterminate = true;
                StatusText.Text = "正在读取包索引…";
                break;

            case ArchiveLoadStage.MountingArchives:
                if (progress.TotalArchives <= 0)
                {
                    LoadProgressBar.IsIndeterminate = true;
                    StatusText.Text = "正在挂载鸣潮资源…";
                    break;
                }

                LoadProgressBar.IsIndeterminate = false;
                var fraction = Math.Clamp((double) progress.MountedArchives / progress.TotalArchives, 0, 1);
                LoadProgressBar.Value = 8 + fraction * 82;
                StatusText.Text = $"正在挂载 {progress.MountedArchives:N0}/{progress.TotalArchives:N0} · {fraction:P0}{FormatRemainingTime(progress)}";
                break;

            case ArchiveLoadStage.BuildingCatalog:
                LoadProgressBar.IsIndeterminate = false;
                LoadProgressBar.Value = 94;
                StatusText.Text = $"正在建立资源目录 · {progress.IndexedFiles:N0} 项";
                break;

            case ArchiveLoadStage.Completed:
                LoadProgressBar.IsIndeterminate = false;
                LoadProgressBar.Value = 100;
                StatusText.Text = "正在完成资源加载…";
                break;
        }
    }

    private static string FormatRemainingTime(ArchiveLoadProgress progress)
    {
        if (progress.TotalArchives <= 0 ||
            progress.MountedArchives < 2 ||
            progress.MountedArchives >= progress.TotalArchives ||
            progress.StageElapsed.TotalSeconds < 0.8)
        {
            return string.Empty;
        }

        var remainingArchives = progress.TotalArchives - progress.MountedArchives;
        var seconds = progress.StageElapsed.TotalSeconds * remainingArchives / progress.MountedArchives;
        if (!double.IsFinite(seconds) || seconds <= 0) return string.Empty;

        if (seconds < 60)
        {
            return $" · 约剩 {Math.Max(1, Math.Round(seconds)):N0} 秒";
        }

        return $" · 约剩 {seconds / 60:F1} 分钟";
    }

    private async void AssetSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var query = sender.Text;

        if (string.IsNullOrWhiteSpace(query))
        {
            SearchResultsList.ItemsSource = null;
            SearchCountText.Text = string.Empty;
            return;
        }

        if (!_archiveService.IsLoaded)
        {
            SearchCountText.Text = "请先加载游戏资源";
            return;
        }

        try
        {
            await Task.Delay(25, token);
            await SearchAsync(query, token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var results = await Task.Run(() => _archiveService.Search(query, 500), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        SearchResultsList.ItemsSource = results;
        started.Stop();
        SearchCountText.Text = $"{results.Count:N0} 项 · {started.Elapsed.TotalMilliseconds:F0} ms";
    }

    private async void RefreshAes_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在刷新 AES…");
        try
        {
            var endpoint = EffectiveEndpoint();
            var result = await _aesService.GetKeysAsync(endpoint, forceRefresh: true);
            SettingsInfoBar.Title = "AES Endpoint 可用";
            SettingsInfoBar.Message = $"已读取主密钥与 {result.Keys.DynamicKeys.Count:N0} 个动态密钥。";
            SettingsInfoBar.Severity = InfoBarSeverity.Success;
            SettingsInfoBar.IsOpen = true;
            StatusText.Text = result.UsedCache ? "就绪 · AES 使用缓存" : "就绪 · AES 已更新";
        }
        catch (Exception ex)
        {
            SettingsInfoBar.Title = "AES Endpoint 检查失败";
            SettingsInfoBar.Message = ex.Message;
            SettingsInfoBar.Severity = InfoBarSeverity.Error;
            SettingsInfoBar.IsOpen = true;
            StatusText.Text = "AES 检查失败";
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings = new AppSettings(
                string.IsNullOrWhiteSpace(PaksPathTextBox.Text) ? null : PaksPathTextBox.Text.Trim(),
                EffectiveEndpoint());
            await _settingsService.SaveAsync(_settings);
            SettingsInfoBar.Title = "设置已保存";
            SettingsInfoBar.Message = "配置保存在程序目录的 Data 文件夹中。";
            SettingsInfoBar.Severity = InfoBarSeverity.Success;
            SettingsInfoBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            SettingsInfoBar.Title = "保存失败";
            SettingsInfoBar.Message = ex.Message;
            SettingsInfoBar.Severity = InfoBarSeverity.Error;
            SettingsInfoBar.IsOpen = true;
        }
    }

    private string EffectiveEndpoint()
        => string.IsNullOrWhiteSpace(AesEndpointTextBox.Text)
            ? AesEndpointService.DefaultEndpoint
            : AesEndpointTextBox.Text.Trim();

    private void SetBusy(bool busy, string? status)
    {
        BusyRing.IsActive = busy;
        if (!string.IsNullOrWhiteSpace(status)) StatusText.Text = status;
    }

    private void ShowLoadMessage(string title, string message, InfoBarSeverity severity)
    {
        LoadInfoBar.Title = title;
        LoadInfoBar.Message = message;
        LoadInfoBar.Severity = severity;
        LoadInfoBar.IsOpen = true;
    }
}
