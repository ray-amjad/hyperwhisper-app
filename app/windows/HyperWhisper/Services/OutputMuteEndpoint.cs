// OUTPUT MUTE ENDPOINT
// The one thing AudioEnvironmentService needs from the default render device: its id, its name and
// its mute flag. Behind this seam the smoke suite drives the real mute/restore rules with a fake
// device, because a CI runner may have no audio endpoint at all (issue #2243).

using NAudio.CoreAudioApi;

namespace HyperWhisper.Services;

internal interface IOutputMuteEndpoint : IDisposable
{
    string Id { get; }
    string Name { get; }
    bool Mute { get; set; }
}

internal interface IOutputMuteEndpoints
{
    /// <summary>The default multimedia render device.</summary>
    IOutputMuteEndpoint GetDefault();

    /// <summary>The device with <paramref name="deviceId"/>, or the default one when it is gone.</summary>
    IOutputMuteEndpoint GetOrDefault(string deviceId);
}

internal sealed class NAudioOutputMuteEndpoints : IOutputMuteEndpoints
{
    public IOutputMuteEndpoint GetDefault()
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            return new Endpoint(enumerator, enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia));
        }
        catch
        {
            enumerator.Dispose();
            throw;
        }
    }

    public IOutputMuteEndpoint GetOrDefault(string deviceId)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            MMDevice device;
            try
            {
                device = enumerator.GetDevice(deviceId);
            }
            catch
            {
                device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }

            return new Endpoint(enumerator, device);
        }
        catch
        {
            enumerator.Dispose();
            throw;
        }
    }

    private sealed class Endpoint(MMDeviceEnumerator enumerator, MMDevice device) : IOutputMuteEndpoint
    {
        public string Id => device.ID;
        public string Name => device.FriendlyName;

        public bool Mute
        {
            get => device.AudioEndpointVolume.Mute;
            set => device.AudioEndpointVolume.Mute = value;
        }

        // Reverse order of creation, as the old `using var` pair did.
        public void Dispose()
        {
            device.Dispose();
            enumerator.Dispose();
        }
    }
}
