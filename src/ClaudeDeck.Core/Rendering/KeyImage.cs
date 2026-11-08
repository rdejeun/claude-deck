namespace ClaudeDeck.Core.Rendering;

/// <summary>Image carrée d'une touche, pixels RGBA 8 bits non prémultipliés, ligne par ligne depuis le haut.</summary>
public sealed class KeyImage
{
    public KeyImage(int size, byte[] rgba)
    {
        if (rgba.Length != size * size * 4) throw new ArgumentException("Taille de tampon incohérente.", nameof(rgba));
        Size = size;
        Rgba = rgba;
        Hash = ComputeHash(rgba);
    }

    public int Size { get; }
    public byte[] Rgba { get; }

    /// <summary>Empreinte du contenu : permet de ne renvoyer au périphérique que les touches modifiées.</summary>
    public ulong Hash { get; }

    public static KeyImage Solid(int size, byte r, byte g, byte b)
    {
        var buf = new byte[size * size * 4];
        for (var i = 0; i < buf.Length; i += 4)
        {
            buf[i] = r; buf[i + 1] = g; buf[i + 2] = b; buf[i + 3] = 255;
        }
        return new KeyImage(size, buf);
    }

    private static ulong ComputeHash(byte[] data)
    {
        // FNV-1a 64 bits : suffisant pour détecter un changement d'image.
        ulong h = 14695981039346656037UL;
        foreach (var b in data)
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }
}
