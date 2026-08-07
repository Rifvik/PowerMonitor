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
        
        this.FormClosing += (s, e) => 
        {
            _cts?.Cancel();
            _engine?.Dispose();
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
            }
            catch (Exception ex)
            {
                Console.WriteLine("Polling error: " + ex.Message);
            }

            await Task.Delay(500, token); // 500ms polling rate
        }
    }
}
