using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MonitorServidor;

/// <summary>
/// Motor del monitor secundario (sin interfaz). Detecta por USB iPad, iPhone y Android (varios a la vez),
/// y para cada uno abre una conexión, captura una pantalla de Windows, la comprime a JPEG y la envía.
/// En modo extendido cada dispositivo recibe su propia pantalla virtual (teléfono a la izquierda, tableta
/// a la derecha de la laptop) con la resolución del dispositivo; al desconectarlo, esa pantalla se quita.
/// Los eventos se disparan en hilos de fondo: la interfaz debe pasarlos a su hilo.
/// </summary>
internal sealed class MonitorServer : IDisposable
{
    /// <summary>Puerto en el que escucha la app del dispositivo (debe coincidir con la app).</summary>
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
    public event Action<bool, string>? UsbStatus;          // todo bien, texto para mostrar
    public event Action<int, string>? ClientsChanged;      // cuántos dispositivos transmiten y resumen
    public event Action<string>? Stats;                    // cuadros por segundo y datos por segundo
    public event Action<string>? ModeChanged;              // resolución que recibe cada dispositivo

    private sealed class Flow
    {
        public int InFlight;
        public readonly ManualResetEventSlim AckEvent = new(false);
    }

    /// <summary>La app aceptó la conexión pero se cerró enseguida: no está abierta en el dispositivo.</summary>
    private sealed class AppNotOpenException : IOException
    {
    }

    private enum WorkerState { Searching, AppClosed, Connected }

    /// <summary>Un dispositivo conectado por USB y la conexión que se le mantiene.</summary>
    private sealed class Worker
    {
        public Worker(string key, bool android)
        {
            Key = key;
            IsAndroid = android;
            Label = android ? "Android" : "iOS";
        }

        public string Key { get; }
        public bool IsAndroid { get; }
        public volatile string Label;
        public volatile WorkerState State = WorkerState.Searching;
        public volatile string Mode = "";
        public double Fps;
        public double Kbps;
        public readonly CancellationTokenSource Cts = new();
        public Thread? Thread;
        public volatile TcpClient? Client;
        public volatile bool Done;
    }

    private sealed class Found
    {
        public Found(string key, bool android, string serial, long usbId)
        {
            Key = key;
            IsAndroid = android;
            Serial = serial;
            UsbId = usbId;
        }

        public string Key { get; }
        public bool IsAndroid { get; }
        public string Serial { get; }
        public long UsbId { get; }
    }

    /// <summary>Estado compartido entre el hilo de transmisión y el que lee al dispositivo.</summary>
    private sealed class Session
    {
        public Session(bool sharp) { Sharp = sharp; }

        public bool Sharp { get; }
        public readonly ManualResetEventSlim GotSize = new(false);
        public readonly ManualResetEventSlim Closed = new(false);

        private int _version;
        private int _targetW = 1024, _targetH = 768;
        private string _idiom = "pad";
        private Rectangle _bounds;

        /// <summary>Aumenta cada vez que el dispositivo informa un tamaño nuevo.</summary>
        public int Version => Volatile.Read(ref _version);

        public (int W, int H) Target
        {
            get { lock (this) return (_targetW, _targetH); }
        }

        /// <summary>"phone" o "pad".</summary>
        public string Idiom
        {
            get { lock (this) return _idiom; }
        }

        /// <summary>Zona del escritorio que se está compartiendo (para convertir los toques).</summary>
        public Rectangle Bounds
        {
            get { lock (this) return _bounds; }
            set { lock (this) _bounds = value; }
        }

        public void SetTarget(int w, int h, string idiom)
        {
            lock (this)
            {
                _targetW = w;
                _targetH = h;
                _idiom = idiom;
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
    private readonly Dictionary<string, Worker> _workers = new();
    private CancellationTokenSource? _cts;
    private bool _extended;
    private int _lastClientCount = -1;
    private string _lastClientSummary = "";

    public bool Running
    {
        get { lock (_gate) return _cts != null; }
    }

    /// <param name="mirror">true = duplica la pantalla principal; false = monitor extendido (pantalla virtual por dispositivo).</param>
    /// <param name="sharp">true = resolución real del dispositivo; false = su tamaño en puntos (menos retraso).</param>
    public void Start(bool mirror, bool sharp, int quality, int fps)
    {
        lock (_gate)
        {
            if (_cts != null) return;
            _extended = !mirror;
            _cts = new CancellationTokenSource();
            _lastClientCount = -1;
            _lastClientSummary = "";
            var token = _cts.Token;
            new Thread(() => ManagerLoop(mirror, sharp, quality, fps, token))
            {
                IsBackground = true,
                Name = "monitor-manager"
            }.Start();
        }
        Log?.Invoke("Buscando dispositivos por USB…");
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        List<Worker> workers;
        bool extended;
        lock (_gate)
        {
            cts = _cts;
            extended = _extended;
            workers = _workers.Values.ToList();
            _cts = null;
        }
        if (cts == null) return;

        cts.Cancel();
        foreach (var worker in workers)
        {
            worker.Cts.Cancel();
            try { worker.Client?.Close(); } catch { }
        }

        // Cada hilo quita su pantalla virtual al terminar; se espera para que la laptop quede como estaba.
        foreach (var worker in workers) worker.Thread?.Join(4000);
        lock (_gate) _workers.Clear();
        if (extended) VirtualDisplay.DetachAll();

        Log?.Invoke("Transmisión detenida.");
    }

    public void Dispose() => Stop();

    private static bool WaitCancelled(CancellationToken token, int milliseconds) =>
        token.WaitHandle.WaitOne(milliseconds);

    // ---------------------------------------------------------------- Detección de dispositivos

    private void ManagerLoop(bool mirror, bool sharp, int quality, int fps, CancellationToken token)
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
            var found = new List<Found>();
            var unauthorized = new List<string>();
            string? iosProblem = null;

            try
            {
                foreach (var device in UsbMux.ListDevices())
                    found.Add(new Found("ios:" + device.Serial, false, device.Serial, device.Id));
            }
            catch (SocketException)
            {
                iosProblem = "Para iPad/iPhone falta iTunes (Apple Mobile Device Support).";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Xml.XmlException)
            {
                iosProblem = "El servicio de Apple no responde: " + ex.Message;
            }

            bool adbMissing = AdbClient.FindAdb() == null;
            if (!adbMissing)
            {
                try
                {
                    foreach (var device in AdbClient.ListDevices())
                    {
                        if (device.State == "device")
                            found.Add(new Found("adb:" + device.Serial, true, device.Serial, 0));
                        else if (device.State == "unauthorized")
                            unauthorized.Add(device.Serial);
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Log?.Invoke("adb: " + ex.Message);
                }
            }

            List<Worker> current;
            lock (_gate)
            {
                if (token.IsCancellationRequested) break;

                // Los dispositivos que ya no están conectados se sueltan; sus hilos quitan su pantalla virtual.
                foreach (var key in _workers.Keys.ToList())
                {
                    var worker = _workers[key];
                    if (worker.Done)
                    {
                        _workers.Remove(key);
                    }
                    else if (!found.Any(f => f.Key == key))
                    {
                        worker.Cts.Cancel();
                        try { worker.Client?.Close(); } catch { }
                    }
                }

                foreach (var item in found)
                {
                    if (_workers.ContainsKey(item.Key)) continue;
                    var worker = new Worker(item.Key, item.IsAndroid);
                    _workers[item.Key] = worker;
                    var captured = item;
                    worker.Thread = new Thread(() => WorkerLoop(worker, captured, mirror, sharp, quality, fps, token))
                    {
                        IsBackground = true,
                        Name = "monitor-" + item.Key
                    };
                    worker.Thread.Start();
                }

                current = _workers.Values.Where(w => !w.Done).ToList();
            }

            var parts = new List<string>();
            foreach (var worker in current)
            {
                parts.Add(worker.State switch
                {
                    WorkerState.Connected => worker.Label + " conectado",
                    WorkerState.AppClosed => worker.Label + ": abre la app «Monitor PC»",
                    _ => worker.Label + ": conectando…"
                });
            }
            foreach (var _ in unauthorized)
                parts.Add("Android: acepta «Permitir depuración USB» en el teléfono");

            if (parts.Count > 0)
            {
                Report(true, string.Join(" · ", parts));
            }
            else
            {
                string text = "Conecta tu iPad, iPhone o Android con el cable USB (en Android activa la depuración USB).";
                if (iosProblem != null) text += " " + iosProblem;
                if (adbMissing) text += " Para Android falta adb: reinstala el programa.";
                Report(false, text);
            }

            if (WaitCancelled(token, 1000)) break;
        }
    }

    private void PublishClients()
    {
        List<Worker> connected;
        lock (_gate)
            connected = _workers.Values.Where(w => w.State == WorkerState.Connected && !w.Done).ToList();

        string summary = connected.Count == 0
            ? ""
            : string.Join(" + ", connected.Select(w => w.Label)) + (connected.Count == 1 ? " conectado" : " conectados");

        lock (_gate)
        {
            if (_lastClientCount == connected.Count && _lastClientSummary == summary) return;
            _lastClientCount = connected.Count;
            _lastClientSummary = summary;
        }
        ClientsChanged?.Invoke(connected.Count, summary);
        PublishMode();
    }

    private void PublishMode()
    {
        List<Worker> connected;
        lock (_gate)
            connected = _workers.Values.Where(w => w.State == WorkerState.Connected && !w.Done).ToList();
        ModeChanged?.Invoke(string.Join(" · ", connected.Select(w => $"{w.Label} {w.Mode}")));
    }

    private void PublishStats()
    {
        List<Worker> connected;
        lock (_gate)
            connected = _workers.Values.Where(w => w.State == WorkerState.Connected && !w.Done).ToList();
        if (connected.Count == 0) return;

        double mbps = connected.Sum(w => w.Kbps) / 1024.0;
        Stats?.Invoke(string.Join(" · ", connected.Select(w => $"{w.Fps:0}")) + $" fps · {mbps:0.0} MB/s");
    }

    // ---------------------------------------------------------------- Un dispositivo

    private static string LabelFor(bool android, string idiom) =>
        android ? (idiom == "pad" ? "Tablet Android" : "Android") : (idiom == "phone" ? "iPhone" : "iPad");

    private void WorkerLoop(Worker worker, Found item, bool mirror, bool sharp, int quality, int fps, CancellationToken managerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(managerToken, worker.Cts.Token);
        var token = linked.Token;
        int? adbPort = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    if (item.IsAndroid)
                    {
                        adbPort ??= AdbClient.Forward(item.Serial, DevicePort);
                        client = AdbClient.ConnectLocal(adbPort.Value);
                    }
                    else
                    {
                        client = UsbMux.Connect(item.UsbId, DevicePort);
                    }
                }
                catch (UsbMuxException ex) when (ex.Code == 3)
                {
                    if (worker.State != WorkerState.AppClosed)
                        Log?.Invoke(worker.Label + ": el puerto 5800 del dispositivo rechazó la conexión (la app no está abierta en primer plano o es una versión que no escucha).");
                    worker.State = WorkerState.AppClosed; // la app no está abierta en el dispositivo
                    if (WaitCancelled(token, 700)) break;
                    continue;
                }
                catch (Exception ex) when (ex is UsbMuxException or IOException or SocketException or InvalidDataException)
                {
                    worker.State = WorkerState.Searching;
                    adbPort = null; // por si el reenvío se perdió
                    if (WaitCancelled(token, 1000)) break;
                    continue;
                }

                worker.Client = client;
                int pause = 500;
                try
                {
                    Serve(worker, client, mirror, sharp, quality, fps, token);
                }
                catch (AppNotOpenException)
                {
                    if (worker.State != WorkerState.AppClosed)
                        Log?.Invoke(worker.Label + ": la app aceptó la conexión y la cerró enseguida (bloquea la pantalla o reinstala la app).");
                    worker.State = WorkerState.AppClosed;
                    pause = 700;
                }
                catch (InvalidOperationException ex)
                {
                    // Problema de configuración (por ejemplo, la pantalla virtual no está lista).
                    Log?.Invoke(ex.Message);
                    pause = 3000;
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    // El dispositivo se desconectó o se detuvo la transmisión.
                }
                catch (Exception ex)
                {
                    Log?.Invoke("Error: " + ex.Message);
                }
                finally
                {
                    try { client.Close(); } catch { }
                    worker.Client = null;
                }

                if (worker.State == WorkerState.Connected)
                {
                    Log?.Invoke(worker.Label + " desconectado.");
                    worker.State = WorkerState.Searching;
                    worker.Mode = "";
                    PublishClients();
                }
                else if (worker.State != WorkerState.AppClosed)
                {
                    worker.State = WorkerState.Searching;
                }

                if (WaitCancelled(token, pause)) break;
            }
        }
        finally
        {
            if (adbPort != null) AdbClient.RemoveForward(item.Serial, adbPort.Value);
            worker.State = WorkerState.Searching;
            worker.Done = true;
            if (!managerToken.IsCancellationRequested) PublishClients();
        }
    }

    /// <summary>Deja lista la zona a compartir según el tamaño que pide el dispositivo.</summary>
    private Surface BuildSurface(Worker worker, Session session, bool mirror, string? virtualDevice, VirtualDisplay.Side side)
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
            if (!VirtualDisplay.SetMode(virtualDevice, side, targetW, targetH, out string message))
                throw new InvalidOperationException(message);
            Log?.Invoke($"{worker.Label}: {message}");

            var current = VirtualDisplay.GetCurrentBounds(virtualDevice);
            if (current == null)
                throw new InvalidOperationException("La pantalla virtual no está conectada al escritorio de Windows.");
            bounds = current.Value;
        }

        session.Bounds = bounds;
        var surface = new Surface(bounds, targetW, targetH);
        worker.Mode = $"{surface.OutW}×{surface.OutH}";
        Log?.Invoke($"{worker.Label}: enviando {surface.OutW}×{surface.OutH} desde {bounds.Width}×{bounds.Height} px.");
        return surface;
    }

    private void Serve(Worker worker, TcpClient client, bool mirror, bool sharp, int quality, int fps, CancellationToken token)
    {
        var stream = client.GetStream();
        var flow = new Flow();
        var session = new Session(sharp);

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);

        // Hilo que lee lo que manda el dispositivo: tamaño, toques y confirmaciones de cuadros.
        new Thread(() => ReadFromDevice(stream, session, flow)) { IsBackground = true, Name = "monitor-input" }.Start();

        // La app informa su tamaño nada más conectar; si se cierra enseguida, la app no está abierta.
        int signaled = WaitHandle.WaitAny(new[] { session.GotSize.WaitHandle, session.Closed.WaitHandle, token.WaitHandle }, 2500);
        if (token.IsCancellationRequested) return;
        if (signaled == 1) throw new AppNotOpenException();
        if (signaled == WaitHandle.WaitTimeout)
        {
            Log?.Invoke("El dispositivo no informó su tamaño; se usa 1024×768.");
            session.SetTarget(1024, 768, "pad");
        }

        worker.Label = LabelFor(worker.IsAndroid, session.Idiom);

        // Pantalla virtual propia: teléfono a la izquierda y tableta a la derecha de la laptop.
        string? virtualDevice = null;
        var side = VirtualDisplay.Side.Right;
        if (!mirror)
        {
            var preferred = session.Idiom == "phone" ? VirtualDisplay.Side.Left : VirtualDisplay.Side.Right;
            var claim = VirtualDisplay.Claim(preferred, out string message);
            if (claim == null) throw new InvalidOperationException(message);
            (virtualDevice, side) = claim.Value;
        }

        Surface? surface = null;
        try
        {
            int appliedVersion = session.Version;
            surface = BuildSurface(worker, session, mirror, virtualDevice, side);

            worker.State = WorkerState.Connected;
            Log?.Invoke(worker.Label + " conectado por USB.");
            PublishClients();

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
                while (!token.IsCancellationRequested && client.Connected && !session.Closed.IsSet)
                {
                    var start = clock.Elapsed;

                    // El dispositivo giró o cambió de tamaño: se ajusta la pantalla y las imágenes de trabajo.
                    if (session.Version != appliedVersion)
                    {
                        appliedVersion = session.Version;
                        var next = BuildSurface(worker, session, mirror, virtualDevice, side);
                        surface.Dispose();
                        surface = next;
                        Interlocked.Exchange(ref flow.InFlight, 0);
                        PublishMode();
                    }

                    // Control de flujo: si ya hay MaxInFlight cuadros sin confirmar, se espera al dispositivo
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
                        worker.Fps = frames / seconds;
                        worker.Kbps = bytes / 1024.0 / seconds;
                        PublishStats();
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
                Thread.CurrentThread.Priority = ThreadPriority.Normal;
                timeEndPeriod(1);
            }
        }
        finally
        {
            surface?.Dispose();
            // Al desconectarse el dispositivo, su pantalla virtual se quita y la laptop recupera su distribución.
            if (virtualDevice != null) VirtualDisplay.Release(virtualDevice);
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
                if (line == "A") // el dispositivo recibió un cuadro
                {
                    if (Interlocked.Decrement(ref flow.InFlight) < 0) Interlocked.Exchange(ref flow.InFlight, 0);
                    flow.AckEvent.Set();
                    continue;
                }

                var parts = line.Split(' ');

                // "R anchoPx altoPx anchoPuntos altoPuntos [phone|pad]": tamaño de la pantalla del dispositivo.
                if (parts[0] == "R" && parts.Length >= 5)
                {
                    if (int.TryParse(parts[1], out int pxW) && int.TryParse(parts[2], out int pxH) &&
                        int.TryParse(parts[3], out int ptW) && int.TryParse(parts[4], out int ptH))
                    {
                        string idiom = parts.Length >= 6 && parts[5] == "phone" ? "phone" : "pad";
                        int w = session.Sharp ? pxW : ptW;
                        int h = session.Sharp ? pxH : ptH;
                        // Teléfonos: en puntos serían muy pequeños para Windows; se usa el doble.
                        if (!session.Sharp && Math.Min(ptW, ptH) < 600)
                        {
                            w *= 2;
                            h *= 2;
                        }
                        if (w >= 320 && h >= 320 && w <= 8192 && h <= 8192) session.SetTarget(w, h, idiom);
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
        finally
        {
            session.Closed.Set();
        }
    }
}
