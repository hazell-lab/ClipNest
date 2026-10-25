using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using ClipNest.App.Platform;
using ClipNest.Core.Capture;
using ClipNest.Core.Diagnostics;
using ClipNest.Core.History;
using ClipNest.Core.Models;
using ClipNest.Core.Settings;
using ClipNest.Core.Storage;

namespace ClipNest.App.Services;

/// <summary>
/// connects clipboard change events to the history: it waits briefly,
/// checks privacy rules, reads the content and saves it.
/// </summary>
internal sealed class CaptureCoordinator : IDisposable
{
    private readonly ClipboardListener _listener;
    private readonly ClipboardAccess _clipboard;
    private readonly HistoryService _history;
    private readonly Func<AppSettings> _settings;
    private readonly DispatcherTimer _settleTimer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private uint _lastSequence;
    private bool _pending;

    public CaptureCoordinator(ClipboardListener listener, ClipboardAccess clipboard, HistoryService history, Func<AppSettings> settings)
    {
        _listener = listener;
        _clipboard = clipboard;
        _history = history;
        _settings = settings;

        // apps often set several formats in a row, so wait for them to finish
        _settleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _settleTimer.Tick += OnSettled;
        _listener.ClipboardChanged += OnClipboardChanged;
    }

    /// <summary>
    /// raised with a short message when a copy was not saved. never contains clipboard content.
    /// </summary>
    public event EventHandler<string>? CaptureSkipped;

    public event EventHandler? CaptureSaved;

    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private async void OnSettled(object? sender, EventArgs e)
    {
        _settleTimer.Stop();

        // only one capture runs at a time, later changes wait their turn
        if (!await _gate.WaitAsync(0))
        {
            _pending = true;
            return;
        }

        try
        {
            do
            {
                _pending = false;
                await CaptureOnceAsync();
            }
            while (_pending);
        }
        catch (Exception ex)
        {
            AppLog.Error("clipboard capture failed", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CaptureOnceAsync()
    {
        var sequence = ClipboardAccess.CurrentSequenceNumber;
        if (sequence == _lastSequence)
        {
            return;
        }

        _lastSequence = sequence;
        var settings = _settings();
        if (!settings.CaptureEnabled || !_history.IsReady)
        {
            return;
        }

        var source = WindowHelpers.GuessClipboardSourceApp();
        var excluded = AppExclusionList.IsExcluded(settings.ExcludedApps, source);

        var read = await _clipboard.ReadAsync(formats =>
            CapturePolicy.Decide(formats, captureEnabled: true, settings.CaptureImages, excluded));

        if (read.Failure != SkipReason.None)
        {
            Report(read.Failure, read.Formats);
            return;
        }

        if (read.Decision.Action == CaptureAction.Skip)
        {
            Report(read.Decision.Reason, read.Formats);
            return;
        }

        try
        {
            CaptureResult result;
            if (read.Text is not null)
            {
                result = await _history.CaptureTextAsync(read.Text, source);
            }
            else if (read.Image is not null)
            {
                var raw = read.Image;
                var decoded = await Task.Run(() =>
                {
                    var image = ImageCodec.Decode(raw, out var failure);
                    return (image, failure);
                });

                if (decoded.image is null)
                {
                    Report(decoded.failure, read.Formats);
                    return;
                }

                result = await _history.CaptureImageAsync(decoded.image, source);
            }
            else
            {
                return;
            }

            if (result.Outcome == CaptureOutcome.Skipped)
            {
                Report(result.Reason, read.Formats);
            }
            else if (result.ChangedHistory)
            {
                CaptureSaved?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (HistoryStorageException ex)
        {
            CaptureSkipped?.Invoke(this, ex.Message);
        }
    }

    private void Report(SkipReason reason, ClipboardFormatsInfo formats)
    {
        var message = CapturePolicy.DescribeSkip(reason, formats.HasFileList);
        if (message is not null)
        {
            AppLog.Info("capture skipped: " + reason);
            CaptureSkipped?.Invoke(this, message);
        }
    }

    public void Dispose()
    {
        _settleTimer.Stop();
        _listener.ClipboardChanged -= OnClipboardChanged;
    }
}
