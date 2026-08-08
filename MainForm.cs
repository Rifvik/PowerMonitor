using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HWMonitor;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HWMonitor;

public class MainForm : Form
{
    private WebView2 _webView;
    private HWInfoSensorEngine _engine;
    private CancellationTokenSource _cts;
    private NotifyIcon _trayIcon;
    private bool _minimizeToTray = true;
    private bool _forceExit = false;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    extern static bool DestroyIcon(IntPtr handle);

    public MainForm()
    {
        InitializeComponent();
        InitializeWebView();

        // Start HWInfoSensorEngine
        _engine = new HWInfoSensorEngine
        {
            ComponentBaseOffsetWatts = 10f,
        };
    }

    private void InitializeComponent()
    {
        this.Text = "PowerMonitor — Hardware Telemetry";
        this.Width = 1200;
        this.Height = 800;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = Color.FromArgb(245, 246, 250);

        _webView = new WebView2
        {
            Dock = DockStyle.Fill
        };

        this.Controls.Add(_webView);
        
        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "PowerMonitor",
            Visible = false
        };
        
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Restore Dashboard", null, (s, e) => 
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            _trayIcon.Visible = false;
        });
        contextMenu.Items.Add("Exit PowerMonitor", null, (s, e) => 
        {
            _forceExit = true;
            this.Close();
        });
        _trayIcon.ContextMenuStrip = contextMenu;

        _trayIcon.DoubleClick += (s, e) => 
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            _trayIcon.Visible = false;
        };

        this.FormClosing += (s, e) => 
        {
            if (_minimizeToTray && !_forceExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                _trayIcon.Visible = true;
            }
            else
            {
                _cts?.Cancel();
                _engine?.Dispose();
                _trayIcon?.Dispose();
            }
        };
    }

    private async void InitializeWebView()
    {
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Path.GetTempPath(), "HWMonitorWebView2"));
        await _webView.EnsureCoreWebView2Async(env);
        
        // Remove standard context menu / developer tools if desired
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

        string indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "index.html");
        _webView.CoreWebView2.Navigate(indexPath);

        _webView.CoreWebView2.NavigationCompleted += (s, e) =>
        {
            // Start polling loop when UI is loaded
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => PollingLoop(_cts.Token));
        };

        _webView.CoreWebView2.WebMessageReceived += (s, e) =>
        {
            try
            {
                var msg = JsonDocument.Parse(e.WebMessageAsJson);
                if (msg.RootElement.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "SETTINGS_UPDATE")
                {
                    if (msg.RootElement.TryGetProperty("payload", out var payload))
                    {
                        if (payload.TryGetProperty("minimizeToTray", out var minProp))
                        {
                            _minimizeToTray = minProp.GetBoolean();
                        }
                    }
                }
            }
            catch { }
        };
    }

    private void UpdateTrayIcon(string text)
    {
        if (this.IsDisposed || this.Disposing) return;
        
        Icon oldIcon = _trayIcon.Icon;
        
        using (Bitmap bitmap = new Bitmap(32, 32))
        {
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.FillEllipse(Brushes.Black, 0, 0, 32, 32);
                
                using (Font font = new Font("Segoe UI", 12, FontStyle.Bold))
                {
                    StringFormat sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center
                    };
                    g.DrawString(text, font, Brushes.White, new RectangleF(0, 0, 32, 32), sf);
                }
            }
            IntPtr hIcon = bitmap.GetHicon();
            Icon newIcon = Icon.FromHandle(hIcon);
            
            if (this.InvokeRequired)
            {
                this.Invoke((MethodInvoker)delegate { 
                    if (!this.IsDisposed) _trayIcon.Icon = newIcon; 
                });
            }
            else
            {
                _trayIcon.Icon = newIcon;
            }
            
            if (oldIcon != null && oldIcon != SystemIcons.Application)
            {
                DestroyIcon(oldIcon.Handle);
                oldIcon.Dispose();
            }
        }
    }

    private async Task PollingLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var snapshot = _engine.GetSnapshot();

                var payload = new
                {
                    Timestamp = snapshot.CapturedAtUtc,
                    Cpu = new
                    {
                        Name = snapshot.CpuName,
                        Power = snapshot.CpuPackagePowerWatts ?? 0f,
                        Tag = snapshot.CpuPowerTag,
                        Limit = snapshot.CpuPowerLimitWatts ?? 45f
                    },
                    Gpu = new
                    {
                        Name = snapshot.GpuName,
                        Power = snapshot.GpuBoardPowerWatts ?? 0f,
                        Tag = snapshot.GpuPowerTag,
                        Limit = snapshot.GpuPowerLimitWatts ?? 115f
                    },
                    System = new
                    {
                        TotalPower = snapshot.TotalSystemPowerWatts,
                        IsEstimated = snapshot.IsEstimated,
                        Source = snapshot.PowerSourceTag
                    }
                };

                string json = JsonSerializer.Serialize(payload);

                // Send to UI thread
                if (!this.IsDisposed && !this.Disposing)
                {
                    this.Invoke((MethodInvoker)delegate
                    {
                        _webView.CoreWebView2.PostWebMessageAsJson(json);
                    });
                }
                
                // Update Tray Icon text
                int totalWatts = (int)Math.Round(snapshot.TotalSystemPowerWatts);
                UpdateTrayIcon(totalWatts.ToString());
            }
            catch (Exception ex)
            {
                Console.WriteLine("Polling error: " + ex.Message);
            }

            await Task.Delay(500, token); // 500ms polling rate
        }
    }
}
