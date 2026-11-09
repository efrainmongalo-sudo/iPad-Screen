using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MonitorServidor;

internal sealed class UsbMuxDevice
{
    public UsbMuxDevice(long id, string serial)
    {
        Id = id;
        Serial = serial;
    }

    public long Id { get; }
    public string Serial { get; }
}

internal sealed class UsbMuxException : Exception
{
    public UsbMuxException(long code) : base($"usbmuxd respondió con el código {code}")
    {
        Code = code;
    }

    /// <summary>2 = dispositivo ya no está; 3 = la app de la iPad no está escuchando en ese puerto.</summary>
    public long Code { get; }
}

/// <summary>
/// Cliente mínimo del servicio usbmuxd que instala iTunes en Windows («Apple Mobile Device Service»,
/// escucha en 127.0.0.1:27015). Permite abrir un túnel TCP por el cable USB hacia un puerto de la iPad.
/// </summary>
internal static class UsbMux
{
    private const int ServicePort = 27015;
    private static int _tag;

    /// <summary>iPads conectadas por USB. Lanza SocketException si el servicio de Apple no está instalado.</summary>
    public static List<UsbMuxDevice> ListDevices()
    {
        using var client = OpenSocket();
        var stream = client.GetStream();
        Send(stream, BuildRequest("ListDevices"));
        var reply = Receive(stream);

        var devices = new List<UsbMuxDevice>();
        if (!reply.TryGetValue("DeviceList", out var listObj) || listObj is not List<object?> list)
            return devices;

        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> device) continue;
            if (!device.TryGetValue("Properties", out var propsObj) || propsObj is not Dictionary<string, object?> props)
                continue;

            if (props.TryGetValue("ConnectionType", out var connectionType) && (connectionType as string) != "USB")
                continue;

            long id = GetLong(device, "DeviceID") ?? GetLong(props, "DeviceID") ?? -1;
            if (id < 0) continue;

            string serial = props.TryGetValue("SerialNumber", out var serialObj) ? serialObj as string ?? "" : "";
            devices.Add(new UsbMuxDevice(id, serial));
        }
        return devices;
    }

    /// <summary>Abre el túnel hacia un puerto de la iPad. El TcpClient devuelto ya es la conexión directa.</summary>
    public static TcpClient Connect(long deviceId, int devicePort)
    {
        var client = OpenSocket();
        try
        {
            var stream = client.GetStream();
            // usbmuxd espera el puerto en orden de red (big-endian).
            int networkPort = ((devicePort & 0xFF) << 8) | ((devicePort >> 8) & 0xFF);
            Send(stream, BuildRequest("Connect", ("DeviceID", deviceId), ("PortNumber", networkPort)));
            var reply = Receive(stream);

            long code = GetLong(reply, "Number") ?? -1;
            if (code != 0) throw new UsbMuxException(code);

            client.ReceiveTimeout = 0; // a partir de aquí es un socket normal, sin límite de espera
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- Protocolo

    private static TcpClient OpenSocket()
    {
        var client = new TcpClient { NoDelay = true, ReceiveTimeout = 4000, SendTimeout = 4000 };
        try
        {
            client.Connect(IPAddress.Loopback, ServicePort);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static string BuildRequest(string messageType, params (string Key, long Value)[] numbers)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\"><dict>");
        sb.Append($"<key>MessageType</key><string>{messageType}</string>");
        sb.Append("<key>ClientVersionString</key><string>MonitorSecundario</string>");
        sb.Append("<key>ProgName</key><string>MonitorSecundario</string>");
        foreach (var (key, value) in numbers)
            sb.Append($"<key>{key}</key><integer>{value.ToString(CultureInfo.InvariantCulture)}</integer>");
        sb.Append("</dict></plist>");
        return sb.ToString();
    }

    private static void Send(NetworkStream stream, string xml)
    {
        var payload = Encoding.UTF8.GetBytes(xml);
        var packet = new byte[16 + payload.Length];
        BitConverter.GetBytes(packet.Length).CopyTo(packet, 0);               // longitud total
        BitConverter.GetBytes(1).CopyTo(packet, 4);                           // versión del protocolo
        BitConverter.GetBytes(8).CopyTo(packet, 8);                           // 8 = mensaje en plist
        BitConverter.GetBytes(Interlocked.Increment(ref _tag)).CopyTo(packet, 12);
        payload.CopyTo(packet, 16);
        stream.Write(packet, 0, packet.Length);
    }

    private static Dictionary<string, object?> Receive(NetworkStream stream)
    {
        var header = ReadExact(stream, 16);
        int length = BitConverter.ToInt32(header, 0);
        if (length < 16 || length > 4 * 1024 * 1024)
            throw new InvalidDataException("Respuesta inválida de usbmuxd.");

        var body = ReadExact(stream, length - 16);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(Encoding.UTF8.GetString(body)), settings);
        var doc = XDocument.Load(reader);
        var dict = doc.Root?.Element("dict") ?? throw new InvalidDataException("Respuesta de usbmuxd sin datos.");
        return ParseDict(dict);
    }

    private static byte[] ReadExact(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n == 0) throw new IOException("usbmuxd cerró la conexión.");
            read += n;
        }
        return buffer;
    }

    // ---------------------------------------------------------------- Plist

    private static object? ParseValue(XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "dict": return ParseDict(element);
            case "array": return element.Elements().Select(ParseValue).ToList();
            case "integer": return long.Parse(element.Value, CultureInfo.InvariantCulture);
            case "true": return true;
            case "false": return false;
            default: return element.Value;
        }
    }

    private static Dictionary<string, object?> ParseDict(XElement element)
    {
        var result = new Dictionary<string, object?>();
        var items = element.Elements().ToList();
        for (int i = 0; i + 1 < items.Count; i += 2)
        {
            if (items[i].Name.LocalName == "key")
                result[items[i].Value] = ParseValue(items[i + 1]);
        }
        return result;
    }

    private static long? GetLong(Dictionary<string, object?> dict, string key)
    {
        if (dict.TryGetValue(key, out var value) && value is long number) return number;
        return null;
    }
}
