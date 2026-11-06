namespace MonitorServidor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\MonitorSecundarioNetnologia", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show(
                "Monitor secundario ya está abierto. Búscalo en el icono junto al reloj de Windows.",
                "Monitor secundario", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        // Con --minimizado arranca oculto, junto al reloj, y empieza a transmitir (lo usa el inicio con Windows).
        bool startHidden = args.Contains("--minimizado");
        Application.Run(new MainForm(startHidden));
    }
}
