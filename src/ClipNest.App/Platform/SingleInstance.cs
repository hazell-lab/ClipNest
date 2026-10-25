using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClipNest.App.Platform;

/// <summary>
/// makes sure only one copy of the app runs. a second launch asks the
/// first one to open its panel and then exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string Name = @"Local\ClipNest-6f1d2a7c-93b4-4c1e-8e57-0b8a4c2d9e31";
    private const int AsfwAny = -1;

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showSignal;
    private RegisteredWaitHandle? _registration;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    private SingleInstance(Mutex mutex, EventWaitHandle showSignal)
    {
        _mutex = mutex;
        _showSignal = showSignal;
    }

    /// <summary>
    /// returns null when another copy is already running.
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, Name, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();

            // allow the running copy to bring its panel to the front
            AllowSetForegroundWindow(AsfwAny);
            try
            {
                using var signal = EventWaitHandle.OpenExisting(Name + "-show");
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // the other copy is still starting up
            }

            return null;
        }

        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, Name + "-show");
        return new SingleInstance(mutex, showSignal);
    }

    /// <summary>
    /// runs the callback on a pool thread whenever another launch asks us to show up.
    /// </summary>
    public void ListenForActivation(Action onActivate)
    {
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _showSignal,
            (_, _) => onActivate(),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _showSignal.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // already released
        }

        _mutex.Dispose();
    }
}
