using AkuWM.Core.Logging;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;

namespace AkuWM.Platform;

/// <summary>
/// The default microphone's mute switch, through Core Audio. The whole of what
/// the <c>toggle-mic</c> command needs from Windows.
/// </summary>
/// <remarks>
/// Both default capture roles are switched together: Windows keeps a "default
/// device" (eConsole) and a "default communication device" (eCommunications),
/// usually the same endpoint, and a person who mutes "the microphone" means
/// both. The new state is read back from the console device after the write,
/// so the reply says what the mixer shows, not what was asked for.
/// </remarks>
public static class Win32Audio
{

    /// <summary>Mutes a live microphone, unmutes a muted one. Null with the reason when Core Audio refuses.</summary>
    public static unsafe bool? ToggleMicrophone(out string? error)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out IMMDevice console);
            IAudioEndpointVolume consoleVolume = VolumeOf(console);
            BOOL wasMuted;
            consoleVolume.GetMute(&wasMuted);
            BOOL target = !wasMuted;
            consoleVolume.SetMute(target, null);

            // The communications role, when it is another device.
            try
            {
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out IMMDevice comms);
                PWSTR consoleId, commsId;
                console.GetId(&consoleId);
                comms.GetId(&commsId);
                if (consoleId.ToString() != commsId.ToString())
                {
                    VolumeOf(comms).SetMute(target, null);
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"toggle-mic: the communications device was not switched: {ex.Message}");
            }

            BOOL isMuted;
            consoleVolume.GetMute(&isMuted);
            error = null;
            return isMuted;
        }
        catch (Exception ex)
        {
            // E_NOTFOUND (0x80070490) is the honest "there is no microphone".
            error = (uint)ex.HResult == 0x80070490
                ? "there is no capture device"
                : $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static unsafe IAudioEndpointVolume VolumeOf(IMMDevice device)
    {
        Guid iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(&iid, CLSCTX.CLSCTX_ALL, null, out object volume);
        return (IAudioEndpointVolume)volume;
    }
}
