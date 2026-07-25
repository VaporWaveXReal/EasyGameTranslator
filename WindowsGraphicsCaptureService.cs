using System.Runtime.InteropServices;
using Windows.AI.MachineLearning;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace EasyGameTranslator;

/// <summary>
/// Captures only the selected window surface, so top-level translation cards
/// never feed back into OCR. A permanent anchor session keeps Windows' capture
/// indicator stable; a short-lived session provides a genuinely current frame
/// on Windows 10, where a long-lived session can freeze after its first frame
/// for legacy DirectDraw/DirectX windows.
/// </summary>
public sealed class WindowsGraphicsCaptureService : IDisposable
{
    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _anchorPool;
    private readonly GraphicsCaptureSession _anchorSession;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private WindowsGraphicsCaptureService(GraphicsCaptureItem item)
    {
        _item = item;
        _device = new LearningModelDevice(LearningModelDeviceKind.DirectX).Direct3D11Device;

        // This session remains alive for the lifetime of translation. Creating
        // the per-scan sessions below therefore does not toggle the yellow
        // capture indicator on and off.
        _anchorPool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            item.Size);
        _anchorSession = _anchorPool.CreateCaptureSession(item);
        _anchorSession.IsCursorCaptureEnabled = false;
        _anchorSession.StartCapture();
    }

    public static WindowsGraphicsCaptureService CreatePrimaryMonitor()
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new PlatformNotSupportedException("Windows Graphics Capture не поддерживается этой системой.");
        return new WindowsGraphicsCaptureService(CreateItemForPrimaryMonitor());
    }

    public static WindowsGraphicsCaptureService CreateForWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            throw new ArgumentException("Не выбрано окно для захвата.", nameof(handle));
        if (!GraphicsCaptureSession.IsSupported())
            throw new PlatformNotSupportedException("Windows Graphics Capture не поддерживается этой системой.");
        return new WindowsGraphicsCaptureService(CreateItemForWindow(handle));
    }

    public static Task<WindowsGraphicsCaptureService> CreateForWindowAsync(IntPtr handle)
        => Task.FromResult(CreateForWindow(handle));

    public async Task<SoftwareBitmap> CaptureFrameAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await CaptureCurrentFrameAsync(token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SoftwareBitmap> CaptureCurrentFrameAsync(CancellationToken token)
    {
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            1,
            _item.Size);
        using var session = pool.CreateCaptureSession(_item);
        session.IsCursorCaptureEnabled = false;
        var result = new TaskCompletionSource<SoftwareBitmap>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var copying = 0;

        async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (Interlocked.Exchange(ref copying, 1) != 0)
                return;
            try
            {
                using var frame = sender.TryGetNextFrame();
                using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                result.TrySetResult(SoftwareBitmap.Copy(bitmap));
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        }

        pool.FrameArrived += OnFrameArrived;
        try
        {
            session.StartCapture();
            return await result.Task.WaitAsync(TimeSpan.FromMilliseconds(900), token);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                "Окно не передало актуальный кадр. Откройте его в обычном или безрамочном режиме.");
        }
        finally
        {
            pool.FrameArrived -= OnFrameArrived;
        }
    }

    private static GraphicsCaptureItem CreateItemForPrimaryMonitor()
    {
        var monitor = MonitorFromPoint(new PointInt32 { X = 0, Y = 0 }, MonitorDefaultToPrimary);
        var factory = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var pointer = factory.CreateForMonitor(monitor, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(IntPtr handle)
    {
        var factory = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var pointer = factory.CreateForWindow(handle, ref iid);
        try
        {
            return GraphicsCaptureItem.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _anchorSession.Dispose();
        _anchorPool.Dispose();
        _gate.Dispose();
    }

    private const uint MonitorDefaultToPrimary = 1;
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(PointInt32 point, uint flags);
}
