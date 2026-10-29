using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BrowserServerModule;

/// <summary>
/// High performance in-memory JPEG encoder using KSA's bundled libstb native library.
/// Zero disk I/O, writing compressed JPEG streams directly to memory.
/// </summary>
public static unsafe class StbJpegEncoder
{
    private const string LibName = "stb";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void WriteCallback(void* context, void* data, int size);

    [DllImport(LibName, EntryPoint = "stbi_write_jpg_to_func", CallingConvention = CallingConvention.Cdecl)]
    private static unsafe extern int StbiWriteJpgToFunc(
        WriteCallback func,
        void* context,
        int x,
        int y,
        int comp,
        void* data,
        int quality);

    [ThreadStatic]
    private static MemoryStream? _threadStream;

    private static readonly WriteCallback _callback = OnChunk;

    private static unsafe void OnChunk(void* context, void* data, int size)
    {
        if (size <= 0 || _threadStream is null)
        {
            return;
        }

        ReadOnlySpan<byte> span = new(data, size);
        _threadStream.Write(span);
    }

    public static unsafe byte[]? EncodeRgba(void* pixelData, int width, int height, int quality = 80)
    {
        if (pixelData == null || width <= 0 || height <= 0)
        {
            return null;
        }

        _threadStream ??= new MemoryStream(width * height / 4);
        _threadStream.SetLength(0);

        int result = StbiWriteJpgToFunc(_callback, null, width, height, 4, pixelData, quality);
        if (result == 0)
        {
            return null;
        }

        return _threadStream.ToArray();
    }
}
