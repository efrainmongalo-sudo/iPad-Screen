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

    private static readonly string[] Markers = { "Virtual Display", "IddSampleDriver", "MttVDD" };

    /// <summary>Nombre Windows del monitor virtual (por ejemplo \\.\DISPLAY5), o null si no está instalado.</summary>
    public static string? FindDeviceName() => FindDevice(out _);

    private static string? FindDevice(out bool attached)
    {
        attached = false;
        var device = NewDisplayDevice();
        for (uint i = 0; i < 64 && EnumDisplayDevices(null, i, ref device, 0); i++)
        {
            string description = device.DeviceString ?? "";
            if (Markers.Any(m => description.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                attached = (device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;
                return device.DeviceName;
            }
            device = NewDisplayDevice();
        }
        return null;
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

    /// <summary>Devuelve el monitor virtual listo para usarse (conectado al escritorio) o null con el motivo.</summary>
    public static string? EnsureReady(out string message)
    {
        var device = FindDevice(out bool attached);
        if (device == null)
        {
            message = "Falta la pantalla virtual: pulsa «Instalar y configurar» en la ventana.";
            return null;
        }

        if (!attached || GetCurrentBounds(device) == null)
        {
            if (!SetMode(device, 1024, 768, out message)) return null;
        }

        message = "";
        return device;
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

    /// <summary>Pone el monitor virtual en la resolución pedida (o la más parecida disponible).</summary>
    public static bool SetMode(string device, int width, int height, out string message)
    {
        var modes = GetModes(device);
        if (modes.Count == 0)
        {
            message = "La pantalla virtual no tiene modos de video. Pulsa «Reconfigurar»; si sigue igual, reinicia Windows.";
            return false;
        }

        var target = ChooseMode(modes, width, height);
        string note = target.Width == width && target.Height == height
            ? $"Pantalla virtual: {target.Width}×{target.Height}."
            : $"La iPad pide {width}×{height}; se usó {target.Width}×{target.Height}, el más parecido disponible.";

        var current = GetCurrentBounds(device);
        if (current != null && current.Value.Width == target.Width && current.Value.Height == target.Height)
        {
            message = note;
            return true; // ya estaba en ese modo
        }

        if (current == null)
        {
            current = AttachToDesktop(device);
            if (current == null)
            {
                message = "Windows no pudo conectar la pantalla virtual al escritorio. Reinicia la laptop o usa Win+P > Extender.";
                return false;
            }
            if (current.Value.Width == target.Width && current.Value.Height == target.Height)
            {
                message = note;
                return true;
            }
        }

        int result = ApplyMode(device, target);
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

        message = note;
        return true;
    }


    /// <summary>Conecta al escritorio un monitor que Windows tiene desactivado (equivale a Win+P > Extender).</summary>
    private static Rectangle? AttachToDesktop(string device)
    {
        SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND | SDC_ALLOW_CHANGES);
        var bounds = WaitForBounds(device, 3000);
        if (bounds != null) return bounds;

        // Alternativa: pedirlo directamente para ese monitor y aplicar los cambios pendientes.
        var dm = NewDevMode();
        dm.dmDeviceName = device;
        dm.dmPositionX = Screen.AllScreens.Max(s => s.Bounds.Right);
        dm.dmPositionY = 0;
        dm.dmBitsPerPel = 32;
        dm.dmPelsWidth = 1024;
        dm.dmPelsHeight = 768;
        dm.dmFields = DM_POSITION | DM_BITSPERPEL | DM_PELSWIDTH | DM_PELSHEIGHT;
        ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
        ApplyPendingDisplayChanges(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        return WaitForBounds(device, 3000);
    }

    private static Rectangle? WaitForBounds(string device, int milliseconds)
    {
        for (int waited = 0; waited <= milliseconds; waited += 100)
        {
            var bounds = GetCurrentBounds(device);
            if (bounds != null) return bounds;
            Thread.Sleep(100);
        }
        return null;
    }

    /// <summary>Cambia la resolución del monitor ya conectado; prueba varias formas porque los drivers virtuales son quisquillosos.</summary>
    private static int ApplyMode(string device, DisplayMode target)
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
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT;
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

    // Tamaños de iPad (horizontal). Se agregan también en vertical.
    private static readonly (int W, int H)[] IpadSizes =
    {
        (1024, 768), (2048, 1536),   // iPad 1 a 9.ª gen. de 9.7", Air 1 y 2, mini
        (1366, 1024), (2732, 2048),  // iPad Pro 12.9"
        (1194, 834), (2388, 1668),   // iPad Pro 11"
        (1180, 820), (2360, 1640),   // iPad Air 4 y 5
        (1112, 834), (2224, 1668),   // iPad Pro 10.5"
        (1080, 810), (2160, 1620),   // iPad 10.2"
        (1133, 744), (2266, 1488),   // iPad mini 6
    };

    public static string BuildSettingsXml()
    {
        var sizes = new List<(int, int)>();
        foreach (var (w, h) in IpadSizes)
        {
            sizes.Add((w, h));
            sizes.Add((h, w));
        }
        sizes.Add((1920, 1080));
        sizes.Add((1280, 720));

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version='1.0' encoding='utf-8'?>");
        sb.AppendLine("<vdd_settings>");
        sb.AppendLine("  <monitors><count>1</count></monitors>");
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
