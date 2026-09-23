using System.Buffers.Binary;

namespace FootballManagerRegenNotifier.Capture.Preprocess;

/// <summary>
/// Encodes a <see cref="CapturedFrame"/> as an uncompressed 24-bit BMP.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of a concrete packaging fact: the <c>Tesseract</c> 5.2.0
/// NuGet package ships <c>PixConverter</c> / <c>BitmapToPixConverter</c> only in
/// its <c>net48</c> asset, and a <c>net10.0-windows</c> project resolves the
/// <c>netstandard2.0</c> asset, where those types do not exist. The usual
/// <c>Bitmap -&gt; Pix</c> bridge is therefore unavailable, and the supported
/// route into the engine is <c>Pix.LoadFromMemory(byte[])</c> with encoded image
/// bytes.
/// </para>
/// <para>
/// Writing the container by hand is a few dozen lines and keeps the capture
/// project free of any imaging dependency, which is what lets it avoid
/// <c>System.Drawing</c> and unmanaged handles entirely.
/// </para>
/// </remarks>
public static class BmpEncoder
{
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;

    public static byte[] Encode24Bpp(CapturedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        // BMP rows are padded to a four-byte boundary.
        int rowSize = (frame.Width * 3 + 3) & ~3;
        int pixelDataSize = rowSize * frame.Height;
        int fileSize = FileHeaderSize + InfoHeaderSize + pixelDataSize;

        var buffer = new byte[fileSize];
        var span = buffer.AsSpan();

        // BITMAPFILEHEADER
        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], fileSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], FileHeaderSize + InfoHeaderSize);

        // BITMAPINFOHEADER
        var info = span[FileHeaderSize..];
        BinaryPrimitives.WriteInt32LittleEndian(info, InfoHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(info[4..], frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(info[8..], frame.Height);
        BinaryPrimitives.WriteInt16LittleEndian(info[12..], 1);   // planes
        BinaryPrimitives.WriteInt16LittleEndian(info[14..], 24);  // bits per pixel
        BinaryPrimitives.WriteInt32LittleEndian(info[20..], pixelDataSize);
        // 96 DPI in pixels per metre. Tesseract warns when an image declares no
        // resolution, and the warning is noisy enough to bury real diagnostics.
        BinaryPrimitives.WriteInt32LittleEndian(info[24..], 3780);
        BinaryPrimitives.WriteInt32LittleEndian(info[28..], 3780);

        int dataStart = FileHeaderSize + InfoHeaderSize;
        for (int y = 0; y < frame.Height; y++)
        {
            // BMP pixel rows run bottom-up.
            int srcRow = (frame.Height - 1 - y) * frame.Stride;
            int dstRow = dataStart + y * rowSize;

            for (int x = 0; x < frame.Width; x++)
            {
                int s = srcRow + x * 4;
                int d = dstRow + x * 3;
                buffer[d] = frame.Pixels[s];
                buffer[d + 1] = frame.Pixels[s + 1];
                buffer[d + 2] = frame.Pixels[s + 2];
            }
        }

        return buffer;
    }
}
