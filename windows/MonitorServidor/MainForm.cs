using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MonitorServidor;

/// <summary>Ventana principal: iniciar o detener la transmisión, ajustes y estado de la iPad.</summary>
internal sealed class MainForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class ScreenItem
    {
        public ScreenItem(string deviceName, string label) { DeviceName = deviceName; Label = label; }
        public string DeviceName { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }

    private sealed class ResItem
    {
        public ResItem(int width, string label) { Width = width; Label = label; }
        public int Width { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }

    private const string IdleHint = "Pulsa «Iniciar transmisión» y conecta la iPad con el cable USB.";

    // Paleta
    private static readonly Color Bg = Color.FromArgb(20, 24, 31);
    private static readonly Color HeaderBg = Color.FromArgb(14, 18, 24);
    private static readonly Color Card = Color.FromArgb(27, 33, 43);
    private static readonly Color Field = Color.FromArgb(38, 46, 59);
    private static readonly Color TextMain = Color.FromArgb(235, 240, 247);
    private static readonly Color TextDim = Color.FromArgb(139, 151, 168);
    private static readonly Color Accent = Color.FromArgb(31, 125, 230);
    private static readonly Color Ok = Color.FromArgb(52, 199, 120);
    private static readonly Color Warn = Color.FromArgb(240, 180, 60);
    private static readonly Color Off = Color.FromArgb(120, 130, 145);
    private static readonly Color Danger = Color.FromArgb(214, 69, 65);

    private readonly MonitorServer _server = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly System.Windows.Forms.Timer _pump = new() { Interval = 100 };
    private readonly ToolTip _tip = new();

    private Icon? _appIcon;
    private NotifyIcon _tray = null!;
    private ToolStripMenuItem _trayToggle = null!;

    private Label _dot = null!, _statusLabel = null!, _statsLabel = null!, _usbLabel = null!, _qualityLabel = null!;
    private ComboBox _screenCombo = null!, _resCombo = null!;
    private NumericUpDown _fpsNum = null!;
    private TrackBar _qualityBar = null!;
    private CheckBox _autoStartCheck = null!;
    private Button _startButton = null!, _refreshScreens = null!;
    private TextBox _logBox = null!;

    private bool _allowShow = true;
    private bool _exiting;

    public MainForm(bool startHidden)
    {
        _allowShow = !startHidden;

        BuildUi();
        BuildTray();
        LoadSettingsIntoUi();

        _server.Log += message => Post(() => AppendLog(message));
        _server.UsbStatus += (ok, text) => Post(() => ShowUsbStatus(ok, text));
        _server.ClientChanged += (connected, _) => Post(() => OnClientChanged(connected));
        _server.Stats += (fps, kbps) => Post(() => _statsLabel.Text = $"{fps:0} fps · {kbps:0} KB/s");

        _pump.Tick += (_, _) =>
        {
            while (_queue.TryDequeue(out var action)) action();
        };
        _pump.Start();

        AppendLog("Listo. Pulsa «Iniciar transmisión» y conecta la iPad con el cable USB.");
        if (startHidden || _settings.AutoStart) StartServer();
    }

    // Permite arrancar sin mostrar la ventana (solo icono junto al reloj).
    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(_allowShow && value);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int dark = 1; // barra de título oscura (Windows 10 20H1 en adelante)
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        }
        catch
        {
            // Versiones antiguas de Windows: se queda la barra clara.
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && _server.Running)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(2500, "Monitor secundario",
                "Sigue transmitiendo. Ábrelo desde el icono junto al reloj.", ToolTipIcon.Info);
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        ReadUiToSettings();
        _settings.Save();
        _pump.Stop();
        _server.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }

    // ---------------------------------------------------------------- Interfaz

    private void Post(Action action) => _queue.Enqueue(action);

    private Label AddLabel(Control parent, string text, int x, int y, float size, bool bold, Color color)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Location = new Point(x, y),
            ForeColor = color,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
        };
        parent.Controls.Add(label);
        return label;
    }

    private Panel AddCard(int y, int height, string title)
    {
        var panel = new Panel { Bounds = new Rectangle(28, y, 704, height), BackColor = Card };
        Controls.Add(panel);
        AddLabel(panel, title, 20, 12, 8.5f, true, TextDim);
        return panel;
    }

    private Button AddButton(Control parent, string text, int x, int y, int w, int h, Color back, float size, bool bold)
    {
        var button = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(w, h),
            FlatStyle = FlatStyle.Flat,
            BackColor = back,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
            Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(back, 0.25f);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(back, 0.1f);
        parent.Controls.Add(button);
        return button;
    }

    private ComboBox AddCombo(Control parent, int x, int y, int w)
    {
        var combo = new ComboBox
        {
            Location = new Point(x, y),
            Width = w,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Field,
            ForeColor = TextMain
        };
        parent.Controls.Add(combo);
        return combo;
    }

    private NumericUpDown AddNumeric(Control parent, int x, int y, int w, int min, int max, int increment)
    {
        var numeric = new NumericUpDown
        {
            Location = new Point(x, y),
            Width = w,
            Minimum = min,
            Maximum = max,
            Increment = increment,
            BackColor = Field,
            ForeColor = TextMain,
            BorderStyle = BorderStyle.FixedSingle
        };
        parent.Controls.Add(numeric);
        return numeric;
    }

    private void BuildUi()
    {
        Text = "Monitor secundario";
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Bg;
        ForeColor = TextMain;
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(760, 646);

        try { _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        if (_appIcon != null) Icon = _appIcon;

        // Encabezado
        var header = new Panel { Bounds = new Rectangle(0, 0, 760, 92), BackColor = HeaderBg };
        Controls.Add(header);
        AddLabel(header, "Monitor secundario", 28, 14, 20f, true, TextMain);
        AddLabel(header, "Usa tu iPad como segunda pantalla de Windows, por cable USB", 30, 56, 10f, false, TextDim);

        // Conexión
        var conn = AddCard(108, 120, "CONEXIÓN POR USB");
        _dot = AddLabel(conn, "●", 18, 36, 16f, false, Off);
        _statusLabel = AddLabel(conn, "Detenido", 46, 38, 13f, true, TextMain);
        _statsLabel = AddLabel(conn, "", 470, 44, 10f, false, TextDim);
        _usbLabel = AddLabel(conn, IdleHint, 20, 82, 9.5f, false, TextDim);

        // Ajustes
        var settings = AddCard(240, 232, "AJUSTES");

        AddLabel(settings, "Pantalla que se comparte", 20, 38, 9.5f, false, TextDim);
        _screenCombo = AddCombo(settings, 20, 62, 330);
        _refreshScreens = AddButton(settings, "↻", 358, 60, 36, 30, Field, 11f, true);
        _tip.SetToolTip(_refreshScreens, "Actualizar la lista de pantallas (útil después de instalar un driver de pantalla virtual)");
        _refreshScreens.Click += (_, _) =>
        {
            RefreshScreens(null);
            AppendLog($"Pantallas detectadas: {_screenCombo.Items.Count}.");
        };

        AddLabel(settings, "Cuadros por segundo", 430, 38, 9.5f, false, TextDim);
        _fpsNum = AddNumeric(settings, 430, 62, 140, 10, 60, 5);

        AddLabel(settings, "Resolución máxima enviada", 20, 102, 9.5f, false, TextDim);
        _resCombo = AddCombo(settings, 20, 126, 372);
        _resCombo.Items.Add(new ResItem(960, "960 px de ancho · menor retraso"));
        _resCombo.Items.Add(new ResItem(1280, "1280 px de ancho · recomendado"));
        _resCombo.Items.Add(new ResItem(1600, "1600 px de ancho"));
        _resCombo.Items.Add(new ResItem(1920, "1920 px de ancho · más nítido"));
        _resCombo.Items.Add(new ResItem(int.MaxValue, "Resolución original de la pantalla"));

        _qualityLabel = AddLabel(settings, "Calidad de imagen", 20, 166, 9.5f, false, TextDim);
        _qualityBar = new TrackBar
        {
            Location = new Point(20, 184),
            Width = 372,
            Minimum = 30,
            Maximum = 95,
            SmallChange = 5,
            LargeChange = 10,
            TickStyle = TickStyle.None,
            BackColor = Card
        };
        _qualityBar.Scroll += (_, _) => UpdateQualityLabel();
        settings.Controls.Add(_qualityBar);

        _autoStartCheck = new CheckBox
        {
            Text = "Iniciar al abrir el programa",
            AutoSize = true,
            Location = new Point(430, 130),
            ForeColor = TextMain,
            BackColor = Color.Transparent
        };
        settings.Controls.Add(_autoStartCheck);

        // Botón principal
        _startButton = AddButton(this, "Iniciar transmisión", 28, 486, 704, 52, Accent, 13f, true);
        _startButton.Click += (_, _) => ToggleServer();

        // Registro
        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = HeaderBg,
            ForeColor = TextDim,
            Font = new Font("Consolas", 9f),
            Bounds = new Rectangle(28, 552, 704, 82)
        };
        Controls.Add(_logBox);
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Abrir ventana", null, (_, _) => ShowWindow()));
        _trayToggle = new ToolStripMenuItem("Iniciar transmisión", null, (_, _) => ToggleServer());
        menu.Items.Add(_trayToggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Salir", null, (_, _) => ExitApp()));

        _tray = new NotifyIcon
        {
            Icon = _appIcon ?? SystemIcons.Application,
            Text = "Monitor secundario",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        _allowShow = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        _server.Stop();
        _tray.Visible = false;
        Application.Exit();
    }

    // ---------------------------------------------------------------- Ajustes

    private void LoadSettingsIntoUi()
    {
        _fpsNum.Value = Math.Clamp(_settings.Fps, 10, 60);
        _qualityBar.Value = Math.Clamp(_settings.Quality, 30, 95);
        UpdateQualityLabel();
        _autoStartCheck.Checked = _settings.AutoStart;

        int resIndex = 1;
        for (int i = 0; i < _resCombo.Items.Count; i++)
        {
            if (_resCombo.Items[i] is ResItem item && item.Width == _settings.MaxWidth) resIndex = i;
        }
        _resCombo.SelectedIndex = resIndex;

        RefreshScreens(_settings.ScreenName);
    }

    private void ReadUiToSettings()
    {
        _settings.ScreenName = (_screenCombo.SelectedItem as ScreenItem)?.DeviceName ?? "";
        _settings.MaxWidth = (_resCombo.SelectedItem as ResItem)?.Width ?? 1280;
        _settings.Quality = _qualityBar.Value;
        _settings.Fps = (int)_fpsNum.Value;
        _settings.AutoStart = _autoStartCheck.Checked;
    }

    private void UpdateQualityLabel() => _qualityLabel.Text = $"Calidad de imagen: {_qualityBar.Value}%";

    private void RefreshScreens(string? preferredDevice)
    {
        string? current = (_screenCombo.SelectedItem as ScreenItem)?.DeviceName ?? preferredDevice;
        _screenCombo.Items.Clear();

        var screens = Screen.AllScreens;
        int select = 0;
        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            string label = $"Pantalla {i + 1} · {screen.Bounds.Width}×{screen.Bounds.Height}" +
                           (screen.Primary ? " (principal)" : "");
            _screenCombo.Items.Add(new ScreenItem(screen.DeviceName, label));
            if (screen.DeviceName == current) select = i;
        }
        _screenCombo.SelectedIndex = select;
    }

    // ---------------------------------------------------------------- Servidor

    private void ToggleServer()
    {
        if (_server.Running)
        {
            _server.Stop();
            SetRunning(false);
        }
        else
        {
            StartServer();
        }
    }

    private void StartServer()
    {
        ReadUiToSettings();
        _settings.Save();
        _server.Start(_settings.ScreenName, _settings.MaxWidth, _settings.Quality, _settings.Fps);
        SetRunning(true);
    }

    private void SetRunning(bool running)
    {
        _startButton.Text = running ? "Detener transmisión" : "Iniciar transmisión";
        _startButton.BackColor = running ? Danger : Accent;
        _startButton.FlatAppearance.MouseOverBackColor = ControlPaint.Light(_startButton.BackColor, 0.25f);
        _startButton.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(_startButton.BackColor, 0.1f);
        _trayToggle.Text = running ? "Detener transmisión" : "Iniciar transmisión";

        foreach (Control control in new Control[] { _screenCombo, _refreshScreens, _resCombo, _fpsNum, _qualityBar })
            control.Enabled = !running;

        _statsLabel.Text = "";
        if (running)
        {
            SetStatus(Warn, "Buscando la iPad por USB…");
        }
        else
        {
            SetStatus(Off, "Detenido");
            _usbLabel.Text = IdleHint;
            _usbLabel.ForeColor = TextDim;
        }
    }

    private void SetStatus(Color color, string text)
    {
        _dot.ForeColor = color;
        _statusLabel.Text = text;
        string tip = "Monitor secundario · " + text;
        _tray.Text = tip.Length > 63 ? tip[..63] : tip; // NotifyIcon admite 63 caracteres
    }

    private void ShowUsbStatus(bool ok, string text)
    {
        if (!_server.Running) return;
        _usbLabel.Text = text;
        _usbLabel.ForeColor = ok ? Ok : Warn;
    }

    private void OnClientChanged(bool connected)
    {
        if (!_server.Running) return;

        if (connected)
        {
            SetStatus(Ok, "iPad conectada por USB");
            if (!Visible) _tray.ShowBalloonTip(2000, "Monitor secundario", "iPad conectada", ToolTipIcon.Info);
        }
        else
        {
            _statsLabel.Text = "";
            SetStatus(Warn, "Buscando la iPad por USB…");
        }
    }

    private void AppendLog(string message)
    {
        if (_logBox.TextLength > 20000) _logBox.Clear();
        _logBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
    }
}
