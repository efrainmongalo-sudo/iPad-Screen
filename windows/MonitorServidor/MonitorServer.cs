using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MonitorServidor;

/// <summary>
/// Motor del monitor secundario (sin interfaz). Busca la iPad por USB, abre un túnel hacia la app,
/// captura una pantalla de Windows, la comprime a JPEG y la envía. Recibe los toques y los convierte
/// en clics del ratón. Los eventos se disparan en hilos de fondo: la interfaz debe pasarlos a su hilo.
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

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    public event Action<string>? Log;
    public event Action<bool, string>? UsbStatus;       // todo bien, texto para mostrar
    public event Action<bool, string>? ClientChanged;   // conectada, descripción
    public event Action<double, double>? Stats;         // cuadros por segundo, KB por segundo

    private sealed class Flow
    {
        public int InFlight;
        public readonly ManualResetEventSlim AckEvent = new(false);
    }

    private readonly object _gate = new();
    private TcpClient? _client;
    private CancellationTokenSource? _cts;

    public bool Running
    {
        get { lock (_gate) return _cts != null; }
    }

    public void Start(string screenDeviceName, int maxWidth, int quality, int fps)
    {
        lock (_gate)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            new Thread(() => UsbLoop(screenDeviceName, maxWidth, quality, fps, token))
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
    private void UsbLoop(string screenName, int maxWidth, int quality, int fps, CancellationToken token)
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

            try
            {
                Serve(client, screenName, maxWidth, quality, fps, token);
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
                if (WaitCancelled(token, 500)) break;
            }
        }
    }

    private void Serve(TcpClient client, string screenName, int maxWidth, int quality, int fps, CancellationToken token)
    {
        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == screenName)
                     ?? Screen.PrimaryScreen
                     ?? Screen.AllScreens[0];
        var bounds = screen.Bounds;
        Log?.Invoke($"Compartiendo {bounds.Width}×{bounds.Height} px.");

        var stream = client.GetStream();
        var flow = new Flow();

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);

        // Hilo que lee lo que manda la iPad: toques y confirmaciones de cuadros.
        new Thread(() => ReadFromDevice(stream, bounds, flow)) { IsBackground = true, Name = "monitor-input" }.Start();

        double scale = bounds.Width > maxWidth ? (double)maxWidth / bounds.Width : 1.0;
        int outW = Math.Max(2, (int)(bounds.Width * scale) & ~1);
        int outH = Math.Max(2, (int)(bounds.Height * scale) & ~1);

        using var full = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var small = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);
        using var gFull = Graphics.FromImage(full);
        using var gSmall = Graphics.FromImage(small);
        gSmall.InterpolationMode = InterpolationMode.Bilinear;
        gSmall.CompositingQuality = CompositingQuality.HighSpeed;

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
                    gFull.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Pantalla bloqueada o cambio de escritorio: se reintenta en un momento.
                    Thread.Sleep(300);
                    continue;
                }

                Bitmap toSend = full;
                if (outW != bounds.Width)
                {
                    gSmall.DrawImage(full, 0, 0, outW, outH);
                    toSend = small;
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
            Thread.CurrentThread.Priority = ThreadPriority.Normal;
            timeEndPeriod(1);
        }
    }

    private static void ReadFromDevice(NetworkStream stream, Rectangle bounds, Flow flow)
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
                if (parts.Length != 3) continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var nx)) continue;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ny)) continue;

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
