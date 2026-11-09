using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MonitorServidor;

internal sealed class DisplayMode
{
    public DisplayMode(int width, int height, int hz)
    {
        Width = width;
        Height = height;
        Hz = hz;
    }

    public int Width { get; }
    public int Height { get; }
    public int Hz { get; }
}

/// <summary>
/// Control de la pantalla virtual (driver «Virtual Display Driver» de VirtualDrivers, firmado, que se
/// instala con winget). El driver publica una lista de resoluciones; aquí se detecta el monitor,
/// se cambia entre esas resoluciones cuando la iPad gira y se prepara la instalación.
/// </summary>
internal static class VirtualDisplay
{
    // ---------------------------------------------------------------- Win32

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;
    private const uint CDS_UPDATEREGISTRY = 0x1;
    private const int DISP_CHANGE_SUCCESSFUL = 0;
    private const int DM_POSITION = 0x20;
    private const int DM_BITSPERPEL = 0x40000;
    private const int DM_PELSWIDTH = 0x80000;
    private const int DM_PELSHEIGHT = 0x100000;
    private const int DM_DISPLAYFREQUENCY = 0x400000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
    private static extern int ApplyPendingDisplayChanges(string? lpszDeviceName, IntPtr lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray, uint numModeInfoArrayElements, IntPtr modeInfoArray, uint flags);

    private const uint CDS_NORESET = 0x10000000;
    private const uint SDC_TOPOLOGY_INTERNAL = 0x1;
    private const uint SDC_TOPOLOGY_EXTEND = 0x4;
    private const uint SDC_APPLY = 0x80;
    private const uint SDC_ALLOW_CHANGES = 0x400;

    private static DEVMODE NewDevMode()
    {
        var dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf<DEVMODE>();
        return dm;
    }

    private static DISPLAY_DEVICE NewDisplayDevice()
    {
        var dd = new DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        return dd;
    }

    // ---------------------------------------------------------------- Detección

    /// <summary>Lado del monitor principal (la laptop) donde se coloca una pantalla virtual.</summary>
    public enum Side { Right, Left }

    private static readonly string[] Markers = { "Virtual Display", "IddSampleDriver", "MttVDD" };

    // Todas las operaciones que cambian pantallas pasan por este candado: varios dispositivos pueden conectarse a la vez.
    private static readonly object Gate = new();
    private static readonly HashSet<string> Claimed = new();

    private static int DisplayNumber(string name)
    {
        int end = name.Length, start = end;
        while (start > 0 && char.IsDigit(name[start - 1])) start--;
        return start < end && int.TryParse(name[start..end], out int n) ? n : int.MaxValue;
    }

    /// <summary>Nombres Windows de las pantallas virtuales (por ejemplo \\.\DISPLAY5), en orden. Vacío si no está instalado.</summary>
    public static List<string> FindDevices()
    {
        var result = new List<string>();
        var device = NewDisplayDevice();
        for (uint i = 0; i < 64 && EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            string description = device.DeviceString ?? "";
            if (Markers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase)))
                result.Add(device.DeviceName);
            device = NewDisplayDevice();
        }
        return result.OrderBy(DisplayNumber).ToList();
    }

    public static string? FindDeviceName() => FindDevices().FirstOrDefault();

    private static bool IsAttached(string name)
    {
        var device = NewDisplayDevice();
        for (uint i = 0; i < 64 && EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            if (string.Equals(device.DeviceName, name, StringComparison.OrdinalIgnoreCase))
                return (device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
            device = NewDisplayDevice();
        }
        return false;
    }

    public static List<DisplayMode> GetModes(string device)
    {
        var modes = new List<DisplayMode>();
        var dm = NewDevMode();
        for (int i = 0; i < 2000 && EnumDisplaySettings(device, i, ref dm); i++)
        {
            if (dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                modes.Add(new DisplayMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
            dm = NewDevMode();
        }
        return modes;
    }

    /// <summary>Posición y tamaño actuales del monitor en el escritorio, o null si no está conectado.</summary>
    public static Rectangle? GetCurrentBounds(string device)
    {
        var dm = NewDevMode();
        if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm) || dm.dmPelsWidth <= 0 || dm.dmPelsHeight <= 0)
            return null;
        return new Rectangle(dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight);
    }

    /// <summary>
    /// Reserva una pantalla virtual libre para un dispositivo. La primera pantalla virtual queda a la derecha
    /// de la laptop y la segunda a la izquierda. Devuelve null (con el motivo) si no hay ninguna libre.
    /// </summary>
    public static (string Device, Side Side)? Claim(Side preferred, out string message)
    {
        lock (Gate)
        {
            var all = FindDevices();
            if (all.Count == 0)
            {
                message = "Falta la pantalla virtual: reinstala el programa.";
                return null;
            }

            string? Slot(Side side) => side == Side.Right ? all[0] : (all.Count > 1 ? all[1] : null);
            Side other = preferred == Side.Right ? Side.Left : Side.Right;
            foreach (var side in new[] { preferred, other })
            {
                var name = Slot(side);
                if (name != null && Claimed.Add(name))
                {
                    message = "";
                    return (name, side);
                }
            }

            message = all.Count < 2
                ? "Solo hay una pantalla virtual. Reinstala el programa para activar la segunda."
                : "No hay más pantallas virtuales libres.";
            return null;
        }
    }

    /// <summary>Libera la pantalla virtual y la quita del escritorio (la laptop recupera su distribución).</summary>
    public static void Release(string device)
    {
        lock (Gate)
        {
            Claimed.Remove(device);
            Detach(device);
        }
    }

    // ---------------------------------------------------------------- Cambio de resolución

    private static DisplayMode ChooseMode(List<DisplayMode> modes, int width, int height)
    {
        static DisplayMode Best(IEnumerable<DisplayMode> group) => group.OrderBy(m => Math.Abs(m.Hz - 60)).First();

        var exact = modes.Where(m => m.Width == width && m.Height == height).ToList();
        if (exact.Count > 0) return Best(exact);

        double aspect = (double)width / height;
        var sameAspect = modes.Where(m => Math.Abs((double)m.Width / m.Height - aspect) < 0.02)
                              .OrderBy(m => Math.Abs(m.Width - width)).ToList();
        if (sameAspect.Count > 0)
        {
            var size = sameAspect[0];
            return Best(sameAspect.Where(m => m.Width == size.Width && m.Height == size.Height));
        }

        var closest = modes.OrderBy(m => Math.Abs((long)m.Width * m.Height - (long)width * height)).First();
        return Best(modes.Where(m => m.Width == closest.Width && m.Height == closest.Height));
    }

    private static Point DesiredPosition(Side side, DisplayMode mode)
    {
        var primary = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).Bounds;
        return side == Side.Left
            ? new Point(primary.Left - mode.Width, primary.Top)
            : new Point(primary.Right, primary.Top);
    }

    /// <summary>Pone la pantalla virtual en la resolución pedida (o la más parecida) y a su lado de la laptop.</summary>
    public static bool SetMode(string device, Side side, int width, int height, out string message)
    {
        lock (Gate)
        {
            var modes = GetModes(device);
            if (modes.Count == 0)
            {
                // Una pantalla desconectada del escritorio no siempre publica sus modos: se conecta primero
                // (con el tamaño pedido) y se vuelve a preguntar.
                var probe = new DisplayMode(width, height, 60);
                var probePos = DesiredPosition(side, probe);
                AttachToDesktop(device, probe, probePos);
                for (int i = 0; i < 20 && modes.Count == 0; i++)
                {
                    modes = GetModes(device);
                    if (modes.Count == 0) Thread.Sleep(150);
                }
            }
            if (modes.Count == 0)
            {
                var current0 = IsAttached(device) ? GetCurrentBounds(device) : null;
                if (current0 != null && current0.Value.Width == width && current0.Value.Height == height)
                {
                    message = $"Pantalla virtual: {width}×{height}.";
                    return true;
                }
                // Último recurso: se intenta el tamaño pedido tal cual (el driver lo tiene en su lista).
                modes = new List<DisplayMode> { new DisplayMode(width, height, 60) };
            }

            var target = ChooseMode(modes, width, height);
            string note = target.Width == width && target.Height == height
                ? $"Pantalla virtual: {target.Width}×{target.Height}."
                : $"El dispositivo pide {width}×{height}; se usó {target.Width}×{target.Height}, el más parecido disponible.";

            var pos = DesiredPosition(side, target);
            var want = new Rectangle(pos.X, pos.Y, target.Width, target.Height);

            var current = IsAttached(device) ? GetCurrentBounds(device) : null;
            if (current == null)
            {
                current = AttachToDesktop(device, target, pos);
                if (current == null)
                {
                    message = "Windows no pudo conectar la pantalla virtual al escritorio. Reinicia la laptop o usa Win+P > Extender.";
                    return false;
                }
            }

            if (current.Value != want)
            {
                int result = ApplyMode(device, target, pos);
                if (result != DISP_CHANGE_SUCCESSFUL)
                {
                    message = $"Windows no aceptó {target.Width}×{target.Height} (código {result}).";
                    return false;
                }

                // Windows tarda un instante en reconfigurar el monitor.
                for (int i = 0; i < 40; i++)
                {
                    var now = GetCurrentBounds(device);
                    if (now != null && now.Value.Width == target.Width && now.Value.Height == target.Height) break;
                    Thread.Sleep(50);
                }
            }

            message = note;
            return true;
        }
    }

    /// <summary>
    /// Quita una pantalla virtual del escritorio de Windows (queda desactivada, no desinstalada), para que
    /// la laptop vuelva a su distribución de antes cuando se termina de usar el dispositivo.
    /// </summary>
    public static void Detach(string device)
    {
        lock (Gate)
        {
            if (!IsAttached(device)) return;

            // Forma estándar de desconectar un monitor: posición y tamaño en cero, y aplicar los cambios.
            var dm = NewDevMode();
            dm.dmDeviceName = device;
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
            ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            ApplyPendingDisplayChanges(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            if (WaitDetached(device, 2000)) return;

            // Alternativa («Solo pantalla de la PC»): solo si ya no queda ninguna otra pantalla virtual en uso.
            if (Claimed.Count == 0)
            {
                SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_INTERNAL | SDC_ALLOW_CHANGES);
                WaitDetached(device, 2000);
            }
        }
    }

    /// <summary>Quita del escritorio todas las pantallas virtuales (por ejemplo, al cerrar el programa).</summary>
    public static void DetachAll()
    {
        lock (Gate)
        {
            Claimed.Clear();
            foreach (var name in FindDevices()) Detach(name);
        }
    }

    private static bool WaitDetached(string device, int milliseconds)
    {
        for (int waited = 0; waited <= milliseconds; waited += 100)
        {
            if (!IsAttached(device)) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>Conecta al escritorio una pantalla que Windows tiene desactivada.</summary>
    private static Rectangle? AttachToDesktop(string device, DisplayMode target, Point pos)
    {
        // 1) Pedirlo solo para ese monitor, con su tamaño y posición, y aplicar los cambios pendientes.
        var dm = NewDevMode();
        dm.dmDeviceName = device;
        dm.dmPositionX = pos.X;
        dm.dmPositionY = pos.Y;
        dm.dmBitsPerPel = 32;
        dm.dmPelsWidth = target.Width;
        dm.dmPelsHeight = target.Height;
        dm.dmDisplayFrequency = target.Hz;
        dm.dmFields = DM_POSITION | DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        ApplyPendingDisplayChanges(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        var bounds = WaitForBounds(device, 2500);
        if (bounds != null) return bounds;

        // 2) Win+P > Extender (conecta todas las desactivadas) y luego se quitan las que no se están usando.
        SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND | SDC_ALLOW_CHANGES);
        bounds = WaitForBounds(device, 3000);
        foreach (var name in FindDevices())
        {
            if (name != device && !Claimed.Contains(name)) Detach(name);
        }
        return bounds;
    }

    private static Rectangle? WaitForBounds(string device, int milliseconds)
    {
        for (int waited = 0; waited <= milliseconds; waited += 100)
        {
            if (IsAttached(device))
            {
                var bounds = GetCurrentBounds(device);
                if (bounds != null) return bounds;
            }
            Thread.Sleep(100);
        }
        return null;
    }

    /// <summary>Cambia resolución y posición del monitor ya conectado; prueba varias formas porque los drivers virtuales son quisquillosos.</summary>
    private static int ApplyMode(string device, DisplayMode target, Point pos)
    {
        int result = -1;
        var attempts = new (bool Frequency, uint Flags)[]
        {
            (true, CDS_UPDATEREGISTRY),
            (false, CDS_UPDATEREGISTRY),
            (false, 0),
        };

        foreach (var (frequency, flags) in attempts)
        {
            var dm = NewDevMode();
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) continue;
            dm.dmPelsWidth = target.Width;
            dm.dmPelsHeight = target.Height;
            dm.dmPositionX = pos.X;
            dm.dmPositionY = pos.Y;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_POSITION;
            if (frequency)
            {
                dm.dmDisplayFrequency = target.Hz;
                dm.dmFields |= DM_DISPLAYFREQUENCY;
            }

            result = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            if (result == DISP_CHANGE_SUCCESSFUL) break;
        }
        return result;
    }

    // ---------------------------------------------------------------- Instalación y configuración

    /// <summary>Se escribe dentro del XML; si falta, la configuración es de una versión anterior y hay que reescribirla.</summary>
    private const string ConfigMarker = "monitor-secundario-v3";
    private const string ConfigPath = @"C:\VirtualDisplayDriver\vdd_settings.xml";

    // Tamaños (horizontal). Se agregan también en vertical.
    private static readonly (int W, int H)[] DeviceSizes =
    {
        // iPad
        (1024, 768), (2048, 1536),   // iPad 1 a 9.ª gen. de 9.7", Air 1 y 2, mini
        (1366, 1024), (2732, 2048),  // iPad Pro 12.9"
        (1194, 834), (2388, 1668),   // iPad Pro 11"
        (1180, 820), (2360, 1640),   // iPad Air 4 y 5
        (1112, 834), (2224, 1668),   // iPad Pro 10.5"
        (1080, 810), (2160, 1620),   // iPad 10.2"
        (1133, 744), (2266, 1488),   // iPad mini 6
        // Teléfonos: puntos ×2 (tamaño cómodo en Windows) y píxeles reales
        (1136, 640), (1334, 750), (1472, 828), (1624, 750), (1792, 828), (1688, 780),
        (1852, 856), (1704, 786), (1864, 860), (1748, 804), (1912, 880),
        (2436, 1125), (2532, 1170), (2556, 1179), (2688, 1242), (2208, 1242), (2778, 1284), (2796, 1290),
        // Teléfonos Android habituales (dp ×2)
        (1560, 720), (1600, 720), (1920, 864), (1280, 720), (1480, 720), (1560, 720), (1800, 810), (2080, 960),
        // Pantallas de escritorio de uso común
        (1920, 1080),
    };

    public static string BuildSettingsXml()
    {
        var sizes = new List<(int, int)>();
        foreach (var (w, h) in DeviceSizes)
        {
            sizes.Add((w, h));
            sizes.Add((h, w));
        }

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version='1.0' encoding='utf-8'?>");
        sb.AppendLine("<vdd_settings>");
        sb.AppendLine($"  <!-- {ConfigMarker} -->");
        sb.AppendLine("  <monitors><count>2</count></monitors>");
        sb.AppendLine("  <gpu><friendlyname>default</friendlyname></gpu>");
        sb.AppendLine("  <global><g_refresh_rate>60</g_refresh_rate></global>");
        sb.AppendLine("  <resolutions>");
        foreach (var (w, h) in sizes.Distinct())
            sb.AppendLine($"    <resolution><width>{w}</width><height>{h}</height><refresh_rate>60</refresh_rate></resolution>");
        sb.AppendLine("  </resolutions>");
        sb.AppendLine("  <logging><SendLogsThroughPipe>false</SendLogsThroughPipe><logging>false</logging><debuglogging>false</debuglogging></logging>");
        sb.AppendLine("  <colour><SDR10bit>false</SDR10bit><HDRPlus>false</HDRPlus><ColourFormat>RGB</ColourFormat></colour>");
        sb.AppendLine("</vdd_settings>");
        return sb.ToString();
    }

    /// <summary>true si el driver está instalado con la configuración actual (todos los tamaños y dos pantallas).</summary>
    public static bool IsConfigCurrent()
    {
        try
        {
            return FindDevices().Count >= 1 && File.Exists(ConfigPath) && File.ReadAllText(ConfigPath).Contains(ConfigMarker);
        }
        catch
        {
            return false;
        }
    }

    private const string SetupScript = """
        $ErrorActionPreference = 'Stop'
        Start-Transcript -Path '@@LOG@@' -Force | Out-Null
        try {
            $pattern = 'Virtual Display Driver|IddSampleDriver|MttVDD'
            $found = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match $pattern }
            if (-not $found) {
                if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw 'winget no esta disponible en este Windows.' }
                Write-Host 'Instalando Virtual Display Driver con winget...'
                winget install --id=VirtualDrivers.Virtual-Display-Driver -e --silent --accept-package-agreements --accept-source-agreements
                Start-Sleep -Seconds 4
                $found = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match $pattern }
                if (-not $found) { throw 'No aparecio el dispositivo de pantalla virtual despues de instalar.' }
            }
            $dir = 'C:\VirtualDisplayDriver'
            if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
            $dest = Join-Path $dir 'vdd_settings.xml'
            if (Test-Path $dest) { Copy-Item $dest ($dest + '.bak') -Force }
            Copy-Item '@@XML@@' $dest -Force
            foreach ($d in $found) {
                Disable-PnpDevice -InstanceId $d.InstanceId -Confirm:$false
                Start-Sleep -Seconds 2
                Enable-PnpDevice -InstanceId $d.InstanceId -Confirm:$false
            }
            # Para teléfonos Android: herramientas de depuración USB (adb). Si falla, no impide lo demás.
            try {
                $adb = Get-Command adb -ErrorAction SilentlyContinue
                if (-not $adb -and (Get-Command winget -ErrorAction SilentlyContinue)) {
                    Write-Host 'Instalando Android Platform Tools (adb) con winget...'
                    winget install --id=Google.PlatformTools -e --silent --accept-package-agreements --accept-source-agreements
                }
            } catch { Write-Host ('AVISO: no se pudo instalar adb: ' + $_) }
            Write-Host 'LISTO'
            exit 0
        } catch {
            Write-Host ('ERROR: ' + $_)
            exit 1
        } finally {
            Stop-Transcript | Out-Null
        }
        """;

    /// <summary>
    /// Instala el driver con winget si falta, escribe la lista de resoluciones de iPad y reinicia el dispositivo.
    /// Windows pide permiso de administrador una vez. Bloquea hasta terminar: llamar desde un hilo de fondo.
    /// </summary>
    public static (bool Ok, string Message) InstallAndConfigure()
    {
        string dir = Path.Combine(Path.GetTempPath(), "monitor-vdd");
        Directory.CreateDirectory(dir);
        string xmlPath = Path.Combine(dir, "vdd_settings.xml");
        string scriptPath = Path.Combine(dir, "setup.ps1");
        string logPath = Path.Combine(dir, "setup.log");

        try { File.Delete(logPath); } catch { }
        File.WriteAllText(xmlPath, BuildSettingsXml(), new UTF8Encoding(false));
        File.WriteAllText(scriptPath,
            SetupScript.Replace("@@XML@@", xmlPath).Replace("@@LOG@@", logPath),
            new UTF8Encoding(true));

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        int exitCode;
        try
        {
            using var process = Process.Start(startInfo);
            if (process == null) return (false, "No se pudo iniciar la instalación.");
            process.WaitForExit();
            exitCode = process.ExitCode;
        }
        catch (Win32Exception)
        {
            return (false, "Se canceló el permiso de administrador; no se instaló nada.");
        }

        if (exitCode == 0)
            return (true, "Pantalla virtual lista. Si no aparece en Windows, reinicia la laptop una vez.");

        string detail = "";
        try
        {
            var lines = File.ReadAllLines(logPath).Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)).TakeLast(3);
            detail = " " + string.Join(" ", lines);
        }
        catch
        {
            // Sin registro disponible.
        }
        return (false, "No se pudo instalar la pantalla virtual." + detail);
    }
}
