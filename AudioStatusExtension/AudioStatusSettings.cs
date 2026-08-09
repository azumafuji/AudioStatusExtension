// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AudioStatusExtension;

internal sealed partial class AudioStatusSettings : JsonSettingsManager, IDisposable
{
    private const string DeviceNameFormatKey = "deviceNameFormat";
    private const string WindowsDisplayNameValue = "windowsDisplayName";
    private const string AudioAdapterValue = "audioAdapter";
    private const string SettingsFileName = "audioStatus.settings.json";

    private readonly ChoiceSetSetting _deviceNameFormat = new(
        DeviceNameFormatKey,
        "Device name format",
        "Choose how audio devices are identified in Command Palette.",
        new List<ChoiceSetSetting.Choice>
        {
            new("Windows display name", WindowsDisplayNameValue),
            new("Audio adapter", AudioAdapterValue),
        });

    public AudioStatusSettings()
    {
        FilePath = GetSettingsFilePath();
        Settings.Add(_deviceNameFormat);
        LoadSettings();
        Settings.SettingsChanged += OnSettingsChanged;
    }

    public AudioDeviceNameFormat DeviceNameFormat =>
        _deviceNameFormat.Value == AudioAdapterValue
            ? AudioDeviceNameFormat.AudioAdapter
            : AudioDeviceNameFormat.WindowsDisplayName;

    public void Dispose()
    {
        Settings.SettingsChanged -= OnSettingsChanged;
    }

    private static string GetSettingsFilePath()
    {
        var settingsDirectory = Utilities.BaseSettingsPath("Microsoft.CmdPal");
        Directory.CreateDirectory(settingsDirectory);
        return Path.Combine(settingsDirectory, SettingsFileName);
    }

    private void OnSettingsChanged(object sender, Settings args)
    {
        SaveSettings();
    }
}
