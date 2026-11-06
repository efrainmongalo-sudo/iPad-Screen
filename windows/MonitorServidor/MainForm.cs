using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MonitorServidor;

/// <summary>Ventana principal: iniciar o detener la transmisión, ajustes y estado de la iPad.</summary>
internal sealed class MainForm : Form
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
    private Label _modeLabel = null!;
    private ComboBox _modeCombo = null!, _detailCombo = null!;
    private NumericUpDown _fpsNum = null!;
    private TrackBar _qualityBar = null!;
    private CheckBox _autoStartCheck = null!;
    private Button _startButton = null!;
    private TextBox _logBox = null!;
    private bool _installing;

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
        _server.ModeChanged += text => Post(() => _modeLabel.Text = "Enviando " + text);

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
        ClientSize = new Size(760, 544);

        try { _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        if (_appIcon != null) Icon = _appIcon;

        // Encabezado
        var header = new Panel { Bounds = new Rectangle(0, 0, 760, 92), BackColor = HeaderBg };
        Controls.Add(header);
        AddLabel(header, "Monitor secundario", 28, 14, 20f, true, TextMain);
        AddLabel(header, "Usa tu iPad como pantalla adicional de Windows, por cable USB", 30, 56, 10f, false, TextDim);

        // Conexión
        var conn = AddCard(108, 116, "CONEXIÓN POR USB");
        _dot = AddLabel(conn, "●", 18, 36, 16f, false, Off);
        _statusLabel = AddLabel(conn, "Detenido", 46, 38, 13f, true, TextMain);
        _statsLabel = AddLabel(conn, "", 470, 34, 10f, false, TextDim);
        _modeLabel = AddLabel(conn, "", 470, 58, 9.5f, false, TextDim);
        _usbLabel = AddLabel(conn, IdleHint, 20, 82, 9.5f, false, TextDim);

        // Ajustes
        var settings = AddCard(236, 160, "AJUSTES");

        AddLabel(settings, "Modo", 20, 34, 9.5f, false, TextDim);
        _modeCombo = AddCombo(settings, 20, 56, 330);
        _modeCombo.Items.Add("Monitor extendido (pantalla adicional)");
        _modeCombo.Items.Add("Duplicar la pantalla principal");

        AddLabel(settings, "Detalle de imagen", 370, 34, 9.5f, false, TextDim);
        _detailCombo = AddCombo(settings, 370, 56, 314);
        _detailCombo.Items.Add("Rápido · tamaño en puntos (recomendado)");
        _detailCombo.Items.Add("Nítido · píxeles reales de la iPad");

        AddLabel(settings, "Cuadros por segundo", 370, 94, 9.5f, false, TextDim);
        _fpsNum = AddNumeric(settings, 370, 118, 100, 10, 60, 5);

        _qualityLabel = AddLabel(settings, "Calidad de imagen", 20, 94, 9.5f, false, TextDim);
        _qualityBar = new TrackBar
        {
            Location = new Point(20, 112),
            Width = 330,
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
            Location = new Point(490, 122),
            ForeColor = TextMain,
            BackColor = Color.Transparent
        };
        settings.Controls.Add(_autoStartCheck);

        // Botón principal
        _startButton = AddButton(this, "Iniciar transmisión", 28, 408, 704, 50, Accent, 13f, true);
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
            Bounds = new Rectangle(28, 468, 704, 66)
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
        _modeCombo.SelectedIndex = _settings.Mirror ? 1 : 0;
        _detailCombo.SelectedIndex = _settings.Sharp ? 1 : 0;
    }

    private void ReadUiToSettings()
    {
        _settings.Mirror = _modeCombo.SelectedIndex == 1;
        _settings.Sharp = _detailCombo.SelectedIndex == 1;
        _settings.Quality = _qualityBar.Value;
        _settings.Fps = (int)_fpsNum.Value;
        _settings.AutoStart = _autoStartCheck.Checked;
    }

    private void UpdateQualityLabel() => _qualityLabel.Text = $"Calidad de imagen: {_qualityBar.Value}%";

    // ---------------------------------------------------------------- Pantalla virtual

    /// <summary>
    /// El instalador ya deja el driver puesto. Solo si por algún motivo falta (por ejemplo, falló
    /// la instalación), se instala aquí solo, sin que el usuario pulse nada.
    /// </summary>
    private void InstallVirtualDisplayThenStart()
    {
        if (_installing) return;
        _installing = true;
        SetRunning(true);
        SetStatus(Warn, "Instalando la pantalla virtual…");
        _usbLabel.Text = "Acepta el permiso de administrador si Windows lo pide.";
        _usbLabel.ForeColor = Warn;
        AppendLog("Falta la pantalla virtual: se instalará automáticamente (puede tardar un minuto)…");

        Task.Run(VirtualDisplay.InstallAndConfigure).ContinueWith(task =>
        {
            var (ok, message) = task.IsFaulted
                ? (false, "Error al instalar: " + task.Exception?.GetBaseException().Message)
                : task.Result;
            Post(() =>
            {
                _installing = false;
                AppendLog(message);
                SetRunning(false);
                if (ok && VirtualDisplay.FindDeviceName() != null)
                {
                    _server.Start(_settings.Mirror, _settings.Sharp, _settings.Quality, _settings.Fps);
                    SetRunning(true);
                }
                else
                {
                    _usbLabel.Text = "No se pudo instalar la pantalla virtual. Usa el modo «Duplicar» o reinstala el programa.";
                    _usbLabel.ForeColor = Warn;
                }
            });
        });
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
        if (!_settings.Mirror && VirtualDisplay.FindDeviceName() == null)
        {
            InstallVirtualDisplayThenStart();
            return;
        }
        _server.Start(_settings.Mirror, _settings.Sharp, _settings.Quality, _settings.Fps);
        SetRunning(true);
    }

    private void SetRunning(bool running)
    {
        _startButton.Text = running ? "Detener transmisión" : "Iniciar transmisión";
        _startButton.BackColor = running ? Danger : Accent;
        _startButton.FlatAppearance.MouseOverBackColor = ControlPaint.Light(_startButton.BackColor, 0.25f);
        _startButton.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(_startButton.BackColor, 0.1f);
        _trayToggle.Text = running ? "Detener transmisión" : "Iniciar transmisión";

        foreach (Control control in new Control[] { _modeCombo, _detailCombo, _fpsNum, _qualityBar })
            control.Enabled = !running;

        _statsLabel.Text = "";
        _modeLabel.Text = "";
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
            _modeLabel.Text = "";
            SetStatus(Warn, "Buscando la iPad por USB…");
        }
    }

    private void AppendLog(string message)
    {
        if (_logBox.TextLength > 20000) _logBox.Clear();
        _logBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
    }
}
