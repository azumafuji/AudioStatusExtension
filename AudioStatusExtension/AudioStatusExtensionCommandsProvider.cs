// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AudioStatusExtension;

public partial class AudioStatusExtensionCommandsProvider : CommandProvider
{
    private const string DeviceNameFormatKey = "deviceNameFormat";
    private const string WindowsDisplayNameValue = "windowsDisplayName";
    private const string AudioAdapterValue = "audioAdapter";
    private static readonly TimeSpan ListenerHealthCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ListenerStaleAfter = TimeSpan.FromMinutes(30);
    private readonly ICommandItem[] _commands;
    private readonly AudioStatusExtensionPage _page;
    private readonly AudioDevicesPage _outputDevicesPage;
    private readonly AudioDevicesPage _inputDevicesPage;
    private readonly AudioStatusDockBand _dockBand;
    private readonly Timer _refreshDebounceTimer;
    private readonly Timer _listenerHealthTimer;
    private readonly Action _scheduleRefreshCallback;
    private readonly object _refreshLock = new();
    private readonly Settings _extensionSettings = new();
    private IDisposable? _audioDeviceWatcher;
    private AudioStatusSnapshot _cachedSnapshot;
    private AudioStatusSnapshot? _unreliableSnapshotCandidate;
    private long _lastCallbackUtcTicks;
    private bool _listenerRegistrationSucceeded;
    private bool _disposed;

    public AudioStatusExtensionCommandsProvider()
    {
        DisplayName = "Audio Status";
        Icon = IconHelpers.FromRelativePath("Public\\StoreLogo.png");
        _extensionSettings.Add(new ChoiceSetSetting(
            DeviceNameFormatKey,
            new List<ChoiceSetSetting.Choice>
            {
                new("Windows display name", WindowsDisplayNameValue),
                new("Audio adapter", AudioAdapterValue),
            })
        {
            Label = "Device name format",
            Description = "Choose how audio devices are identified in Command Palette.",
        });
        Settings = _extensionSettings;
        ApplyDeviceNameFormat();
        _extensionSettings.SettingsChanged += OnSettingsChanged;
        _scheduleRefreshCallback = CreateWeakScheduleRefreshCallback(this);
        _page = new AudioStatusExtensionPage(_scheduleRefreshCallback);
        _outputDevicesPage = new AudioDevicesPage(AudioDeviceKind.Output, _scheduleRefreshCallback);
        _inputDevicesPage = new AudioDevicesPage(AudioDeviceKind.Input, _scheduleRefreshCallback);
        _dockBand = new AudioStatusDockBand(_scheduleRefreshCallback);
        _commands = [
            new CommandItem(_page) { Title = DisplayName },
            new CommandItem(_outputDevicesPage)
            {
                Title = "Switch output device",
                Subtitle = "Choose the default speakers or headphones",
                Icon = new IconInfo("\uE767"),
            },
            new CommandItem(_inputDevicesPage)
            {
                Title = "Switch input device",
                Subtitle = "Choose the default microphone",
                Icon = new IconInfo("\uE720"),
            },
        ];
        _refreshDebounceTimer = new Timer(Refresh, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _cachedSnapshot = AudioDeviceService.GetSnapshot();
        RecordCallbackActivity();
        InitializeListener("startup");
        _listenerHealthTimer = new Timer(
            CheckListenerHealth,
            null,
            ListenerHealthCheckInterval,
            ListenerHealthCheckInterval);
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    public override ICommandItem[] GetDockBands()
    {
        return [_dockBand];
    }

    public override void Dispose()
    {
        lock (_refreshLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _extensionSettings.SettingsChanged -= OnSettingsChanged;
            _listenerHealthTimer.Dispose();
            _refreshDebounceTimer.Dispose();
            DisposeListener();
        }

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ScheduleRefresh()
    {
        try
        {
            _refreshDebounceTimer.Change(TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnSettingsChanged(object sender, Settings args)
    {
        ApplyDeviceNameFormat();
        ScheduleRefresh();
    }

    private void ApplyDeviceNameFormat()
    {
        AudioDeviceService.DeviceNameFormat =
            _extensionSettings.GetSetting<string>(DeviceNameFormatKey) == AudioAdapterValue
                ? AudioDeviceNameFormat.AudioAdapter
                : AudioDeviceNameFormat.WindowsDisplayName;
    }

    private static Action CreateWeakScheduleRefreshCallback(AudioStatusExtensionCommandsProvider provider)
    {
        var weakProvider = new WeakReference<AudioStatusExtensionCommandsProvider>(provider);
        return () =>
        {
            if (weakProvider.TryGetTarget(out var target))
            {
                target.OnAudioDeviceCallback();
            }
        };
    }

    private void OnAudioDeviceCallback()
    {
        RecordCallbackActivity();
        Log("Audio device callback fired.");
        ScheduleRefresh();
    }

    private void Refresh()
    {
        lock (_refreshLock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                // Always ask Windows for the current defaults. The callback is only a prompt;
                // it is never the source of truth.
                _cachedSnapshot = AudioDeviceService.GetSnapshot();
                _dockBand.Refresh();
                _page.Refresh();
                _outputDevicesPage.RefreshItems();
                _inputDevicesPage.RefreshItems();
            }
            catch (Exception ex)
            {
                // Timer and native audio callbacks run outside the Command Palette call stack.
                // A transient refresh failure must not terminate the extension or its watcher.
                Log($"Status refresh failed: {ex.Message}");
            }
        }
    }

    private void Refresh(object? state)
    {
        Refresh();
    }

    private void CheckListenerHealth(object? state)
    {
        lock (_refreshLock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var currentSnapshot = AudioDeviceService.GetSnapshot();
                var stateChanged = IsConfirmedStateChange(currentSnapshot);
                var lastCallbackAt = new DateTimeOffset(
                    Interlocked.Read(ref _lastCallbackUtcTicks),
                    TimeSpan.Zero);
                var callbackStale = DateTimeOffset.UtcNow - lastCallbackAt >= ListenerStaleAfter;

                if (stateChanged)
                {
                    Log("Current Windows audio state differs from cached state; refreshing and reinitializing listener.");
                    _cachedSnapshot = currentSnapshot;
                    _dockBand.Refresh();
                    _page.Refresh();
                    _outputDevicesPage.RefreshItems();
                    _inputDevicesPage.RefreshItems();
                }

                if (stateChanged || callbackStale || !_listenerRegistrationSucceeded)
                {
                    var reason = stateChanged
                        ? "missed device change"
                        : _listenerRegistrationSucceeded
                            ? "callback health timeout"
                            : "registration retry";
                    InitializeListener(reason);
                }
            }
            catch (Exception ex)
            {
                Log($"Listener health check failed: {ex.Message}");
            }
        }
    }

    private void InitializeListener(string reason)
    {
        // Unregister the old callback before registering its replacement, so there is
        // never more than one active registration owned by this provider.
        DisposeListener();
        _listenerRegistrationSucceeded = AudioDeviceService.TryWatchDefaultDeviceChanges(
            _scheduleRefreshCallback,
            out var watcher);
        _audioDeviceWatcher = watcher;
        RecordCallbackActivity();
        Log(_listenerRegistrationSucceeded
            ? $"Audio device listener registered ({reason})."
            : $"Audio device listener registration failed ({reason}); retrying on the next health check.");
    }

    private void RecordCallbackActivity()
    {
        Interlocked.Exchange(ref _lastCallbackUtcTicks, DateTimeOffset.UtcNow.Ticks);
    }

    private void DisposeListener()
    {
        var watcher = _audioDeviceWatcher;
        _audioDeviceWatcher = null;
        _listenerRegistrationSucceeded = false;
        if (watcher is null)
        {
            return;
        }

        try
        {
            watcher.Dispose();
            Log("Audio device listener unregistered.");
        }
        catch (Exception ex)
        {
            // A failed native unregistration must not prevent shutdown or a replacement
            // listener from being created.
            Log($"Audio device listener unregistration failed: {ex.Message}");
        }
    }

    private bool IsConfirmedStateChange(AudioStatusSnapshot currentSnapshot)
    {
        if (_cachedSnapshot.HasSameDevices(currentSnapshot))
        {
            _unreliableSnapshotCandidate = null;
            return false;
        }

        if (currentSnapshot.IsReliable())
        {
            _unreliableSnapshotCandidate = null;
            return true;
        }

        // A transient Core Audio query failure can produce an unavailable snapshot.
        // Require the same result twice before treating it as real device state.
        if (_unreliableSnapshotCandidate?.HasSameDevices(currentSnapshot) == true)
        {
            _unreliableSnapshotCandidate = null;
            return true;
        }

        _unreliableSnapshotCandidate = currentSnapshot;
        Log("Ignoring one unconfirmed unavailable audio snapshot.");
        return false;
    }

    private static void Log(string message)
    {
        Console.WriteLine($"[{DateTimeOffset.Now:O}] [AudioStatus] {message}");
    }
}
