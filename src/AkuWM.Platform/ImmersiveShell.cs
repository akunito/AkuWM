using System.Runtime.InteropServices;
using AkuWM.Core.Logging;
using AkuWM.Core.Model;

namespace AkuWM.Platform;

/// <summary>
/// The shell's own view of a window, and the one call AkuWM needs from it:
/// cloak, and uncloak.
/// </summary>
/// <remarks>
/// <para>
/// Hiding a workspace means making its windows invisible without minimising
/// them -- a minimised game stops rendering, a cloaked one does not, which is
/// the whole reason this desk hides by cloaking. The documented
/// <c>DWMWA_CLOAK</c> attribute only works on a process's own windows, so the
/// cloak of somebody else's window goes through the shell:
/// <c>IApplicationView::SetCloak</c>, reached from
/// <c>CLSID_ImmersiveShell</c>'s service provider.
/// </para>
/// <para>
/// These interfaces are not in Microsoft's metadata and are not documented.
/// The declarations below are written from the MIT-licensed
/// <c>Ciantic/VirtualDesktopAccessor</c> and <c>MScholtes/VirtualDesktop</c>,
/// which are the references LICENSING.md allows for exactly this. Only what
/// AkuWM calls is declared; the rest of each vtable is reserved with the right
/// number of slots, because a COM interface is its layout.
/// </para>
/// <para>
/// It works on elevated windows -- a normal process may cloak a game it can
/// neither move nor resize -- which is the asymmetry the whole design rests
/// on, and what spike S3 is for.
/// </para>
/// </remarks>
public sealed class ImmersiveShell : IDisposable
{
    private static readonly Guid ImmersiveShellClsid = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid ApplicationViewCollectionId = new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");

    private IServiceProvider10? _services;
    private IApplicationViewCollection? _views;

    public bool Available => _views is not null;

    public string? Unavailable { get; private set; }

    public ImmersiveShell()
    {
        try
        {
            Type? type = Type.GetTypeFromCLSID(ImmersiveShellClsid);
            if (type is null)
            {
                Unavailable = "CLSID_ImmersiveShell is not registered";
                return;
            }

            _services = (IServiceProvider10)Activator.CreateInstance(type)!;
            object views = _services.QueryService(ApplicationViewCollectionId, ApplicationViewCollectionId);
            _views = (IApplicationViewCollection)views;
        }
        catch (Exception ex)
        {
            Unavailable = $"{ex.GetType().Name}: {ex.Message}";
            _views = null;
        }
    }

    /// <summary>Cloaks or uncloaks somebody else's window.</summary>
    /// <returns>Null on success, or why it failed.</returns>
    public string? SetCloak(WindowHandle window, bool cloaked)
    {
        if (_views is null)
        {
            return Unavailable ?? "the shell's view collection is not available";
        }

        try
        {
            _views.GetViewForHwnd((IntPtr)window.Value, out IApplicationView? view);
            if (view is null)
            {
                return "the shell has no view for that window";
            }

            try
            {
                // The first argument says *which* cloak, the second whether it
                // is on. Clearing it with type None is refused with E_INVALIDARG
                // (measured): the type has to stay Shell and the flag go to 0.
                view.SetCloak(ApplicationViewCloakType.Shell, cloaked ? 1 : 0);
                return null;
            }
            finally
            {
                Marshal.ReleaseComObject(view);
            }
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: 0x{ex.HResult:x8} {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_views is not null)
        {
            Marshal.ReleaseComObject(_views);
            _views = null;
        }

        if (_services is not null)
        {
            Marshal.ReleaseComObject(_services);
            _services = null;
        }

        Log.Debug("immersive shell released");
    }

    private enum ApplicationViewCloakType
    {
        None = 0,
        Default = 1,
        Shell = 2,
        Inherited = 3,
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider10
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(in Guid service, in Guid riid);
    }

    [ComImport]
    [Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollection
    {
        int GetViews(out IntPtr views);

        int GetViewsByZOrder(out IntPtr views);

        int GetViewsByAppUserModelId(string id, out IntPtr views);

        void GetViewForHwnd(IntPtr window, out IApplicationView? view);

        // The rest of the vtable is not called and not declared: nothing below
        // this point is ever reached through this interface.
    }

    [ComImport]
    [Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationView
    {
        // IInspectable's three slots come first: an application view is a
        // WinRT object, so the vtable starts with GetIids, GetRuntimeClassName
        // and GetTrustLevel after IUnknown.
        int GetIids(out ulong count, out IntPtr iids);

        int GetRuntimeClassName(out IntPtr name);

        int GetTrustLevel(out IntPtr trust);

        int SetFocus();

        int SwitchTo();

        int TryInvokeBack(IntPtr callback);

        int GetThumbnailWindow(out IntPtr window);

        int GetMonitor(out IntPtr monitor);

        int GetVisibility(out int visibility);

        int SetCloak(ApplicationViewCloakType type, int flags);

        // Everything after SetCloak is left out for the same reason as above.
    }
}
