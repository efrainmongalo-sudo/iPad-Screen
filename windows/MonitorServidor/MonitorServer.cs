using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MonitorServidor;

/// <summary>
/// Motor del monitor secundario (sin interfaz). Busca la iPad por USB, abre un túnel hacia la app,
/// captura una pantalla de Windows, la comprime a JPEG y la envía. Recibe los toques y los convierte
/// en clics del ratón. La iPad informa su tamaño al conectarse y al girar; en modo extendido la pantalla
/// virtual cambia de resolución para coincidir. Los eventos se disparan en hilos de fondo.
/// </summary>
internal sealed class MonitorServer : IDisposable
{
    /// <summary>Puerto en el que escucha la app de la iPad (debe coincidir con StreamServer.port).</summary>
    public const int DevicePort = 5800;

    /// <summary>Cuadros enviados sin confirmar como máximo. Menos cuadros en camino = menos retraso.</summary>
    private const int MaxInFlight = 2;

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint milliseconds);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint milliseconds);

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public Point ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    private const int CURSOR_SHOWING = 0x1;
    private const int DI_NORMAL = 0x3;

    /// <summary>CopyFromScreen no incluye el puntero del ratón: se dibuja encima de la captura.</summary>
    private static void DrawCursor(Graphics g, Rectangle bounds)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero) return;
        if (!bounds.Contains(info.ptScreenPos)) return;

        int hotX = 0, hotY = 0;
        if (GetIconInfo(info.hCursor, out var icon))
        {
            hotX = icon.xHotspot;
            hotY = icon.yHotspot;
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }

        IntPtr hdc = g.GetHdc();
        try
        {
            DrawIconEx(hdc, info.ptScreenPos.X - bounds.X - hotX, info.ptScreenPos.Y - bounds.Y - hotY,
                info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    public event Action<string>? Log;
    public event Action<bool, string>? UsbStatus;       // todo bien, texto para mostrar
    public event Action<bool, string>? ClientChanged;   // conectada, descripción
    public event Action<double, double>? Stats;         // cuadros por segundo, KB por segundo
    public event Action<string>? ModeChanged;           // resolución actual de lo que se envía

    private sealed class Flow
    {
        public int InFlight;
        public readonly ManualResetEventSlim AckEvent = new(false);
    }

    /// <summary>Estado compartido entre el hilo de transmisión y el que lee a la iPad.</summary>
    private sealed class Session
    {
        public Session(bool sharp) { Sharp = sharp; }

        public bool Sharp { get; }
        public readonly ManualResetEventSlim GotSize = new(false);

        private int _version;
        private int _targetW = 1024, _targetH = 768;
        private Rectangle _bounds;

        /// <summary>Aumenta cada vez que la iPad informa un tamaño nuevo.</summary>
        public int Version => Volatile.Read(ref _version);

        public (int W, int H) Target
        {
            get { lock (this) return (_targetW, _targetH); }
        }

        /// <summary>Zona del escritorio que se está compartiendo (para convertir los toques).</summary>
        public Rectangle Bounds
        {
            get { lock (this) return _bounds; }
            set { lock (this) _bounds = value; }
        }

        public void SetTarget(int w, int h)
        {
            lock (this)
            {
                _targetW = w;
                _targetH = h;
            }
            Interlocked.Increment(ref _version);
            GotSize.Set();
        }
    }

    /// <summary>Imágenes de trabajo; se recrean cuando cambia la resolución.</summary>
    private sealed class Surface : IDisposable
    {
        public Surface(Rectangle bounds, int maxW, int maxH)
        {
            Bounds = bounds;
            double scale = Math.Min(1.0, Math.Min((double)maxW / bounds.Width, (double)maxH / bounds.Height));
            OutW = Math.Max(2, (int)(bounds.Width * scale) & ~1);
            OutH = Math.Max(2, (int)(bounds.Height * scale) & ~1);

            Full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            GFull = Graphics.FromImage(Full);
            Small = new Bitmap(OutW, OutH, PixelFormat.Format24bppRgb);
            GSmall = Graphics.FromImage(Small);
            GSmall.InterpolationMode = InterpolationMode.Bilinear;
            GSmall.CompositingQuality = CompositingQuality.HighSpeed;
        }

        public Rectangle Bounds { get; }
        public int OutW { get; }
        public int OutH { get; }
        public Bitmap Full { get; }
        public Bitmap Small { get; }
        public Graphics GFull { get; }
        public Graphics GSmall { get; }
        public bool Scaled => OutW != Bounds.Width || OutH != Bounds.Height;

        public void Dispose()
        {
            GFull.Dispose();
            GSmall.Dispose();
            Full.Dispose();
            Small.Dispose();
        }
    }

    private readonly object _gate = new();
    private TcpClient? _client;
    private CancellationTokenSource? _cts;

    public bool Running
    {
        get { lock (_gate) return _cts != null; }
    }

    /// <param name="mirror">true = duplica la pantalla principal; false = monitor extendido (pantalla virtual).</param>
    /// <param name="sharp">true = resolución real de la iPad; false = su tamaño en puntos (menos retraso).</param>
    public void Start(bool mirror, bool sharp, int quality, int fps)
    {
        lock (_gate)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            new Thread(() => UsbLoop(mirror, sharp, quality, fps, token))
            {
                IsBackground = true,
                Name = "monitor-usb"
            }.Start();
        }
        Log?.Invoke("Buscando la iPad por USB…");
    }

    public void Stop()
    {
        TcpClient? client;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            client = _client;
            cts = _cts;
            _client = null;
            _cts = null;
        }

        cts?.Cancel();
        try { client?.Close(); } catch { }
        if (cts != null) Log?.Invoke("Transmisión detenida.");
    }

    public void Dispose() => Stop();

    private static bool WaitCancelled(CancellationToken token, int milliseconds) =>
        token.WaitHandle.WaitOne(milliseconds);

    /// <summary>Busca la iPad, abre el túnel y transmite; si algo se corta, vuelve a buscar.</summary>
    private void UsbLoop(bool mirror, bool sharp, int quality, int fps, CancellationToken token)
    {
        string? lastStatus = null;

        void Report(bool ok, string text)
        {
            if (token.IsCancellationRequested || text == lastStatus) return;
            lastStatus = text;
            UsbStatus?.Invoke(ok, text);
        }

        while (!token.IsCancellationRequested)
        {
            if (!mirror && VirtualDisplay.FindDeviceName() == null)
            {
                Report(false, "Falta la pantalla virtual: pulsa «Instalar y configurar».");
                if (WaitCancelled(token, 2000)) break;
                continue;
            }

            UsbMuxDevice? device;
            try
            {
                device = UsbMux.ListDevices().FirstOrDefault();
            }
            catch (SocketException)
            {
                Report(false, "Falta iTunes: instala «Apple Mobile Device Support» (iTunes de apple.com) y reinicia.");
                if (WaitCancelled(token, 3000)) break;
                continue;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException)
            {
                Report(false, "El servicio de Apple no responde: " + ex.Message);
                if (WaitCancelled(token, 2000)) break;
                continue;
            }

            if (device == null)
            {
                Report(false, "iPad no detectada. Conecta el cable USB y desbloquea la iPad.");
                if (WaitCancelled(token, 1000)) break;
                continue;
            }

            TcpClient client;
            try
            {
                client = UsbMux.Connect(device.Id, DevicePort);
            }
            catch (UsbMuxException ex) when (ex.Code == 3)
            {
                Report(true, "iPad detectada. Abre la app «Monitor PC» en la iPad.");
                if (WaitCancelled(token, 700)) break;
                continue;
            }
            catch (Exception ex) when (ex is UsbMuxException or IOException or SocketException or InvalidDataException)
            {
                Report(true, "iPad detectada. Reintentando la conexión…");
                if (WaitCancelled(token, 1000)) break;
                continue;
            }

            lock (_gate)
            {
                if (token.IsCancellationRequested)
                {
                    client.Close();
                    break;
                }
                _client = client;
            }

            Report(true, "iPad conectada por USB.");
            Log?.Invoke("iPad conectada por USB.");
            ClientChanged?.Invoke(true, "USB");

            int pause = 500;
            try
            {
                Serve(client, mirror, sharp, quality, fps, token);
            }
            catch (InvalidOperationException ex)
            {
                // Problema de configuración (por ejemplo, la pantalla virtual no está lista).
                Log?.Invoke(ex.Message);
                Report(false, ex.Message);
                pause = 3000;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // La iPad se desconectó o se detuvo la transmisión.
            }
            catch (Exception ex)
            {
                Log?.Invoke("Error: " + ex.Message);
            }
            finally
            {
                try { client.Close(); } catch { }
                lock (_gate)
                {
                    if (ReferenceEquals(_client, client)) _client = null;
                }
            }

            if (!token.IsCancellationRequested)
            {
                Log?.Invoke("iPad desconectada.");
                ClientChanged?.Invoke(false, "USB");
                lastStatus = null;
                if (WaitCancelled(token, pause)) break;
            }
        }
    }

    /// <summary>Deja lista la zona a compartir según el tamaño que pide la iPad.</summary>
    private Surface BuildSurface(Session session, bool mirror, string? virtualDevice)
    {
        var (targetW, targetH) = session.Target;
        Rectangle bounds;

        if (mirror || virtualDevice == null)
        {
            var screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
            bounds = screen.Bounds;
        }
        else
        {
            if (!VirtualDisplay.SetMode(virtualDevice, targetW, targetH, out string message))
                throw new InvalidOperationException(message);
            Log?.Invoke(message);

            var current = VirtualDisplay.GetCurrentBounds(virtualDevice);
            if (current == null)
                throw new InvalidOperationException("La pantalla virtual no está conectada al escritorio de Windows.");
            bounds = current.Value;
        }

        session.Bounds = bounds;
        var surface = new Surface(bounds, targetW, targetH);
        string text = $"{surface.OutW}×{surface.OutH}" + (mirror ? " (copia)" : " (monitor extendido)");
        ModeChanged?.Invoke(text);
        Log?.Invoke($"Enviando {surface.OutW}×{surface.OutH} desde {bounds.Width}×{bounds.Height} px.");
        return surface;
    }

    private void Serve(TcpClient client, bool mirror, bool sharp, int quality, int fps, CancellationToken token)
    {
        string? virtualDevice = null;
        if (!mirror)
        {
            virtualDevice = VirtualDisplay.EnsureReady(out string message);
            if (virtualDevice == null) throw new InvalidOperationException(message);
        }

        var stream = client.GetStream();
        var flow = new Flow();
        var session = new Session(sharp);

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);

        // Hilo que lee lo que manda la iPad: tamaño, toques y confirmaciones de cuadros.
        new Thread(() => ReadFromDevice(stream, session, flow)) { IsBackground = true, Name = "monitor-input" }.Start();

        // La app informa su tamaño nada más conectar; si es una versión antigua, se usa 1024×768.
        if (!session.GotSize.Wait(2500, token))
        {
            Log?.Invoke("La iPad no informó su tamaño; se usa 1024×768.");
            session.SetTarget(1024, 768);
        }

        int appliedVersion = session.Version;
        var surface = BuildSurface(session, mirror, virtualDevice);

        var jpeg = new MemoryStream(512 * 1024);
        var header = new byte[4];
        var frameTime = TimeSpan.FromSeconds(1.0 / fps);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastStats = clock.Elapsed;
        int frames = 0;
        long bytes = 0;

        timeBeginPeriod(1); // temporizador de 1 ms para que las esperas sean exactas
        Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        try
        {
            while (!token.IsCancellationRequested && client.Connected)
            {
                var start = clock.Elapsed;

                // La iPad giró o cambió de tamaño: se ajusta la pantalla y las imágenes de trabajo.
                if (session.Version != appliedVersion)
                {
                    appliedVersion = session.Version;
                    var next = BuildSurface(session, mirror, virtualDevice);
                    surface.Dispose();
                    surface = next;
                    Interlocked.Exchange(ref flow.InFlight, 0);
                }

                // Control de flujo: si ya hay MaxInFlight cuadros sin confirmar, se espera a la iPad
                // en lugar de acumular cuadros viejos (eso sería retraso).
                while (Volatile.Read(ref flow.InFlight) >= MaxInFlight)
                {
                    flow.AckEvent.Reset();
                    if (Volatile.Read(ref flow.InFlight) < MaxInFlight) break;
                    if (!flow.AckEvent.Wait(250, token))
                    {
                        Interlocked.Exchange(ref flow.InFlight, 0); // sin respuesta: se sigue igual
                        break;
                    }
                }

                try
                {
                    surface.GFull.CopyFromScreen(surface.Bounds.Location, Point.Empty, surface.Bounds.Size);
                    DrawCursor(surface.GFull, surface.Bounds);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Pantalla bloqueada o cambio de escritorio: se reintenta en un momento.
                    Thread.Sleep(300);
                    continue;
                }

                Bitmap toSend = surface.Full;
                if (surface.Scaled)
                {
                    surface.GSmall.DrawImage(surface.Full, 0, 0, surface.OutW, surface.OutH);
                    toSend = surface.Small;
                }

                jpeg.SetLength(0);
                toSend.Save(jpeg, codec, encoderParams);

                int length = (int)jpeg.Length;
                header[0] = (byte)(length >> 24);
                header[1] = (byte)(length >> 16);
                header[2] = (byte)(length >> 8);
                header[3] = (byte)length;

                Interlocked.Increment(ref flow.InFlight);
                stream.Write(header, 0, 4);
                stream.Write(jpeg.GetBuffer(), 0, length);

                frames++;
                bytes += length + 4;
                var now = clock.Elapsed;
                if (now - lastStats >= TimeSpan.FromSeconds(1))
                {
                    double seconds = (now - lastStats).TotalSeconds;
                    Stats?.Invoke(frames / seconds, bytes / 1024.0 / seconds);
                    frames = 0;
                    bytes = 0;
                    lastStats = now;
                }

                var elapsed = clock.Elapsed - start;
                if (elapsed < frameTime) Thread.Sleep(frameTime - elapsed);
            }
        }
        finally
        {
            surface.Dispose();
            Thread.CurrentThread.Priority = ThreadPriority.Normal;
            timeEndPeriod(1);
        }
    }

    private static void ReadFromDevice(NetworkStream stream, Session session, Flow flow)
    {
        try
        {
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 256, leaveOpen: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line == "A") // la iPad recibió un cuadro
                {
                    if (Interlocked.Decrement(ref flow.InFlight) < 0) Interlocked.Exchange(ref flow.InFlight, 0);
                    flow.AckEvent.Set();
                    continue;
                }

                var parts = line.Split(' ');

                // "R anchoPx altoPx anchoPuntos altoPuntos": tamaño de la pantalla de la iPad.
                if (parts[0] == "R" && parts.Length == 5)
                {
                    if (int.TryParse(parts[1], out int pxW) && int.TryParse(parts[2], out int pxH) &&
                        int.TryParse(parts[3], out int ptW) && int.TryParse(parts[4], out int ptH))
                    {
                        int w = session.Sharp ? pxW : ptW;
                        int h = session.Sharp ? pxH : ptH;
                        if (w >= 320 && h >= 320 && w <= 8192 && h <= 8192) session.SetTarget(w, h);
                    }
                    continue;
                }

                if (parts.Length != 3) continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var nx)) continue;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ny)) continue;

                var bounds = session.Bounds;
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                int x = bounds.X + (int)Math.Round(Math.Clamp(nx, 0, 1) * (bounds.Width - 1));
                int y = bounds.Y + (int)Math.Round(Math.Clamp(ny, 0, 1) * (bounds.Height - 1));

                switch (parts[0])
                {
                    case "D":
                        SetCursorPos(x, y);
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                        break;
                    case "M":
                        SetCursorPos(x, y);
                        break;
                    case "U":
                        SetCursorPos(x, y);
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // Conexión cerrada.
        }
    }
}
