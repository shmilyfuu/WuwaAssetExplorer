using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    private readonly HashSet<string> _loadedTreePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _treeLoadTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _breadcrumbPaths = [];
    private CancellationTokenSource? _searchCts;
    private AppSettings _settings = AppSettings.Default;
    private bool _loadInProgress;
    private string _currentDirectoryPath = string.Empty;

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

            await InitializeResourceBrowserAsync();

            PaksPathTextBox.Text = resolved;
            AssetCountText.Text = $"已加载 {result.AssetCount:N0} 个文件项 · {result.Elapsed.TotalSeconds:F1}s";
            StatusText.Text = aes.UsedCache ? "就绪 · AES 使用缓存" : "就绪";
            ShowLoadMessage("鸣潮资源已加载", $"资源目录共 {result.AssetCount:N0} 个文件项。可以通过左侧目录浏览，也可以直接搜索名称或完整路径。", InfoBarSeverity.Success);

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

    private async Task InitializeResourceBrowserAsync()
    {
        DirectoryTree.RootNodes.Clear();
        _loadedTreePaths.Clear();
        _treeLoadTasks.Clear();
        _currentDirectoryPath = string.Empty;

        var root = await Task.Run(() => _archiveService.Browse(string.Empty));
        foreach (var directory in root.Items.Where(x => x.IsDirectory))
        {
            DirectoryTree.RootNodes.Add(CreateDirectoryNode(directory));
        }

        ApplyDirectorySnapshot(root);
        DirectoryTree.SelectedNode = null;
    }

    private static TreeViewNode CreateDirectoryNode(AssetBrowserEntry directory)
    {
        var node = new TreeViewNode
        {
            Content = new DirectoryNodeContent(directory.Name, directory.FullPath)
        };

        // A lightweight placeholder makes the first expand gesture open immediately.
        // It is replaced with real children after the directory snapshot is ready.
        node.Children.Add(new TreeViewNode { Content = LoadingNodeContent.Instance });
        return node;
    }

    private async Task EnsureNodeChildrenLoadedAsync(TreeViewNode node)
    {
        if (node.Content is not DirectoryNodeContent content) return;
        if (_loadedTreePaths.Contains(content.FullPath)) return;

        if (_treeLoadTasks.TryGetValue(content.FullPath, out var existingTask))
        {
            await existingTask;
            return;
        }

        var loadTask = LoadNodeChildrenAsync(node, content);
        _treeLoadTasks[content.FullPath] = loadTask;
        try
        {
            await loadTask;
        }
        finally
        {
            _treeLoadTasks.Remove(content.FullPath);
        }
    }

    private async Task LoadNodeChildrenAsync(TreeViewNode node, DirectoryNodeContent content)
    {
        var snapshot = await Task.Run(() => _archiveService.Browse(content.FullPath));
        node.Children.Clear();
        foreach (var directory in snapshot.Items.Where(x => x.IsDirectory))
        {
            node.Children.Add(CreateDirectoryNode(directory));
        }

        _loadedTreePaths.Add(content.FullPath);
    }

    private async void DirectoryTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (!_archiveService.IsLoaded || args.Node.Content is not DirectoryNodeContent) return;

        try
        {
            await EnsureNodeChildrenLoadedAsync(args.Node);
        }
        catch (Exception ex)
        {
            StatusText.Text = "目录展开失败";
            ShowLoadMessage("无法展开资源目录", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async void DirectoryTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not DirectoryNodeContent content) return;
        await NavigateToDirectoryAsync(content.FullPath, clearSearch: true, syncTree: false);
    }

    private async void DirectoryTree_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var node = DirectoryTree.SelectedNode;
        if (node?.Content is not DirectoryNodeContent content) return;

        try
        {
            if (node.IsExpanded)
            {
                node.IsExpanded = false;
            }
            else
            {
                await EnsureNodeChildrenLoadedAsync(node);
                node.IsExpanded = true;
            }

            await NavigateToDirectoryAsync(content.FullPath, clearSearch: true, syncTree: false);
        }
        catch (Exception ex)
        {
            StatusText.Text = "目录展开失败";
            ShowLoadMessage("无法展开资源目录", ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async void DirectoryBreadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index < 0 || args.Index >= _breadcrumbPaths.Count) return;
        await NavigateToDirectoryAsync(_breadcrumbPaths[args.Index], clearSearch: true);
    }

    private async void BrowserList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (BrowserList.SelectedItem is not AssetBrowserEntry { IsDirectory: true } directory) return;
        await NavigateToDirectoryAsync(directory.FullPath, clearSearch: true);
    }

    private async Task NavigateToDirectoryAsync(
        string? directoryPath,
        bool clearSearch,
        bool syncTree = true)
    {
        if (!_archiveService.IsLoaded) return;

        var normalized = string.IsNullOrWhiteSpace(directoryPath)
            ? string.Empty
            : directoryPath.Replace('\\', '/').Trim('/');

        if (clearSearch && !string.IsNullOrEmpty(AssetSearchBox.Text))
        {
            AssetSearchBox.Text = string.Empty;
        }

        var snapshot = await Task.Run(() => _archiveService.Browse(normalized));
        _currentDirectoryPath = normalized;
        ApplyDirectorySnapshot(snapshot);

        if (syncTree)
        {
            await SyncTreeToDirectoryAsync(normalized);
        }
    }

    private async Task SyncTreeToDirectoryAsync(string directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath))
        {
            DirectoryTree.SelectedNode = null;
            return;
        }

        var segments = directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = string.Empty;
        TreeViewNode? currentNode = null;

        for (var i = 0; i < segments.Length; i++)
        {
            currentPath = string.IsNullOrEmpty(currentPath)
                ? segments[i]
                : currentPath + "/" + segments[i];

            if (i == 0)
            {
                currentNode = FindNodeByPath(DirectoryTree.RootNodes, currentPath);
            }
            else
            {
                if (currentNode is null) return;
                await EnsureNodeChildrenLoadedAsync(currentNode);
                currentNode.IsExpanded = true;
                currentNode = FindNodeByPath(currentNode.Children, currentPath);
            }

            if (currentNode is null) return;
        }

        DirectoryTree.SelectedNode = currentNode;
    }

    private static TreeViewNode? FindNodeByPath(IEnumerable<TreeViewNode> nodes, string fullPath)
    {
        foreach (var node in nodes)
        {
            if (node.Content is DirectoryNodeContent content &&
                content.FullPath.Equals(fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }
        }

        return null;
    }

    private void ApplyDirectorySnapshot(AssetDirectorySnapshot snapshot)
    {
        BrowserList.ItemsSource = snapshot.Items;
        SearchCountText.Text = $"{snapshot.DirectoryCount:N0} 个文件夹 · {snapshot.ResourceCount:N0} 个资源";
        UpdateBreadcrumb(snapshot.DirectoryPath);
    }

    private void UpdateBreadcrumb(string directoryPath)
    {
        var labels = new List<string> { "资源根目录" };
        _breadcrumbPaths.Clear();
        _breadcrumbPaths.Add(string.Empty);

        if (!string.IsNullOrEmpty(directoryPath))
        {
            var current = string.Empty;
            foreach (var segment in directoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = string.IsNullOrEmpty(current) ? segment : current + "/" + segment;
                labels.Add(segment);
                _breadcrumbPaths.Add(current);
            }
        }

        DirectoryBreadcrumb.ItemsSource = labels;
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
            SearchCountText.Text = string.Empty;
            if (_archiveService.IsLoaded)
            {
                try
                {
                    await NavigateToDirectoryAsync(_currentDirectoryPath, clearSearch: false);
                }
                catch (Exception ex)
                {
                    StartupLog.Write("Failed to restore current directory after search", ex);
                }
            }
            else
            {
                BrowserList.ItemsSource = null;
            }
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

        BrowserList.ItemsSource = results;
        started.Stop();
        SearchCountText.Text = $"搜索结果 {results.Count:N0} 项 · {started.Elapsed.TotalMilliseconds:F0} ms";
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

    private sealed record DirectoryNodeContent(string Name, string FullPath)
    {
        public override string ToString() => Name;
    }

    private sealed class LoadingNodeContent
    {
        public static LoadingNodeContent Instance { get; } = new();
        public override string ToString() => "正在加载…";
    }
}
