using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using AlphaMode = Vortice.DCommon.AlphaMode;
using Size = System.Drawing.Size;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using Color = System.Drawing.Color;
using PixelFormat = Vortice.DCommon.PixelFormat;

namespace VirtualScreenViewer;

/// <summary>
/// A panel that shows one monitor live, captured with Windows Graphics Capture (the API OBS and
/// the Snipping Tool use) and drawn with Direct2D, so frames never leave the graphics card. The
/// cursor comes with the capture. Frames arrive on a capture thread and are drawn there; resizing
/// and stopping happen on the UI thread, so both take the same lock.
/// </summary>
internal sealed class GpuView : Control
{
    private readonly object _lock = new();
    private ID3D11Device? _device;
    private IDXGIDevice? _dxgiDevice;
    private IDirect3DDevice? _winrtDevice;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1DeviceContext? _ctx;
    private IDXGISwapChain1? _swapChain;
    private ID2D1Bitmap1? _target;

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private Windows.Graphics.SizeInt32 _poolSize;

    /// <summary>The captured monitor's size in pixels.</summary>
    public Size SourceSize { get; private set; }
    public bool IsCapturing => _session is not null;
    /// <summary>Raised (on the capture thread) when capture stops by itself, e.g. the monitor went away.</summary>
    public event Action? CaptureLost;

    public GpuView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        BackColor = Color.Black;
    }

    public static bool Supported => GraphicsCaptureSession.IsSupported();

    // ---------- Device and swap chain ----------

    private void EnsureDevice()
    {
        if (_device is not null) return;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 }, out _device).CheckError();
        _dxgiDevice = _device!.QueryInterface<IDXGIDevice>();

        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(_dxgiDevice.NativePointer, out IntPtr inspectable));
        try { _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
        using var d2dDevice = _d2dFactory.CreateDevice(_dxgiDevice);
        _ctx = d2dDevice.CreateDeviceContext(DeviceContextOptions.None);

        using var adapter = _dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var desc = new SwapChainDescription1
        {
            Width = (uint)Math.Max(1, ClientSize.Width),
            Height = (uint)Math.Max(1, ClientSize.Height),
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            SwapEffect = SwapEffect.FlipSequential,
            Scaling = Scaling.Stretch,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
        };
        _swapChain = factory.CreateSwapChainForHwnd(_device, Handle, desc);
        factory.MakeWindowAssociation(Handle, WindowAssociationFlags.IgnoreAltEnter);
        CreateTarget();
    }

    private void CreateTarget()
    {
        using var surface = _swapChain!.GetBuffer<IDXGISurface>(0);
        var props = new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Ignore), 96, 96,
            BitmapOptions.Target | BitmapOptions.CannotDraw);
        _target = _ctx!.CreateBitmapFromDxgiSurface(surface, props);
        _ctx.Target = _target;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        lock (_lock)
        {
            if (_swapChain is null || ClientSize.Width < 1 || ClientSize.Height < 1) return;
            _ctx!.Target = null;
            _target?.Dispose();
            _swapChain.ResizeBuffers(2, (uint)ClientSize.Width, (uint)ClientSize.Height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
            CreateTarget();
        }
    }

    // ---------- Capture ----------

    /// <summary>Starts showing the monitor <paramref name="hmonitor"/>.</summary>
    public void Start(IntPtr hmonitor)
    {
        Stop();
        lock (_lock)
        {
            EnsureDevice();
            _item = CaptureItemForMonitor(hmonitor);
            _item.Closed += (_, _) => { Stop(); CaptureLost?.Invoke(); };
            _poolSize = _item.Size;
            SourceSize = new Size(_poolSize.Width, _poolSize.Height);
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            _pool.FrameArrived += OnFrameArrived;
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = true;
            // No yellow "being captured" border on the virtual display (Windows 11; it would show up
            // in OBS too). Windows 10 doesn't have the option and keeps the border.
            try { _session.IsBorderRequired = false; } catch { }
            _session.StartCapture();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _session?.Dispose(); _session = null;
            if (_pool is not null) { _pool.FrameArrived -= OnFrameArrived; _pool.Dispose(); _pool = null; }
            _item = null;
        }
        Invalidate();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_lock)
        {
            if (_pool is null || _ctx is null || _swapChain is null) return;
            using var frame = sender.TryGetNextFrame();
            if (frame is null) return;

            // The monitor's resolution changed: recreate the pool at the new size (next frame uses it).
            if (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height)
            {
                _poolSize = frame.ContentSize;
                SourceSize = new Size(_poolSize.Width, _poolSize.Height);
                _pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
                return;
            }
            if (!Visible || ClientSize.Width < 1 || ClientSize.Height < 1) return;

            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            var iid = typeof(IDXGISurface).GUID;
            using var surface = new IDXGISurface(access.GetInterface(ref iid));
            using var bitmap = _ctx.CreateBitmapFromDxgiSurface(surface,
                new BitmapProperties1(new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied)));

            var rect = Fit(SourceSize, ClientSize);
            _ctx.BeginDraw();
            _ctx.Clear(new Color4(0, 0, 0, 1));
            _ctx.DrawBitmap(bitmap, new Vortice.RawRectF(rect.Left, rect.Top, rect.Right, rect.Bottom), 1f,
                Vortice.Direct2D1.InterpolationMode.Linear, null, null);
            _ctx.EndDraw();
            _swapChain.Present(1, PresentFlags.None);
        }
    }

    public static Rectangle Fit(Size src, Size box)
    {
        if (src.Width <= 0 || src.Height <= 0) return Rectangle.Empty;
        double scale = Math.Min((double)box.Width / src.Width, (double)box.Height / src.Height);
        int w = (int)(src.Width * scale), h = (int)(src.Height * scale);
        return new Rectangle((box.Width - w) / 2, (box.Height - h) / 2, w, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // Frames are drawn as they arrive; with nothing capturing, just clear the panel.
        if (_session is null) e.Graphics.Clear(Color.Black);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
            lock (_lock)
            {
                _target?.Dispose(); _ctx?.Dispose(); _swapChain?.Dispose(); _d2dFactory?.Dispose();
                (_winrtDevice as IDisposable)?.Dispose(); _dxgiDevice?.Dispose(); _device?.Dispose();
                _target = null; _ctx = null; _swapChain = null; _device = null;
            }
        }
        base.Dispose(disposing);
    }

    // ---------- WinRT / COM interop ----------

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    private static readonly Guid IGraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private static GraphicsCaptureItem CaptureItemForMonitor(IntPtr hmonitor)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();
        var iid = IGraphicsCaptureItemIid;
        IntPtr ptr = interop.CreateForMonitor(hmonitor, ref iid);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }
}
