using ClaudeDeck.Core.Rendering;

namespace ClaudeDeck.Core.Device;

/// <summary>
/// Convertit une image de touche au format natif du Stream Deck Mini : BMP 24 bits, après rotation/miroirs.
/// L'ordre des opérations reproduit celui de python-elgato-streamdeck (rotation anti-horaire, puis miroir horizontal,
/// puis miroir vertical), ce qui pour le Mini (90°, miroir vertical) revient à une transposition.
/// </summary>
public static class MiniImageEncoder
{
    public static byte[] ToNativeBmp(KeyImage image, int rotationCcw, bool flipHorizontal, bool flipVertical)
    {
        var n = image.Size;
        var src = image.Rgba;
        var rowSize = (n * 3 + 3) & ~3;
        var pixelBytes = rowSize * n;
        var bmp = new byte[54 + pixelBytes];

        // En-tête BITMAPFILEHEADER + BITMAPINFOHEADER.
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        WriteInt(bmp, 2, bmp.Length);
        WriteInt(bmp, 10, 54);
        WriteInt(bmp, 14, 40);
        WriteInt(bmp, 18, n);
        WriteInt(bmp, 22, n); // hauteur positive : lignes stockées de bas en haut
        bmp[26] = 1;
        bmp[28] = 24;
        WriteInt(bmp, 34, pixelBytes);
        WriteInt(bmp, 38, 2835);
        WriteInt(bmp, 42, 2835);

        var rot = ((rotationCcw % 360) + 360) % 360;
        for (var y = 0; y < n; y++)
        {
            // Ligne BMP : la première ligne stockée est la dernière de l'image.
            var dstRow = 54 + (n - 1 - y) * rowSize;
            for (var x = 0; x < n; x++)
            {
                var (sx, sy) = SourceOf(x, y, n, rot, flipHorizontal, flipVertical);
                var si = (sy * n + sx) * 4;
                var a = src[si + 3];
                // Composition sur fond noir (l'écran n'a pas de transparence).
                var r = (byte)(src[si] * a / 255);
                var g = (byte)(src[si + 1] * a / 255);
                var b = (byte)(src[si + 2] * a / 255);
                var di = dstRow + x * 3;
                bmp[di] = b; bmp[di + 1] = g; bmp[di + 2] = r;
            }
        }
        return bmp;
    }

    /// <summary>Pour un pixel (x, y) de l'image transformée, coordonnées dans l'image source.</summary>
    internal static (int X, int Y) SourceOf(int x, int y, int n, int rotationCcw, bool flipH, bool flipV)
    {
        // Les transformations sont appliquées dans l'ordre rotation → miroir H → miroir V ;
        // on les inverse donc en partant de la dernière.
        if (flipV) y = n - 1 - y;
        if (flipH) x = n - 1 - x;
        return rotationCcw switch
        {
            90 => (n - 1 - y, x),
            180 => (n - 1 - x, n - 1 - y),
            270 => (y, n - 1 - x),
            _ => (x, y),
        };
    }

    private static void WriteInt(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }
}
