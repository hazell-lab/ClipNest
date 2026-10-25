using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using ClipNest.Core.History;
using ClipNest.Core.Models;
using ClipNest.Core.Storage;
using ClipNest.Core.Text;

namespace ClipNest.App.ViewModels;

/// <summary>
/// what the history panel needs from the rest of the app.
/// </summary>
internal interface IPanelHost
{
    Task CopyEntryAsync(EntryViewModel entry, bool allowAutoPaste);

    void OpenCodePreview(EntryViewModel entry);

    bool Confirm(string title, string message, string confirmText, bool danger);

    string? Prompt(string title, string message, string? initialValue);

    void OpenSettings();

    void ToggleCapture();

    void HidePanel(bool restoreFocus);
}

internal enum PanelState
{
    Loading,
    Ready,
    Error,
}

/// <summary>
/// state and actions for the floating history panel.
/// </summary>
internal sealed class HistoryViewModel : ObservableObject
{
    private const int PageSize = 100;

    private readonly HistoryService _history;
    private readonly IPanelHost _host;
    private readonly DispatcherTimer _searchDelay;
    private readonly DispatcherTimer _noticeTimer;

    private string _searchText = "";
    private HistoryFilter _filter = HistoryFilter.All;
    private PanelState _state = PanelState.Loading;
    private string _errorMessage = "";
    private EntryViewModel? _selected;
    private bool _isCapturing = true;
    private string _footerText = "";
    private string? _notice;
    private bool _noticeIsWarning;
    private bool _hasMore;
    private bool _loadingMore;
    private int _generation;
    private int _recentLoaded;

    public HistoryViewModel(HistoryService history, IPanelHost host)
    {
        _history = history;
        _host = host;

        var view = new ListCollectionView(Items);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(EntryViewModel.SectionTitle)));
        ItemsView = view;

        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            await ReloadAsync();
        };

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            Notice = null;
        };

        CopyCommand = new AsyncCommand(p => p is EntryViewModel e ? _host.CopyEntryAsync(e, allowAutoPaste: true) : Task.CompletedTask);
        TogglePinCommand = new AsyncCommand(p => p is EntryViewModel e ? TogglePinAsync(e) : Task.CompletedTask);
        RenameCommand = new AsyncCommand(p => p is EntryViewModel e ? RenameAsync(e) : Task.CompletedTask);
        DeleteCommand = new AsyncCommand(p => p is EntryViewModel e ? DeleteAsync(e) : Task.CompletedTask);
        PreviewCommand = new RelayCommand(p =>
        {
            if (p is EntryViewModel { IsText: true } e)
            {
                _host.OpenCodePreview(e);
            }
        });
        ClearHistoryCommand = new AsyncCommand(_ => ClearHistoryAsync());
        ToggleCaptureCommand = new RelayCommand(_ => _host.ToggleCapture());
        OpenSettingsCommand = new RelayCommand(_ => _host.OpenSettings());
        CloseCommand = new RelayCommand(_ => _host.HidePanel(restoreFocus: true));
        RetryCommand = new AsyncCommand(_ => ReloadAsync());
        DismissNoticeCommand = new RelayCommand(_ => Notice = null);
        SetFilterCommand = new AsyncCommand(p => p is HistoryFilter f ? SetFilterAsync(f) : Task.CompletedTask);
    }

    public ObservableCollection<EntryViewModel> Items { get; } = new();

    public ICollectionView ItemsView { get; }

    public ICommand CopyCommand { get; }
    public ICommand TogglePinCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand ClearHistoryCommand { get; }
    public ICommand ToggleCaptureCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand DismissNoticeCommand { get; }
    public ICommand SetFilterCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
            {
                OnPropertyChanged(nameof(SearchHint));
                _searchDelay.Stop();
                _searchDelay.Start();
            }
        }
    }

    public HistoryFilter Filter
    {
        get => _filter;
        private set
        {
            if (SetProperty(ref _filter, value))
            {
                OnPropertyChanged(nameof(IsFilterAll));
                OnPropertyChanged(nameof(IsFilterText));
                OnPropertyChanged(nameof(IsFilterImages));
                OnPropertyChanged(nameof(IsFilterPinned));
                OnPropertyChanged(nameof(SearchHint));
            }
        }
    }

    public bool IsFilterAll
    {
        get => _filter == HistoryFilter.All;
        set => SetFilterFromRadio(HistoryFilter.All, value);
    }

    public bool IsFilterText
    {
        get => _filter == HistoryFilter.Text;
        set => SetFilterFromRadio(HistoryFilter.Text, value);
    }

    public bool IsFilterImages
    {
        get => _filter == HistoryFilter.Images;
        set => SetFilterFromRadio(HistoryFilter.Images, value);
    }

    public bool IsFilterPinned
    {
        get => _filter == HistoryFilter.Pinned;
        set => SetFilterFromRadio(HistoryFilter.Pinned, value);
    }

    // be honest that picture contents are not searchable
    public string SearchHint => _filter == HistoryFilter.Images
        ? "Search image titles (pinned images only)"
        : "Search text and pinned titles";

    public PanelState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(IsError));
                UpdateEmptyState();
            }
        }
    }

    public bool IsLoading => _state == PanelState.Loading;

    public bool IsError => _state == PanelState.Error;

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool ShowEmpty => _state == PanelState.Ready && Items.Count == 0;

    public string EmptyTitle => !string.IsNullOrWhiteSpace(_searchText)
        ? "No matches"
        : _filter switch
        {
            HistoryFilter.Pinned => "No pinned items yet",
            HistoryFilter.Images => "No images yet",
            HistoryFilter.Text => "No text yet",
            _ => "Your clipboard history is empty",
        };

    public string EmptyDetail => !string.IsNullOrWhiteSpace(_searchText)
        ? "Try a shorter or different search. Image contents are not searchable."
        : _filter == HistoryFilter.Pinned
            ? "Pin an item to keep it here. Pinned items are never removed automatically."
            : _isCapturing
                ? "Copy some text or an image and it will show up here."
                : "Capture is paused. Resume it to start saving copies again.";

    public EntryViewModel? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                OnPropertyChanged(nameof(CaptureLabel));
                OnPropertyChanged(nameof(CaptureGlyph));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(EmptyDetail));
            }
        }
    }

    public bool IsPaused => !_isCapturing;

    public string CaptureLabel => _isCapturing ? "Capturing" : "Paused";

    public string CaptureGlyph => _isCapturing ? "" : "";

    public string FooterText
    {
        get => _footerText;
        private set => SetProperty(ref _footerText, value);
    }

    public string? Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    public bool NoticeIsWarning
    {
        get => _noticeIsWarning;
        private set => SetProperty(ref _noticeIsWarning, value);
    }

    public bool HasMore
    {
        get => _hasMore;
        private set => SetProperty(ref _hasMore, value);
    }

    public void ShowNotice(string message, bool warning = false)
    {
        NoticeIsWarning = warning;
        Notice = message;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    public async Task OnPanelShownAsync()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in Items)
        {
            item.RefreshTime(now);
        }

        // reloading is cheap and keeps the order right after copies and cleanups
        await ReloadAsync();
        await RefreshFooterAsync();
    }

    public async Task ReloadAsync()
    {
        var generation = ++_generation;
        var keepId = Selected?.Id;
        if (Items.Count == 0)
        {
            State = PanelState.Loading;
        }

        try
        {
            var search = _searchText;
            var pinned = await _history.GetPinnedAsync(search, _filter);
            var recent = await _history.GetRecentAsync(search, _filter, 0, PageSize);

            // a newer reload started while this one was waiting, so drop these results
            if (generation != _generation)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            Items.Clear();
            foreach (var entry in pinned.Concat(recent))
            {
                Add(new EntryViewModel(entry, now));
            }

            _recentLoaded = recent.Count;
            HasMore = recent.Count == PageSize;
            Selected = Items.FirstOrDefault(i => i.Id == keepId) ?? Items.FirstOrDefault();
            State = PanelState.Ready;
            UpdateEmptyState();
        }
        catch (HistoryStorageException ex)
        {
            if (generation != _generation)
            {
                return;
            }

            ErrorMessage = ex.Message;
            Items.Clear();
            State = PanelState.Error;
        }
    }

    /// <summary>
    /// loads the next page of older items when the user scrolls near the end.
    /// </summary>
    public async Task LoadMoreAsync()
    {
        if (!_hasMore || _loadingMore || _state != PanelState.Ready)
        {
            return;
        }

        _loadingMore = true;
        var generation = _generation;
        try
        {
            var page = await _history.GetRecentAsync(_searchText, _filter, _recentLoaded, PageSize);
            if (generation != _generation)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var entry in page)
            {
                if (Items.Any(i => i.Id == entry.Id))
                {
                    continue;
                }

                Add(new EntryViewModel(entry, now));
            }

            _recentLoaded += page.Count;
            HasMore = page.Count == PageSize;
        }
        catch (HistoryStorageException ex)
        {
            ShowNotice(ex.Message, warning: true);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    public async Task RefreshFooterAsync()
    {
        try
        {
            var report = await _history.GetStorageReportAsync();
            var items = report.EntryCount == 1 ? "1 item" : $"{report.EntryCount:N0} items";
            FooterText = $"{items} · about {ByteSize.Format(report.TotalBytes)} on this PC";
        }
        catch (HistoryStorageException)
        {
            FooterText = "Storage unavailable";
        }
    }

    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        // the view keeps pinned rows first, the same order as the list on screen
        var ordered = ItemsView.Cast<EntryViewModel>().ToList();
        var index = Selected is null ? -1 : ordered.IndexOf(Selected);
        var next = Math.Clamp(index + delta, 0, ordered.Count - 1);
        Selected = ordered[next];

        if (next >= ordered.Count - 5)
        {
            _ = LoadMoreAsync();
        }
    }

    public void SelectFirst() => Selected = ItemsView.Cast<EntryViewModel>().FirstOrDefault();

    public void ResetForOpen()
    {
        if (_searchText.Length > 0)
        {
            _searchText = "";
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(SearchHint));
        }
    }

    private void Add(EntryViewModel item)
    {
        Items.Add(item);
        if (item.IsImage)
        {
            _ = item.LoadThumbnailAsync(_history.Images);
        }
    }

    private void SetFilterFromRadio(HistoryFilter filter, bool isChecked)
    {
        if (isChecked && _filter != filter)
        {
            _ = SetFilterAsync(filter);
        }
    }

    private async Task SetFilterAsync(HistoryFilter filter)
    {
        Filter = filter;
        await ReloadAsync();
    }

    private async Task TogglePinAsync(EntryViewModel entry)
    {
        try
        {
            await _history.SetPinnedAsync(entry.Id, !entry.IsPinned);
            await ReloadAsync();
        }
        catch (HistoryStorageException ex)
        {
            ShowNotice(ex.Message, warning: true);
        }
    }

    private async Task RenameAsync(EntryViewModel entry)
    {
        if (!entry.IsPinned)
        {
            ShowNotice("Pin the item first, then you can give it a title.");
            return;
        }

        var title = _host.Prompt("Pin title", "Give this pinned item a short title. Leave it empty to remove the title.", entry.PinTitle);
        if (title is null)
        {
            return;
        }

        try
        {
            await _history.RenamePinAsync(entry.Id, title);
            await ReloadAsync();
        }
        catch (HistoryStorageException ex)
        {
            ShowNotice(ex.Message, warning: true);
        }
    }

    private async Task DeleteAsync(EntryViewModel entry)
    {
        // pinned items are kept on purpose, so ask before removing one
        if (entry.IsPinned && !_host.Confirm(
                "Delete pinned item?",
                "This pinned item will be removed from your history for good.",
                "Delete",
                danger: true))
        {
            return;
        }

        var ordered = ItemsView.Cast<EntryViewModel>().ToList();
        var index = ordered.IndexOf(entry);
        try
        {
            if (await _history.DeleteAsync(entry.Id))
            {
                Items.Remove(entry);
                _recentLoaded = Math.Max(0, _recentLoaded - (entry.IsPinned ? 0 : 1));
                var remaining = ItemsView.Cast<EntryViewModel>().ToList();
                Selected = remaining.Count == 0 ? null : remaining[Math.Clamp(index, 0, remaining.Count - 1)];
                UpdateEmptyState();
                await RefreshFooterAsync();
            }
        }
        catch (HistoryStorageException ex)
        {
            ShowNotice(ex.Message, warning: true);
        }
    }

    private async Task ClearHistoryAsync()
    {
        if (!_host.Confirm(
                "Clear history?",
                "All unpinned items and their images will be deleted from this PC. Pinned items are kept.",
                "Clear history",
                danger: true))
        {
            return;
        }

        try
        {
            var removed = await _history.ClearUnpinnedAsync();
            await ReloadAsync();
            await RefreshFooterAsync();
            ShowNotice(removed == 1 ? "1 item deleted." : $"{removed:N0} items deleted.");
        }
        catch (HistoryStorageException ex)
        {
            ShowNotice(ex.Message, warning: true);
        }
    }

    private void UpdateEmptyState()
    {
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDetail));
    }

}
