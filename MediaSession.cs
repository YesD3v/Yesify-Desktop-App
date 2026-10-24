using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Yesify.Models;
using Yesify.ViewModels;

namespace Yesify;

/// <summary>
/// Publishes what's playing as a Windows media session (the volume/media flyout, lock screen, and any
/// app that reads media sessions): title, artist, album, artwork, play/pause state and the timeline. It
/// also takes play/pause, next/previous and seek requests from them. It talks to WinRT's
/// SystemMediaTransportControls through plain COM calls (vtable slots from windows.media.h), which keeps
/// the Windows SDK projection, and the memory it costs, out of the app.
/// </summary>
public sealed unsafe class MediaSession : IDisposable
{
    // MediaPlaybackStatus / MediaPlaybackType / SystemMediaTransportControlsButton values
    private const int StatusChanging = 1, StatusStopped = 2, StatusPlaying = 3, StatusPaused = 4;
    private const int TypeMusic = 1;
    private const int ButtonPlay = 0, ButtonPause = 1, ButtonStop = 2, ButtonNext = 6, ButtonPrevious = 7;

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    private static readonly Guid IID_IMarshal = new("00000003-0000-0000-C000-000000000046");
    private static readonly Guid IID_Interop = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");          // ISystemMediaTransportControlsInterop
    private static readonly Guid IID_Controls = new("99fa3ff4-1742-42a6-902e-087d41f965ec");        // ISystemMediaTransportControls
    private static readonly Guid IID_Controls2 = new("ea98d2f6-7f3c-4af2-a586-72889808efb1");       // ISystemMediaTransportControls2
    private static readonly Guid IID_Music2 = new("00368462-97d3-44b9-b00f-008afcefaf18");          // IMusicDisplayProperties2
    private static readonly Guid IID_Timeline = new("5125316a-c3a2-475b-8507-93534dc88f15");        // ...TimelineProperties
    private static readonly Guid IID_UriFactory = new("44a9796f-723e-4fdf-a218-033e75b0c084");      // IUriRuntimeClassFactory
    private static readonly Guid IID_StreamRefStatics = new("857309dc-3fbf-4e7d-986f-ef3b1a07a964"); // IRandomAccessStreamReferenceStatics
    private static readonly Guid IID_RandomAccessStream = new("905a0fe1-bc53-11df-8c49-001e4fc686da");
    // TypedEventHandler<SystemMediaTransportControls, ButtonPressedEventArgs / PlaybackPositionChangeRequestedEventArgs>
    private static readonly Guid IID_ButtonHandler = new("0557e996-7b23-5bae-aa81-ea0d671143a4");
    private static readonly Guid IID_SeekHandler = new("44e34f15-bdc0-50a7-ace4-39e91fb753f1");

    // Windows calls the handlers on its own threads; they hand the request to the UI thread.
    private static MediaSession? _instance;

    private readonly MainViewModel _vm;
    private readonly Dispatcher _dispatcher;
    private readonly IntPtr _controls, _controls2, _updater, _music, _music2, _timeline, _uriFactory, _streamRefs;
    private readonly DispatcherTimer _resync;
    private Track? _track;
    private bool _disposed;

    /// <summary>Starts the session for a window, or returns null if Windows can't provide one.</summary>
    public static MediaSession? TryCreate(IntPtr hwnd, MainViewModel vm)
    {
        try { return _instance = new MediaSession(hwnd, vm); }
        catch { return null; } // no media session is better than no app
    }

    private MediaSession(IntPtr hwnd, MainViewModel vm)
    {
        _vm = vm;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var interop = Factory("Windows.Media.SystemMediaTransportControls", IID_Interop);
        try
        {
            var iid = IID_Controls;
            IntPtr controls;
            Check(((delegate* unmanaged<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(interop, 6))(interop, hwnd, &iid, &controls)); // GetForWindow
            _controls = controls;
        }
        finally { Marshal.Release(interop); }

        _controls2 = QueryInterface(_controls, IID_Controls2);
        _updater = GetPtr(_controls, 8);              // get_DisplayUpdater
        PutInt(_updater, 7, TypeMusic);               // put_Type
        _music = GetPtr(_updater, 12);                // get_MusicProperties
        _music2 = QueryInterface(_music, IID_Music2);
        _timeline = Activate("Windows.Media.SystemMediaTransportControlsTimelineProperties", IID_Timeline);
        _uriFactory = Factory("Windows.Foundation.Uri", IID_UriFactory);
        _streamRefs = Factory("Windows.Storage.Streams.RandomAccessStreamReference", IID_StreamRefStatics);

        PutBool(_controls, 13, true);                 // put_IsPlayEnabled
        PutBool(_controls, 17, true);                 // put_IsPauseEnabled
        PutBool(_controls, 25, true);                 // put_IsPreviousEnabled
        PutBool(_controls, 27, true);                 // put_IsNextEnabled
        AddHandler(_controls, 32, Handler.Create(IID_ButtonHandler, &OnButtonPressed));             // add_ButtonPressed
        AddHandler(_controls2, 13, Handler.Create(IID_SeekHandler, &OnPositionChangeRequested));    // add_PlaybackPositionChangeRequested

        // Windows advances the position by itself while the status is Playing; this corrects any drift.
        _resync = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => UpdateTimeline(), _dispatcher);

        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.Seeked += UpdateTimeline;
        WatchTrack(_vm.CurrentTrack);
        UpdateStatus();
    }

    // ---------------------------------------------------------------- following the player

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentTrack):
                WatchTrack(_vm.CurrentTrack);
                break;
            case nameof(MainViewModel.IsPlaying):
            case nameof(MainViewModel.IsResolving):
                UpdateStatus();
                break;
            case nameof(MainViewModel.DurationSeconds):
                UpdateTimeline();
                break;
        }
    }

    // Covers can arrive after the song starts (playlist covers load lazily), so follow ImageUrl too.
    private void WatchTrack(Track? track)
    {
        if (_track != null) _track.PropertyChanged -= OnTrackPropertyChanged;
        _track = track;
        if (track != null) track.PropertyChanged += OnTrackPropertyChanged;
        UpdateDisplay();
        UpdateTimeline();
    }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Track.ImageUrl)) UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (_disposed) return;
        try
        {
            var t = _track;
            PutBool(_controls, 11, t != null);        // put_IsEnabled: no song, no session
            PutString(_music, 7, t?.Title ?? "");     // put_Title
            PutString(_music, 11, t?.Artist ?? "");   // put_Artist
            PutString(_music2, 7, t?.Album ?? "");    // put_AlbumTitle
            var thumbnail = Thumbnail(t?.ImageUrl);
            try { PutPtr(_updater, 11, thumbnail); }  // put_Thumbnail
            finally { if (thumbnail != IntPtr.Zero) Marshal.Release(thumbnail); }
            Check(((delegate* unmanaged<IntPtr, int>)Slot(_updater, 17))(_updater)); // Update
        }
        catch { /* the session is a nicety */ }
    }

    private void UpdateStatus()
    {
        if (_disposed) return;
        var status = _track == null ? StatusStopped
                   : _vm.IsPlaying ? StatusPlaying
                   : _vm.IsResolving ? StatusChanging
                   : StatusPaused;
        try { PutInt(_controls, 7, status); }         // put_PlaybackStatus
        catch { }
        if (status == StatusPlaying) _resync.Start(); else _resync.Stop();
        UpdateTimeline();
    }

    private void UpdateTimeline()
    {
        if (_disposed) return;
        try
        {
            var duration = _track == null ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Math.Max(_vm.DurationSeconds > 1 ? _vm.DurationSeconds : 0, _track.Duration.TotalSeconds));
            var position = _track == null ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Clamp(_vm.PlaybackPosition.Ticks, 0, duration.Ticks));
            PutLong(_timeline, 7, 0);                 // put_StartTime
            PutLong(_timeline, 9, duration.Ticks);    // put_EndTime
            PutLong(_timeline, 11, 0);                // put_MinSeekTime
            PutLong(_timeline, 13, duration.Ticks);   // put_MaxSeekTime
            PutLong(_timeline, 15, position.Ticks);   // put_Position
            PutPtr(_controls2, 12, _timeline);        // UpdateTimelineProperties
        }
        catch { }
    }

    /// <summary>A stream reference for the artwork: a web address or a picture on disk.</summary>
    private IntPtr Thumbnail(string? image)
    {
        if (string.IsNullOrEmpty(image)) return IntPtr.Zero;
        try
        {
            IntPtr reference;
            if (image.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var text = HString(image);
                IntPtr uri;
                try { Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Slot(_uriFactory, 6))(_uriFactory, text, &uri)); } // CreateUri
                finally { WindowsDeleteString(text); }
                try { Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Slot(_streamRefs, 7))(_streamRefs, uri, &reference)); } // CreateFromUri
                finally { Marshal.Release(uri); }
                return reference;
            }
            if (!File.Exists(image)) return IntPtr.Zero;
            var iid = IID_RandomAccessStream;
            Check(CreateRandomAccessStreamOnFile(image, 0 /* read */, ref iid, out var stream));
            try { Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Slot(_streamRefs, 8))(_streamRefs, stream, &reference)); } // CreateFromStream
            finally { Marshal.Release(stream); }
            return reference;
        }
        catch { return IntPtr.Zero; }
    }

    // ---------------------------------------------------------------- requests from Windows

    [UnmanagedCallersOnly]
    private static int OnButtonPressed(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            int value;
            if (((delegate* unmanaged<IntPtr, int*, int>)Slot(args, 6))(args, &value) < 0) return 0; // get_Button
            var button = value;
            _instance?._dispatcher.BeginInvoke(() => _instance?.OnButton(button));
        }
        catch { }
        return 0;
    }

    private void OnButton(int button)
    {
        switch (button)
        {
            case ButtonPlay when !_vm.IsAudioPlaying:
            case ButtonPause or ButtonStop when _vm.IsAudioPlaying:
                _vm.TogglePlayCommand.Execute(null);
                break;
            case ButtonNext:
                _vm.NextCommand.Execute(null);
                break;
            case ButtonPrevious:
                _vm.PreviousCommand.Execute(null);
                break;
        }
    }

    [UnmanagedCallersOnly]
    private static int OnPositionChangeRequested(IntPtr self, IntPtr sender, IntPtr args)
    {
        try
        {
            long value;
            if (((delegate* unmanaged<IntPtr, long*, int>)Slot(args, 6))(args, &value) < 0) return 0; // get_RequestedPlaybackPosition
            var ticks = value;
            _instance?._dispatcher.BeginInvoke(() =>
            {
                if (_instance?._track == null) return;
                _instance._vm.SeekTo(Math.Max(0, TimeSpan.FromTicks(ticks).TotalSeconds));
            });
        }
        catch { }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _resync.Stop();
        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.Seeked -= UpdateTimeline;
        if (_track != null) _track.PropertyChanged -= OnTrackPropertyChanged;
        try
        {
            PutInt(_controls, 7, 0);                  // Closed
            PutBool(_controls, 11, false);
        }
        catch { }
        _disposed = true;
        // The COM objects live until the process ends, a moment from now.
    }

    // ---------------------------------------------------------------- event handler objects

    /// <summary>
    /// A WinRT TypedEventHandler implemented by hand: IUnknown plus Invoke. It is agile (with the
    /// free-threaded marshaler) so Windows may call it from any thread, and lives as long as the app.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Handler
    {
        public void** Vtable;
        public Guid Iid;
        public IntPtr FreeThreadedMarshaler;

        public static IntPtr Create(Guid iid, delegate* unmanaged<IntPtr, IntPtr, IntPtr, int> invoke)
        {
            var vtable = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*)));
            vtable[0] = (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
            vtable[1] = (delegate* unmanaged<IntPtr, uint>)&AddRef;
            vtable[2] = (delegate* unmanaged<IntPtr, uint>)&Release;
            vtable[3] = invoke;

            var self = (Handler*)NativeMemory.AllocZeroed((nuint)sizeof(Handler));
            self->Vtable = vtable;
            self->Iid = iid;
            if (CoCreateFreeThreadedMarshaler((IntPtr)self, out var ftm) >= 0) self->FreeThreadedMarshaler = ftm;
            return (IntPtr)self;
        }

        [UnmanagedCallersOnly]
        private static int QueryInterface(IntPtr self, Guid* riid, IntPtr* result)
        {
            var handler = (Handler*)self;
            if (*riid == handler->Iid || *riid == IID_IUnknown || *riid == IID_IAgileObject)
            {
                *result = self;
                return 0;
            }
            if (*riid == IID_IMarshal && handler->FreeThreadedMarshaler != IntPtr.Zero)
                return Marshal.QueryInterface(handler->FreeThreadedMarshaler, in *riid, out *result);
            *result = IntPtr.Zero;
            return unchecked((int)0x80004002); // E_NOINTERFACE
        }

        // Never freed, so there's nothing to count.
        [UnmanagedCallersOnly] private static uint AddRef(IntPtr self) => 1;
        [UnmanagedCallersOnly] private static uint Release(IntPtr self) => 1;
    }

    // ---------------------------------------------------------------- COM plumbing

    private static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static void PutBool(IntPtr obj, int slot, bool value) =>
        Check(((delegate* unmanaged<IntPtr, byte, int>)Slot(obj, slot))(obj, value ? (byte)1 : (byte)0));

    private static void PutInt(IntPtr obj, int slot, int value) =>
        Check(((delegate* unmanaged<IntPtr, int, int>)Slot(obj, slot))(obj, value));

    private static void PutLong(IntPtr obj, int slot, long value) =>
        Check(((delegate* unmanaged<IntPtr, long, int>)Slot(obj, slot))(obj, value));

    private static void PutPtr(IntPtr obj, int slot, IntPtr value) =>
        Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Slot(obj, slot))(obj, value));

    private static void PutString(IntPtr obj, int slot, string value)
    {
        var text = HString(value);
        try { PutPtr(obj, slot, text); }
        finally { WindowsDeleteString(text); }
    }

    private static IntPtr GetPtr(IntPtr obj, int slot)
    {
        IntPtr result;
        Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(obj, slot))(obj, &result));
        return result;
    }

    private static void AddHandler(IntPtr obj, int slot, IntPtr handler)
    {
        long token;
        Check(((delegate* unmanaged<IntPtr, IntPtr, long*, int>)Slot(obj, slot))(obj, handler, &token));
    }

    private static IntPtr QueryInterface(IntPtr obj, Guid iid)
    {
        Check(Marshal.QueryInterface(obj, in iid, out var result));
        return result;
    }

    private static IntPtr HString(string value)
    {
        Check(WindowsCreateString(value, (uint)value.Length, out var text));
        return text;
    }

    private static IntPtr Factory(string className, Guid iid)
    {
        var name = HString(className);
        try
        {
            Check(RoGetActivationFactory(name, ref iid, out var factory));
            return factory;
        }
        finally { WindowsDeleteString(name); }
    }

    private static IntPtr Activate(string className, Guid iid)
    {
        var name = HString(className);
        try
        {
            Check(RoActivateInstance(name, out var instance));
            try { return QueryInterface(instance, iid); }
            finally { Marshal.Release(instance); }
        }
        finally { WindowsDeleteString(name); }
    }

    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("combase.dll")] private static extern int RoActivateInstance(IntPtr classId, out IntPtr instance);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, uint length, out IntPtr text);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr text);
    [DllImport("ole32.dll")] private static extern int CoCreateFreeThreadedMarshaler(IntPtr outer, out IntPtr marshaler);
    [DllImport("shcore.dll", CharSet = CharSet.Unicode)] private static extern int CreateRandomAccessStreamOnFile(string path, uint mode, ref Guid iid, out IntPtr stream);
}
