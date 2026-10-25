using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClipNest.Core.Capture;
using ClipNest.Core.Diagnostics;
using ClipNest.Core.Models;
using ClipNest.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ClipNest.Core.History;

public enum CaptureOutcome
{
    Added,
    MovedToTop,
    DuplicateIgnored,
    IgnoredSelfCopy,
    Skipped,
}

public sealed record CaptureResult(CaptureOutcome Outcome, long? EntryId = null, SkipReason Reason = SkipReason.None)
{
    public static CaptureResult Skip(SkipReason reason) => new(CaptureOutcome.Skipped, null, reason);

    public bool ChangedHistory => Outcome is CaptureOutcome.Added or CaptureOutcome.MovedToTop;
}

public sealed record StorageReport(int EntryCount, int PinnedCount, long DatabaseBytes, long ImageBytes)
{
    public long TotalBytes => DatabaseBytes + ImageBytes;
}

/// <summary>
/// the main history logic: saving captures, skipping repeats,
/// copying back, pinning, deleting and cleanup.
/// all methods do their disk work on background threads.
/// </summary>
public sealed class HistoryService
{
    private readonly ClipDatabase _database;
    private readonly ClipRepository _repository;
    private readonly ImageStore _images;
    private readonly SelfCopyGuard _guard;
    private readonly TimeProvider _time;

    // captures run one at a time so repeat checks stay correct
    private readonly SemaphoreSlim _captureLock = new(1, 1);

    public HistoryService(ClipDatabase database, ImageStore images, SelfCopyGuard guard, TimeProvider? time = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _time = time ?? TimeProvider.System;
        _repository = new ClipRepository(database);
    }

    /// <summary>
    /// raised after the saved history changes. it can fire on any thread.
    /// </summary>
    public event EventHandler? HistoryChanged;

    public bool IsReady { get; private set; }

    public string? InitializationError { get; private set; }

    public ImageStore Images => _images;

    public async Task<DatabaseOpenResult> InitializeAsync()
    {
        try
        {
            var result = await Task.Run(() =>
            {
                var opened = _database.Initialize();

                // remove image files left behind by a crash or an old version
                var removed = _images.DeleteOrphans(_repository.GetReferencedFiles());
                if (removed > 0)
                {
                    AppLog.Info($"removed {removed} unused image files");
                }

                return opened;
            }).ConfigureAwait(false);

            IsReady = true;
            InitializationError = null;
            return result;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            IsReady = false;
            InitializationError = ex.Message;
            AppLog.Error("history storage could not be opened", ex);
            throw new HistoryStorageException("Clipboard history storage could not be opened. " + ex.Message, ex);
        }
    }

    public async Task<CaptureResult> CaptureTextAsync(string? text, string? sourceApp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return CaptureResult.Skip(SkipReason.Empty);
        }

        if (text.Length > CaptureLimits.MaxTextChars)
        {
            return CaptureResult.Skip(SkipReason.TooLarge);
        }

        var hash = ContentHasher.ForText(text);
        if (_guard.IsSelfCopy(hash))
        {
            return new CaptureResult(CaptureOutcome.IgnoredSelfCopy);
        }

        var size = (long)System.Text.Encoding.UTF8.GetByteCount(text);
        return await CaptureCoreAsync(hash, sourceApp, cancellationToken, now => new NewEntry
        {
            Kind = ClipKind.Text,
            Text = text,
            ContentHash = hash,
            ByteSize = size,
            CreatedAt = now,
            SourceApp = sourceApp,
        }).ConfigureAwait(false);
    }

    public async Task<CaptureResult> CaptureImageAsync(CapturedImage image, string? sourceApp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 || image.PngBytes.Length == 0)
        {
            return CaptureResult.Skip(SkipReason.Empty);
        }

        if (image.Width > CaptureLimits.MaxImageDimension
            || image.Height > CaptureLimits.MaxImageDimension
            || (long)image.Width * image.Height > CaptureLimits.MaxImagePixels
            || image.PngBytes.LongLength > CaptureLimits.MaxEncodedImageBytes)
        {
            return CaptureResult.Skip(SkipReason.TooLarge);
        }

        if (_guard.IsSelfCopy(image.PixelHash))
        {
            return new CaptureResult(CaptureOutcome.IgnoredSelfCopy);
        }

        return await CaptureCoreAsync(image.PixelHash, sourceApp, cancellationToken, now =>
        {
            // files are written only when a new entry is really needed
            var (imageFile, thumbFile) = _images.Save(image.PngBytes, image.ThumbnailPngBytes);
            return new NewEntry
            {
                Kind = ClipKind.Image,
                ContentHash = image.PixelHash,
                ImageFile = imageFile,
                ThumbnailFile = thumbFile,
                ImageWidth = image.Width,
                ImageHeight = image.Height,
                ByteSize = image.PngBytes.LongLength + image.ThumbnailPngBytes.LongLength,
                CreatedAt = now,
                SourceApp = sourceApp,
            };
        }).ConfigureAwait(false);
    }

    private async Task<CaptureResult> CaptureCoreAsync(
        string hash,
        string? sourceApp,
        CancellationToken cancellationToken,
        Func<DateTimeOffset, NewEntry> createEntry)
    {
        EnsureReady();
        await _captureLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        CaptureResult result;
        try
        {
            result = await Task.Run(() =>
            {
                var now = _time.GetUtcNow();

                // the same content as the newest entry is a consecutive repeat
                var latest = _repository.GetMostRecent();
                if (latest is not null && latest.ContentHash == hash)
                {
                    return new CaptureResult(CaptureOutcome.DuplicateIgnored, latest.Id);
                }

                // older copies of the same content move to the top instead of being saved twice
                var existing = _repository.FindByHash(hash);
                if (existing is not null)
                {
                    _repository.Touch(existing.Id, now);
                    return new CaptureResult(CaptureOutcome.MovedToTop, existing.Id);
                }

                var entry = createEntry(now);
                try
                {
                    var id = _repository.Insert(entry);
                    return new CaptureResult(CaptureOutcome.Added, id);
                }
                catch
                {
                    // do not leave image files behind if the row was not saved
                    _images.Delete(entry.ImageFile, entry.ThumbnailFile);
                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            AppLog.Error("a clipboard capture could not be saved", ex);
            throw new HistoryStorageException("The clipboard item could not be saved.", ex);
        }
        finally
        {
            _captureLock.Release();
        }

        if (result.ChangedHistory)
        {
            OnChanged();
        }

        return result;
    }

    /// <summary>
    /// gets everything needed to copy an entry back, and marks its content
    /// so the clipboard change that follows is not saved again.
    /// </summary>
    public async Task<CopyPayload?> PrepareCopyAsync(long id)
    {
        EnsureReady();
        var payload = await RunStorageAsync(() =>
        {
            var entry = _repository.GetById(id);
            if (entry is null)
            {
                return null;
            }

            _guard.Expect(entry.ContentHash);
            _repository.Touch(id, _time.GetUtcNow());

            if (entry.Kind == ClipKind.Text)
            {
                return new CopyPayload { Entry = entry, Text = _repository.GetText(id) ?? "" };
            }

            var path = _images.GetFullPath(entry.ImageFile);
            return new CopyPayload
            {
                Entry = entry,
                ImagePath = path,
                ImageFileMissing = path is null || !File.Exists(path),
            };
        }).ConfigureAwait(false);

        if (payload is not null)
        {
            OnChanged();
        }

        return payload;
    }

    public Task<ClipEntry?> GetAsync(long id)
    {
        EnsureReady();
        return RunStorageAsync(() => _repository.GetById(id));
    }

    public Task<string?> GetFullTextAsync(long id)
    {
        EnsureReady();
        return RunStorageAsync(() => _repository.GetText(id));
    }

    public Task<IReadOnlyList<ClipEntry>> GetPinnedAsync(string? search, HistoryFilter filter, int limit = 500)
    {
        EnsureReady();
        return RunStorageAsync(() => _repository.Query(new EntryQuery(true, KindFor(filter), search, 0, limit)));
    }

    public Task<IReadOnlyList<ClipEntry>> GetRecentAsync(string? search, HistoryFilter filter, int offset, int limit)
    {
        EnsureReady();
        if (filter == HistoryFilter.Pinned)
        {
            return Task.FromResult<IReadOnlyList<ClipEntry>>(Array.Empty<ClipEntry>());
        }

        return RunStorageAsync(() => _repository.Query(new EntryQuery(false, KindFor(filter), search, offset, limit)));
    }

    public async Task<bool> SetPinnedAsync(long id, bool pinned)
    {
        EnsureReady();
        var changed = await RunStorageAsync(() => _repository.SetPinned(id, pinned, _time.GetUtcNow())).ConfigureAwait(false);
        if (changed)
        {
            OnChanged();
        }

        return changed;
    }

    public async Task<bool> RenamePinAsync(long id, string? title)
    {
        EnsureReady();
        var changed = await RunStorageAsync(() => _repository.SetPinTitle(id, title)).ConfigureAwait(false);
        if (changed)
        {
            OnChanged();
        }

        return changed;
    }

    public Task<bool> SetCodeLanguageAsync(long id, string? languageId)
    {
        EnsureReady();
        return RunStorageAsync(() => _repository.SetCodeLanguage(id, languageId));
    }

    public async Task<bool> DeleteAsync(long id)
    {
        EnsureReady();
        var result = await RunStorageAsync(() => FinishDeletion(_repository.Delete(id))).ConfigureAwait(false);
        if (result.DeletedCount > 0)
        {
            OnChanged();
        }

        return result.DeletedCount > 0;
    }

    public async Task<int> ClearUnpinnedAsync()
    {
        EnsureReady();
        var result = await RunStorageAsync(() => FinishDeletion(_repository.DeleteAllUnpinned(), compact: true)).ConfigureAwait(false);
        OnChanged();
        return result.DeletedCount;
    }

    /// <summary>
    /// removes every entry, pinned ones too, and every stored image.
    /// </summary>
    public async Task<int> DeleteEverythingAsync()
    {
        EnsureReady();
        var result = await RunStorageAsync(() =>
        {
            var deleted = FinishDeletion(_repository.DeleteEverything(), compact: true);
            _images.DeleteAllOwnFiles();
            return deleted;
        }).ConfigureAwait(false);
        OnChanged();
        return result.DeletedCount;
    }

    public async Task<int> ApplyRetentionAsync(RetentionLimits limits)
    {
        EnsureReady();
        var result = await RunStorageAsync(() =>
            FinishDeletion(_repository.ApplyRetention(limits, _time.GetUtcNow()))).ConfigureAwait(false);
        if (result.DeletedCount > 0)
        {
            OnChanged();
        }

        return result.DeletedCount;
    }

    public Task<StorageReport> GetStorageReportAsync()
    {
        EnsureReady();
        return RunStorageAsync(() =>
        {
            var counts = _repository.GetCounts();
            long databaseBytes = 0;
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var info = new FileInfo(_database.DatabasePath + suffix);
                if (info.Exists)
                {
                    databaseBytes += info.Length;
                }
            }

            return new StorageReport(counts.Total, counts.Pinned, databaseBytes, _images.MeasureBytes());
        });
    }

    private DeletionResult FinishDeletion(DeletionResult result, bool compact = false)
    {
        // rows are gone, now remove the files they used
        if (result.ImageFiles.Count > 0)
        {
            _images.Delete(result.ImageFiles);
        }

        if (compact || result.DeletedCount > 20)
        {
            try
            {
                _repository.ReleaseFreeSpace();
            }
            catch (SqliteException ex)
            {
                AppLog.Warn("could not compact the database: " + ex.SqliteErrorCode);
            }
        }

        return result;
    }

    private async Task<T> RunStorageAsync<T>(Func<T> work)
    {
        try
        {
            return await Task.Run(work).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            AppLog.Error("a history storage operation failed", ex);
            throw new HistoryStorageException("Clipboard history storage is not available right now.", ex);
        }
    }

    private void EnsureReady()
    {
        if (!IsReady)
        {
            throw new HistoryStorageException(InitializationError is null
                ? "Clipboard history is still starting."
                : "Clipboard history storage could not be opened. " + InitializationError);
        }
    }

    private void OnChanged()
    {
        try
        {
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error("a history listener failed", ex);
        }
    }

    private static ClipKind? KindFor(HistoryFilter filter) => filter switch
    {
        HistoryFilter.Text => ClipKind.Text,
        HistoryFilter.Images => ClipKind.Image,
        _ => null,
    };

    private static bool IsStorageError(Exception ex) =>
        ex is SqliteException or IOException or UnauthorizedAccessException or HistoryStorageException;
}
