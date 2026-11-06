using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MonitorServidor;

/// <summary>
/// Motor del monitor secundario (sin interfaz). Captura una pantalla de Windows, la comprime a JPEG
/// y la envía por TCP a la app de la iPad. Recibe los toques y los convierte en clics del ratón.
/// Los eventos se disparan en hilos de fondo: la interfaz debe pasarlos a su hilo.
/// </summary>
internal sealed class MonitorServer : IDisposable
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    public event Action<string>? Log;
    public event Action<bool, string>? ClientChanged;   // conectada, dirección de la iPad
    public event Action<double, double>? Stats;         // cuadros por segundo, KB por segundo

    private readonly object _gate = new();
    private TcpListener? _listener;
    private TcpClient? _client;
    private CancellationTokenSource? _cts;

    public bool Running
    {
        get { lock (_gate) return _listener != null; }
    }

    /// <summary>Abre el puerto y empieza a esperar a la iPad. Lanza SocketException si el puerto está ocupado.</summary>
    public void Start(int port, string screenDeviceName, int maxWidth, int quality, int fps)
    {
        lock (_gate)
        {
            if (_listener != null) return;

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            var thread = new Thread(() => AcceptLoop(listener, screenDeviceName, maxWidth, quality, fps, token))
            {
                IsBackground = true,
                Name = "monitor-accept"
            };
            thread.Start();
        }
        Log?.Invoke($"Servidor iniciado en el puerto {port}.");
    }

    public void Stop()
    {
        TcpListener? listener;
        TcpClient? client;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            listener = _listener;
            client = _client;
            cts = _cts;
            _listener = null;
            _client = null;
            _cts = null;
        }

        cts?.Cancel();
        try { listener?.Stop(); } catch { }
        try { client?.Close(); } catch { }
        if (listener != null) Log?.Invoke("Servidor detenido.");
    }

    public void Dispose() => Stop();

    private void AcceptLoop(TcpListener listener, string screenName, int maxWidth, int quality, int fps, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                break; // el servidor se detuvo
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

            client.NoDelay = true;
            string remote = "iPad";
            try { remote = client.Client.RemoteEndPoint?.ToString() ?? "iPad"; } catch { }

            Log?.Invoke($"iPad conectada: {remote}");
            ClientChanged?.Invoke(true, remote);

            try
            {
                Serve(client, screenName, maxWidth, quality, fps, token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // La iPad se desconectó.
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
                ClientChanged?.Invoke(false, remote);
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

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);

        // Hilo que lee los toques de la iPad.
        new Thread(() => ReadTouches(stream, bounds)) { IsBackground = true, Name = "monitor-touch" }.Start();

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

        while (!token.IsCancellationRequested && client.Connected)
        {
            var start = clock.Elapsed;

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

    private static void ReadTouches(NetworkStream stream, Rectangle bounds)
    {
        try
        {
            using var reader = new StreamReader(stream, System.Text.Encoding.ASCII, false, 256, leaveOpen: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
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
