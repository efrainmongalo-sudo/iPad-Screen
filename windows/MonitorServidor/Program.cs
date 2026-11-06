using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MonitorServidor;

/// <summary>
/// Servidor de monitor secundario (versión 1, captura con GDI).
/// Captura una pantalla de Windows, la comprime a JPEG y la envía por TCP a la app de la iPad.
/// Recibe de vuelta los toques y los convierte en clics del ratón.
///
/// Uso: MonitorServidor.exe [--port 5800] [--screen 0] [--maxwidth 1280] [--quality 70] [--fps 30] [--list]
/// </summary>
internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private static int Main(string[] args)
    {
        SetProcessDPIAware(); // para trabajar con píxeles reales y no escalados

        int port = GetInt(args, "--port", 5800);
        int screenIndex = GetInt(args, "--screen", 0);
        int maxWidth = GetInt(args, "--maxwidth", 1280);
        int quality = Math.Clamp(GetInt(args, "--quality", 70), 10, 100);
        int fps = Math.Clamp(GetInt(args, "--fps", 30), 1, 60);

        var screens = Screen.AllScreens;
        Console.WriteLine("Pantallas detectadas:");
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            Console.WriteLine($"  [{i}] {screens[i].DeviceName}  {b.Width}x{b.Height}  en ({b.X},{b.Y})" +
                              (screens[i].Primary ? "  (principal)" : ""));
        }
        if (args.Contains("--list")) return 0;

        if (screenIndex < 0 || screenIndex >= screens.Length)
        {
            Console.WriteLine($"No existe la pantalla {screenIndex}. Usa --screen con un número de la lista.");
            return 1;
        }

        var jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine();
        Console.WriteLine($"Compartiendo la pantalla [{screenIndex}] en el puerto {port}.");
        Console.WriteLine("Direcciones IP de esta PC (escribe una en la app de la iPad):");
        foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList
                     .Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            Console.WriteLine($"  {ip}");
        Console.WriteLine("Esperando conexión de la iPad… (Ctrl+C para salir)");

        while (true)
        {
            using var client = listener.AcceptTcpClient();
            client.NoDelay = true;
            Console.WriteLine($"{DateTime.Now:T}  iPad conectada: {client.Client.RemoteEndPoint}");
            try
            {
                Serve(client, Screen.AllScreens[screenIndex], maxWidth, fps, jpegCodec, encoderParams);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                // La iPad se desconectó; volvemos a esperar.
            }
            Console.WriteLine($"{DateTime.Now:T}  iPad desconectada.");
        }
    }

    private static void Serve(TcpClient client, Screen screen, int maxWidth, int fps,
                              ImageCodecInfo jpegCodec, EncoderParameters encoderParams)
    {
        var stream = client.GetStream();
        var bounds = screen.Bounds;

        // Hilo que lee los toques de la iPad.
        var touchThread = new Thread(() => ReadTouches(stream, bounds)) { IsBackground = true };
        touchThread.Start();

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

        while (client.Connected)
        {
            var start = clock.Elapsed;

            gFull.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            Bitmap toSend = full;
            if (outW != bounds.Width)
            {
                gSmall.DrawImage(full, 0, 0, outW, outH);
                toSend = small;
            }

            jpeg.SetLength(0);
            toSend.Save(jpeg, jpegCodec, encoderParams);

            int length = (int)jpeg.Length;
            header[0] = (byte)(length >> 24);
            header[1] = (byte)(length >> 16);
            header[2] = (byte)(length >> 8);
            header[3] = (byte)length;
            stream.Write(header, 0, 4);
            stream.Write(jpeg.GetBuffer(), 0, length);

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

    private static int GetInt(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
    }
}
