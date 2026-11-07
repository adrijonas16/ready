using System.Collections.Concurrent;

namespace UtilesApi.Infrastructure.Security;

/// <summary>
/// Protección del login por correo (además del límite por IP):
/// - como mucho 10 intentos por minuto para un mismo correo;
/// - tras 5 contraseñas incorrectas en 15 minutos, el correo queda bloqueado hasta que pasen 15 minutos desde el primero.
/// Vale también para correos que no existen, así no se puede saber cuáles están registrados.
/// En memoria del proceso (una sola instancia).
/// </summary>
public class LoginGuard
{
    public const int MaxFallos = 5;
    public static readonly TimeSpan VentanaFallos = TimeSpan.FromMinutes(15);
    public const int MaxIntentosPorMinuto = 10;

    private readonly ConcurrentDictionary<string, Registro> _registros = new();
    private readonly TimeProvider _reloj;
    private DateTimeOffset _ultimaLimpieza;

    public LoginGuard(TimeProvider? reloj = null)
    {
        _reloj = reloj ?? TimeProvider.System;
        _ultimaLimpieza = _reloj.GetUtcNow();
    }

    private sealed class Registro
    {
        public readonly List<DateTimeOffset> Fallos = new();
        public readonly List<DateTimeOffset> Intentos = new();
    }

    private static string Clave(string? email) => (email ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Registra el intento y dice si se puede continuar. Si no, devuelve los segundos que hay que esperar
    /// y si es por contraseñas incorrectas (bloqueo) o por demasiados intentos seguidos.
    /// </summary>
    public (bool Permitido, int EsperarSegundos, bool Bloqueado) Intentar(string? email)
    {
        var ahora = _reloj.GetUtcNow();
        Limpiar(ahora);
        var reg = _registros.GetOrAdd(Clave(email), _ => new Registro());
        lock (reg)
        {
            reg.Fallos.RemoveAll(f => ahora - f >= VentanaFallos);
            if (reg.Fallos.Count >= MaxFallos)
            {
                var hasta = reg.Fallos[0] + VentanaFallos;
                return (false, Math.Max(1, (int)Math.Ceiling((hasta - ahora).TotalSeconds)), true);
            }

            reg.Intentos.RemoveAll(i => ahora - i >= TimeSpan.FromMinutes(1));
            if (reg.Intentos.Count >= MaxIntentosPorMinuto)
            {
                var hasta = reg.Intentos[0] + TimeSpan.FromMinutes(1);
                return (false, Math.Max(1, (int)Math.Ceiling((hasta - ahora).TotalSeconds)), false);
            }
            reg.Intentos.Add(ahora);
            return (true, 0, false);
        }
    }

    public void Fallo(string? email)
    {
        var ahora = _reloj.GetUtcNow();
        var reg = _registros.GetOrAdd(Clave(email), _ => new Registro());
        lock (reg) reg.Fallos.Add(ahora);
    }

    public void Exito(string? email) => _registros.TryRemove(Clave(email), out _);

    /// <summary>Cada 5 minutos borra los correos sin actividad reciente para no crecer sin límite.</summary>
    private void Limpiar(DateTimeOffset ahora)
    {
        if (ahora - _ultimaLimpieza < TimeSpan.FromMinutes(5)) return;
        _ultimaLimpieza = ahora;
        foreach (var (clave, reg) in _registros)
        {
            bool viejo;
            lock (reg)
            {
                viejo = reg.Fallos.All(f => ahora - f >= VentanaFallos) && reg.Intentos.All(i => ahora - i >= TimeSpan.FromMinutes(1));
            }
            if (viejo) _registros.TryRemove(clave, out _);
        }
    }
}
