using System.Text;
using System.Text.RegularExpressions;

namespace UtilesApi.Infrastructure.Security;

/// <summary>
/// Validación de archivos subidos: extensión, tipo declarado y firma real del contenido (magic bytes),
/// para que nadie suba HTML/SVG/ejecutables con otro nombre al almacenamiento público.
/// </summary>
public static class Archivos
{
    public const long MaxBytesPorArchivo = 10 * 1024 * 1024;   // 10 MB
    public const int MaxArchivosLista = 6;
    /// <summary>Tamaño máximo del cuerpo en endpoints de subida (Kestrel / multipart).</summary>
    public const long MaxBytesSubida = 25 * 1024 * 1024;       // 25 MB

    public enum Tipo { Jpeg, Png, Webp, Gif, Heic, Pdf, Docx, Doc }

    /// <summary>Lista de útiles: fotos, PDF o Word (Word ya se aceptaba y la IA lee .docx).</summary>
    public static readonly Tipo[] Lista = { Tipo.Jpeg, Tipo.Png, Tipo.Webp, Tipo.Heic, Tipo.Pdf, Tipo.Docx, Tipo.Doc };

    /// <summary>Imágenes del panel (productos, marcas, campañas, noticias). Sin SVG: puede llevar scripts.</summary>
    public static readonly Tipo[] Imagenes = { Tipo.Jpeg, Tipo.Png, Tipo.Webp, Tipo.Gif };

    private static readonly Dictionary<string, Tipo> Extensiones = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = Tipo.Jpeg, [".jpeg"] = Tipo.Jpeg, [".jfif"] = Tipo.Jpeg,
        [".png"] = Tipo.Png,
        [".webp"] = Tipo.Webp,
        [".gif"] = Tipo.Gif,
        [".heic"] = Tipo.Heic, [".heif"] = Tipo.Heic,
        [".pdf"] = Tipo.Pdf,
        [".docx"] = Tipo.Docx,
        [".doc"] = Tipo.Doc,
    };

    private static readonly Dictionary<Tipo, string> ContentTypes = new()
    {
        [Tipo.Jpeg] = "image/jpeg",
        [Tipo.Png] = "image/png",
        [Tipo.Webp] = "image/webp",
        [Tipo.Gif] = "image/gif",
        [Tipo.Heic] = "image/heic",
        [Tipo.Pdf] = "application/pdf",
        [Tipo.Docx] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [Tipo.Doc] = "application/msword",
    };

    public sealed record Resultado(string? Error, string ContentType, string NombreSeguro);

    /// <summary>
    /// Revisa un archivo. Devuelve el content-type que se debe guardar (el real, no el que dijo el navegador)
    /// y un nombre de archivo sin caracteres raros; o un mensaje de error en español.
    /// </summary>
    public static async Task<Resultado> Validar(IFormFile? file, Tipo[] permitidos, long maxBytes = MaxBytesPorArchivo)
    {
        if (file == null || file.Length == 0)
            return new Resultado("El archivo está vacío", "", "");
        var nombre = NombreSeguro(file.FileName);
        if (file.Length > maxBytes)
            return new Resultado($"\"{nombre}\" pesa más de {maxBytes / (1024 * 1024)} MB", "", nombre);

        var ext = Path.GetExtension(file.FileName ?? "");
        Tipo? porExtension = Extensiones.TryGetValue(ext, out var t) ? t : null;

        var cabecera = new byte[16];
        int leidos;
        await using (var s = file.OpenReadStream())
        {
            leidos = await s.ReadAtLeastAsync(cabecera, cabecera.Length, throwOnEndOfStream: false);
        }
        var real = Detectar(cabecera.AsSpan(0, leidos));

        // .docx es un zip: la firma solo dice "zip", así que se exige además la extensión .docx
        if (real == Tipo.Docx && porExtension != Tipo.Docx) real = null;

        if (real == null || !permitidos.Contains(real.Value))
            return new Resultado($"Tipo de archivo no permitido: \"{nombre}\". Sube {Describir(permitidos)}.", "", nombre);

        return new Resultado(null, ContentTypes[real.Value], nombre);
    }

    public static string Describir(Tipo[] permitidos) =>
        permitidos.SequenceEqual(Imagenes) ? "una imagen JPG, PNG, WEBP o GIF" : "fotos (JPG, PNG, WEBP, HEIC), PDF o Word";

    /// <summary>Firma de los primeros bytes.</summary>
    public static Tipo? Detectar(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return Tipo.Jpeg;
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return Tipo.Png;
        if (b.Length >= 12 && Ascii(b[..4]) == "RIFF" && Ascii(b[8..12]) == "WEBP") return Tipo.Webp;
        if (b.Length >= 6 && (Ascii(b[..6]) == "GIF87a" || Ascii(b[..6]) == "GIF89a")) return Tipo.Gif;
        if (b.Length >= 12 && Ascii(b[4..8]) == "ftyp")
        {
            var marca = Ascii(b[8..12]);
            if (marca is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1") return Tipo.Heic;
        }
        if (b.Length >= 5 && Ascii(b[..5]) == "%PDF-") return Tipo.Pdf;
        if (b.Length >= 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 0x03 && b[3] == 0x04) return Tipo.Docx;
        if (b.Length >= 8 && b[..8].SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 })) return Tipo.Doc;
        return null;
    }

    private static string Ascii(ReadOnlySpan<byte> b) => Encoding.ASCII.GetString(b);

    /// <summary>Nombre de archivo sin rutas ni caracteres especiales (se usa en la clave del almacenamiento).</summary>
    public static string NombreSeguro(string? nombre)
    {
        var n = Path.GetFileName(nombre ?? "");
        n = Regex.Replace(n, @"[^A-Za-z0-9._-]+", "_").Trim('.', '_');
        if (n.Length > 80) n = n[^80..];
        return string.IsNullOrEmpty(n) ? "archivo" : n;
    }
}
