using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using SharpDX;
using SharpDX.Direct3D;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;
using Windows.Graphics.Imaging;
using WinRT;

namespace EasyGameTranslator;

/// <summary>
/// Captures the selected window's visible rectangle from DXGI Desktop
/// Duplication.  The duplication stream stays alive between scans, so it
/// neither freezes on legacy games nor creates the yellow WGC border.
/// Translation card windows use WDA_EXCLUDEFROMCAPTURE and therefore do not
/// feed their black rectangles back into the next OCR pass.
/// </summary>
public sealed class VisibleWindowCaptureService : IDisposable
{
    private D3D11.Device? _device;
    private D3D11.Texture2D? _staging;
    private DXGI.OutputDuplication? _duplication;
    private Rectangle _desktopBounds;
    private bool _hasFrame;
    private bool _disposed;

    public Task<SoftwareBitmap> CaptureBitmapAsync(Rectangle bounds, CancellationToken token)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds));

        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            return CaptureBitmap(bounds, token);
        }, token);
    }

    public Task CaptureToPngAsync(Rectangle bounds, string filePath, CancellationToken token)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds));

        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            Capture(bounds, filePath, token);
        }, token);
    }

    private void Capture(Rectangle bounds, string filePath, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureDuplication(bounds);

        DXGI.Resource? desktopResource = null;
        var frameAcquired = false;
        try
        {
            var result = _duplication!.TryAcquireNextFrame(250, out _, out desktopResource);
            if (result == DXGI.ResultCode.WaitTimeout)
            {
                if (!_hasFrame)
                {
                    result = _duplication.TryAcquireNextFrame(1000, out _, out desktopResource);
                    result.CheckError();
                    frameAcquired = true;
                }
            }
            else if (result == DXGI.ResultCode.AccessLost)
            {
                ResetDuplication();
                EnsureDuplication(bounds);
                result = _duplication!.TryAcquireNextFrame(500, out _, out desktopResource);
                result.CheckError();
                frameAcquired = true;
            }
            else
            {
                result.CheckError();
                frameAcquired = true;
            }

            token.ThrowIfCancellationRequested();
            if (desktopResource is not null)
            {
                using var desktopTexture = desktopResource.QueryInterface<D3D11.Texture2D>();
                _device!.ImmediateContext.CopyResource(desktopTexture, _staging!);
                _hasFrame = true;
            }

            SaveCrop(bounds, filePath);
        }
        finally
        {
            desktopResource?.Dispose();
            if (frameAcquired)
                _duplication?.ReleaseFrame();
        }
    }

    private SoftwareBitmap CaptureBitmap(Rectangle bounds, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureDuplication(bounds);

        DXGI.Resource? desktopResource = null;
        var frameAcquired = false;
        try
        {
            var result = _duplication!.TryAcquireNextFrame(120, out _, out desktopResource);
            if (result == DXGI.ResultCode.WaitTimeout)
            {
                if (!_hasFrame)
                {
                    result = _duplication.TryAcquireNextFrame(1000, out _, out desktopResource);
                    result.CheckError();
                    frameAcquired = true;
                }
            }
            else if (result == DXGI.ResultCode.AccessLost)
            {
                ResetDuplication();
                EnsureDuplication(bounds);
                result = _duplication!.TryAcquireNextFrame(500, out _, out desktopResource);
                result.CheckError();
                frameAcquired = true;
            }
            else
            {
                result.CheckError();
                frameAcquired = true;
            }

            token.ThrowIfCancellationRequested();
            if (desktopResource is not null)
            {
                using var desktopTexture = desktopResource.QueryInterface<D3D11.Texture2D>();
                _device!.ImmediateContext.CopyResource(desktopTexture, _staging!);
                _hasFrame = true;
            }

            return CopyCropToSoftwareBitmap(bounds);
        }
        finally
        {
            desktopResource?.Dispose();
            if (frameAcquired)
                _duplication?.ReleaseFrame();
        }
    }

    private SoftwareBitmap CopyCropToSoftwareBitmap(Rectangle requestedBounds)
    {
        var localX = requestedBounds.Left - _desktopBounds.Left;
        var localY = requestedBounds.Top - _desktopBounds.Top;
        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            requestedBounds.Width,
            requestedBounds.Height,
            BitmapAlphaMode.Ignore);
        var context = _device!.ImmediateContext;
        var mapped = context.MapSubresource(_staging!, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);
        try
        {
            using var bitmapBuffer = bitmap.LockBuffer(BitmapBufferAccessMode.Write);
            var plane = bitmapBuffer.GetPlaneDescription(0);
            using var reference = bitmapBuffer.CreateReference();
            reference.As<IMemoryBufferByteAccess>().GetBuffer(out var destinationBase, out _);

            var bytesPerRow = requestedBounds.Width * 4;
            for (var row = 0; row < requestedBounds.Height; row++)
            {
                var source = IntPtr.Add(mapped.DataPointer, (localY + row) * mapped.RowPitch + localX * 4);
                var destination = IntPtr.Add(destinationBase, plane.StartIndex + row * plane.Stride);
                Utilities.CopyMemory(destination, source, bytesPerRow);
            }
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
        finally
        {
            context.UnmapSubresource(_staging!, 0);
        }
    }

    private void EnsureDuplication(Rectangle requestedBounds)
    {
        if (_duplication is not null && _desktopBounds.Contains(requestedBounds))
            return;

        ResetDuplication();
        using var factory = new DXGI.Factory1();
        DXGI.Adapter? selectedAdapter = null;
        DXGI.Output? selectedOutput = null;
        var bestArea = 0;

        foreach (var adapter in factory.Adapters1)
        {
            foreach (var output in adapter.Outputs)
            {
                var desktop = ToRectangle(output.Description.DesktopBounds);
                var area = IntersectionArea(desktop, requestedBounds);
                if (area <= bestArea)
                {
                    output.Dispose();
                    continue;
                }

                selectedOutput?.Dispose();
                selectedAdapter?.Dispose();
                selectedOutput = output;
                selectedAdapter = adapter.QueryInterface<DXGI.Adapter>();
                bestArea = area;
            }
            adapter.Dispose();
        }

        if (selectedAdapter is null || selectedOutput is null || bestArea == 0)
            throw new InvalidOperationException("Не найден монитор, на котором находится выбранное окно.");

        try
        {
            _desktopBounds = ToRectangle(selectedOutput.Description.DesktopBounds);
            if (!_desktopBounds.Contains(requestedBounds))
                throw new InvalidOperationException("Окно расположено сразу на нескольких мониторах. Переместите его целиком на один монитор.");

            _device = new D3D11.Device(
                selectedAdapter,
                D3D11.DeviceCreationFlags.BgraSupport,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0);
            using var output1 = selectedOutput.QueryInterface<DXGI.Output1>();
            _duplication = output1.DuplicateOutput(_device);
            _staging = new D3D11.Texture2D(_device, new D3D11.Texture2DDescription
            {
                Width = _desktopBounds.Width,
                Height = _desktopBounds.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new DXGI.SampleDescription(1, 0),
                Usage = D3D11.ResourceUsage.Staging,
                BindFlags = D3D11.BindFlags.None,
                CpuAccessFlags = D3D11.CpuAccessFlags.Read,
                OptionFlags = D3D11.ResourceOptionFlags.None
            });
        }
        finally
        {
            selectedOutput.Dispose();
            selectedAdapter.Dispose();
        }
    }

    private void SaveCrop(Rectangle requestedBounds, string filePath)
    {
        var localX = requestedBounds.Left - _desktopBounds.Left;
        var localY = requestedBounds.Top - _desktopBounds.Top;
        var context = _device!.ImmediateContext;
        var mapped = context.MapSubresource(_staging!, 0, D3D11.MapMode.Read, D3D11.MapFlags.None);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            using var bitmap = new Bitmap(requestedBounds.Width, requestedBounds.Height, PixelFormat.Format32bppArgb);
            var bitmapData = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var bytesPerRow = requestedBounds.Width * 4;
                for (var row = 0; row < requestedBounds.Height; row++)
                {
                    var source = IntPtr.Add(mapped.DataPointer, (localY + row) * mapped.RowPitch + localX * 4);
                    var destination = IntPtr.Add(bitmapData.Scan0, row * bitmapData.Stride);
                    Utilities.CopyMemory(destination, source, bytesPerRow);
                }
            }
            finally
            {
                bitmap.UnlockBits(bitmapData);
            }

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            bitmap.Save(stream, ImageFormat.Png);
        }
        finally
        {
            context.UnmapSubresource(_staging!, 0);
        }
    }

    private void ResetDuplication()
    {
        _duplication?.Dispose();
        _staging?.Dispose();
        _device?.Dispose();
        _duplication = null;
        _staging = null;
        _device = null;
        _desktopBounds = Rectangle.Empty;
        _hasFrame = false;
    }

    private static Rectangle ToRectangle(SharpDX.Mathematics.Interop.RawRectangle rectangle)
        => Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);

    private static int IntersectionArea(Rectangle first, Rectangle second)
    {
        var intersection = Rectangle.Intersect(first, second);
        return intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetDuplication();
    }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMemoryBufferByteAccess
    {
        void GetBuffer(out IntPtr buffer, out uint capacity);
    }
}
