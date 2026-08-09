using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AudioStatusExtension;

internal sealed partial class AudioDevicesPage : ListPage
{
    private readonly AudioDeviceTarget _target;
    private readonly Action _onChanged;

    public AudioDevicesPage(AudioDeviceTarget target, Action onChanged)
    {
        _target = target;
        _onChanged = onChanged;
        Icon = new IconInfo(GetIconGlyph(target));
        Title = $"{GetTargetLabel(target)} devices";
        Name = $"Switch {GetTargetLabel(target).ToLowerInvariant()} device";
    }

    public override IListItem[] GetItems()
    {
        var devices = AudioDeviceService.GetDevices(_target);
        if (devices.Length == 0)
        {
            return
            [
                new ListItem(new NoOpCommand())
                {
                    Title = "No active devices found",
                    Subtitle = GetTargetLabel(_target),
                    Icon = Icon,
                },
            ];
        }

        var items = new IListItem[devices.Length];
        for (var index = 0; index < devices.Length; index++)
        {
            var device = devices[index];
            items[index] = new ListItem(new SetDefaultAudioDeviceCommand(_target, device, Refresh))
            {
                Title = device.Name,
                Subtitle = device.IsDefault ? $"{GetTargetLabel(_target)} - current" : GetTargetLabel(_target),
                Icon = Icon,
            };
        }

        return items;
    }

    public void RefreshItems()
    {
        RaiseItemsChanged();
    }

    internal static string GetTargetLabel(AudioDeviceTarget target)
    {
        return target switch
        {
            { Kind: AudioDeviceKind.Output, Role: AudioEndpointRole.Communications } => "Communications output",
            { Kind: AudioDeviceKind.Input, Role: AudioEndpointRole.Communications } => "Communications input",
            { Kind: AudioDeviceKind.Output } => "Output",
            _ => "Input",
        };
    }

    internal static string GetIconGlyph(AudioDeviceTarget target)
    {
        return target.Kind == AudioDeviceKind.Output ? "\uE767" : "\uE720";
    }

    private void Refresh()
    {
        _onChanged();
    }
}

internal sealed partial class SetDefaultAudioDeviceCommand : InvokableCommand
{
    private readonly AudioDeviceTarget _target;
    private readonly AudioDeviceInfo _device;
    private readonly Action _onChanged;

    public SetDefaultAudioDeviceCommand(AudioDeviceTarget target, AudioDeviceInfo device, Action onChanged)
    {
        _target = target;
        _device = device;
        _onChanged = onChanged;
        Name = device.Name;
        Icon = new IconInfo(AudioDevicesPage.GetIconGlyph(target));
    }

    public override ICommandResult Invoke()
    {
        try
        {
            AudioDeviceService.SetDefaultDevice(_target, _device.Id);

            try
            {
                _onChanged();
            }
            catch
            {
                // The device was changed successfully. A UI refresh failure is recovered by
                // the provider's audio notification or periodic refresh.
            }

            return CommandResult.Hide();
        }
        catch (Exception ex)
        {
            return CommandResult.ShowToast($"Could not set default device: {ex.Message}");
        }
    }
}
