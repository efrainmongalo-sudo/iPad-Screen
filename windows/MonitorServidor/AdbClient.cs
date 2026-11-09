using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace MonitorServidor;

internal sealed class AdbDevice
{
    public AdbDevice(string serial, string state)
    {
        Serial = serial;
        State = state;
    }

    public string Serial { get; }

    /// <summary>"device" = listo; "unauthorized" = falta aceptar la depuración USB en el teléfono; "offline".</summary>
    public string State { get; }
}

/// <summary>
/// Conexión con teléfonos y tabletas Android por el cable USB, usando adb (Android Platform Tools).
/// «adb forward» abre un puerto local de la PC que llega al puerto de la app dentro del teléfono.
/// </summary>
internal static class AdbClient
{
    private static string? _adbPath;
    private static bool _serverStarted;

    public static string? FindAdb()
    {
        if (_adbPath != null && File.Exists(_adbPath)) return _adbPath;

        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "adb.exe") };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim().Trim('"'), "adb.exe")); } catch { }
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AddWingetCandidates(candidates, local);
        try
        {
            foreach (var user in Directory.GetDirectories(@"C:\Users"))
                AddWingetCandidates(candidates, Path.Combine(user, "AppData", "Local"));
        }
        catch
        {
            // Sin acceso a otras cuentas: se ignoran.
        }

        candidates.Add(Path.Combine(local, @"Android\Sdk\platform-tools\adb.exe"));
        candidates.Add(@"C:\platform-tools\adb.exe");
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"platform-tools\adb.exe"));

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    _adbPath = candidate;
                    return candidate;
                }
            }
            catch
            {
                // Ruta inválida: se ignora.
            }
        }
        return null;
    }

    private static void AddWingetCandidates(List<string> candidates, string localAppData)
    {
        candidates.Add(Path.Combine(localAppData, @"Microsoft\WinGet\Links\adb.exe"));
        try
        {
            string packages = Path.Combine(localAppData, @"Microsoft\WinGet\Packages");
            if (!Directory.Exists(packages)) return;
            foreach (var dir in Directory.GetDirectories(packages, "Google.PlatformTools*"))
            {
                candidates.Add(Path.Combine(dir, "platform-tools", "adb.exe"));
                candidates.Add(Path.Combine(dir, "adb.exe"));
            }
        }
        catch
        {
            // Sin acceso: se ignora.
        }
    }

    /// <summary>Ejecuta adb sin ventana y devuelve lo que escribió. Para comandos con el servidor ya iniciado.</summary>
    private static string Run(string adb, string arguments, int timeoutMs = 5000)
    {
        var info = new ProcessStartInfo(adb, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(info) ?? throw new IOException("No se pudo ejecutar adb.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(true); } catch { }
            _serverStarted = false; // se vuelve a intentar iniciar el servidor de adb
            throw new IOException("adb no respondió.");
        }
        process.WaitForExit(); // espera a que se vacíen las salidas
        return output.Result + "\n" + error.Result;
    }

    /// <summary>Inicia el servidor de adb. Sin redirigir la salida: el servidor se queda abierto y la bloquearía.</summary>
    private static void EnsureServer(string adb)
    {
        if (_serverStarted) return;
        var info = new ProcessStartInfo(adb, "start-server") { UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(info);
        process?.WaitForExit(10000);
        _serverStarted = true;
    }

    public static List<AdbDevice> ListDevices()
    {
        var adb = FindAdb();
        var devices = new List<AdbDevice>();
        if (adb == null) return devices;

        EnsureServer(adb);
        foreach (var raw in Run(adb, "devices").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('*') || line.StartsWith("List of", StringComparison.OrdinalIgnoreCase))
                continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) devices.Add(new AdbDevice(parts[0], parts[1]));
        }
        return devices;
    }

    /// <summary>Abre un puerto local que llega al puerto de la app en el teléfono y devuelve ese puerto.</summary>
    public static int Forward(string serial, int devicePort)
    {
        var adb = FindAdb() ?? throw new IOException("Falta adb.");
        string output = Run(adb, $"-s {serial} forward tcp:0 tcp:{devicePort}");
        foreach (var line in output.Split('\n'))
        {
            if (int.TryParse(line.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port > 0)
                return port;
        }
        throw new IOException("adb no devolvió el puerto: " + output.Trim());
    }

    public static void RemoveForward(string serial, int localPort)
    {
        try
        {
            var adb = FindAdb();
            if (adb != null) Run(adb, $"-s {serial} forward --remove tcp:{localPort}", 3000);
        }
        catch
        {
            // El teléfono ya no está: no hay nada que quitar.
        }
    }

    public static TcpClient ConnectLocal(int port)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            client.Connect(IPAddress.Loopback, port);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
