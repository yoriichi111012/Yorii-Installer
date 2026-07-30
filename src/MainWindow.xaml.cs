using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MicaWPF.Controls;
using Windows.Management.Deployment;

namespace YoriiInstaller
{
    public partial class MainWindow : MicaWindow
    {
        private CancellationTokenSource _cts;
        private bool _cancelled;
        private bool _installing;

        private const string RepoOwner = "yoriichi111012";
        private const string Repository = "Yorii-Launcher";
        private const string CertUrl = "https://raw.githubusercontent.com/yoriichi111012/Yorii-Launcher/main/Yorii-Launcher-Certificate.cer";
        private const string UserAgentValue = "Yorii Launcher Installer";

        public MainWindow()
        {
            InitializeComponent();
        }

        private static void SetStepState(MicaWPF.Controls.ProgressRing ring, System.Windows.Controls.TextBlock text, string state)
        {
            switch (state)
            {
                case "Pending":
                    ring.Visibility = Visibility.Hidden;
                    ring.IsIndeterminate = false;
                    text.Foreground = FindBrush("#9E9E9E");
                    break;
                case "Active":
                    ring.Visibility = Visibility.Visible;
                    ring.IsIndeterminate = true;
                    text.Foreground = FindBrush("#ea9c80");
                    break;
                case "Completed":
                    ring.Visibility = Visibility.Hidden;
                    ring.IsIndeterminate = false;
                    text.Foreground = FindBrush("#79C979");
                    break;
                case "Error":
                    ring.Visibility = Visibility.Hidden;
                    ring.IsIndeterminate = false;
                    text.Foreground = FindBrush("#F44747");
                    break;
            }
        }

        private async void InstallBtn_Click(object sender, RoutedEventArgs e)
        {
            _cts = new CancellationTokenSource();
            _cancelled = false;
            _installing = true;
            InstallBtn.Visibility = Visibility.Hidden;
            StatusText.Text = "Preparing installation...";
            StatusText.Foreground = FindBrush("#9E9E9E");
            ProgressBar.Value = 0;
            ProgressBar.IsIndeterminate = true;
            ProgressBar.Visibility = Visibility.Visible;

            SetStepState(CertDownloadRing, DownloadCertificateText, "Pending");
            SetStepState(CertInstallRing, InstallCertificateText, "Pending");
            SetStepState(PackageDownloadRing, FetchLatestReleaseText, "Pending");
            SetStepState(PackageInstallRing, LatestReleaseText, "Pending");

            bool success = false;
            var token = _cts.Token;

            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentValue);
                http.Timeout = TimeSpan.FromMinutes(10);

                // 1. Download certificate
                UpdateStatus("Downloading signing certificate...");
                UI(() => SetStepState(CertDownloadRing, DownloadCertificateText, "Active"));

                string certPath = Path.Combine(Path.GetTempPath(), "Yorii Launcher.cer");
                using (var resp = await http.GetAsync(CertUrl, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    resp.EnsureSuccessStatusCode();
                    using var src = await resp.Content.ReadAsStreamAsync();
                    using var dst = File.Create(certPath);
                    await src.CopyToAsync(dst, 81920, token);
                }

                // 3. Verify & install certificate
                UpdateStatus("Verifying signing certificate...");
                UI(() =>
                {
                    SetStepState(CertDownloadRing, DownloadCertificateText, "Completed");
                    SetStepState(CertInstallRing, InstallCertificateText, "Active");
                });

                using (var cert = new X509Certificate2(certPath))
                {
                    if (cert.NotAfter < DateTime.Now)
                        throw new InstallException($"Signing certificate expired on {cert.NotAfter}.");

                    string thumbprint = cert.Thumbprint;
                    bool alreadyInstalled = false;
                    using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                    {
                        store.Open(OpenFlags.ReadOnly);
                        alreadyInstalled = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).Count > 0;
                    }

                    if (!alreadyInstalled)
                    {
                        UpdateStatus("Installing signing certificate...");
                        using (var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                        {
                            store.Open(OpenFlags.ReadWrite);
                            store.Add(cert);
                        }
                    }

                    UI(() => SetStepState(CertInstallRing, InstallCertificateText, "Completed"));
                }
                try { File.Delete(certPath); } catch { }

                // 4. Detect system architecture
                UpdateStatus("Detecting system architecture...");
                string arch = Environment.Is64BitOperatingSystem
                    ? (Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") == "ARM64" ? "arm64" : "x64")
                    : throw new InstallException("Yorii Launcher requires a 64-bit version of Windows.");

                // 5. Download package with progress
                string pkgName = $"Yorii.Launcher_{arch}.msix";
                string dlUrl = $"https://github.com/{RepoOwner}/{Repository}/releases/latest/download/{pkgName}";
                string dlPath = Path.Combine(Path.GetTempPath(), pkgName);

                UpdateStatus("Downloading package...");
                UI(() => SetStepState(PackageDownloadRing, FetchLatestReleaseText, "Active"));

                using (var resp = await http.GetAsync(dlUrl, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    resp.EnsureSuccessStatusCode();
                    long total = resp.Content.Headers.ContentLength ?? -1;
                    using var src = await resp.Content.ReadAsStreamAsync();
                    using var dst = File.Create(dlPath);
                    var buf = new byte[65536];
                    long read = 0;
                    var sw = Stopwatch.StartNew();
                    var lastUI = Stopwatch.StartNew();
                    int nr;
                    while ((nr = await src.ReadAsync(buf, 0, buf.Length, token)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, nr, token);
                        read += nr;
                        if (lastUI.ElapsedMilliseconds >= 150 || read == total)
                        {
                            var elapsed = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                            var speed = read / elapsed;
                            var speedMB = speed / (1024 * 1024);
                            var doneMB = read / (1024.0 * 1024.0);
                            if (total > 0)
                            {
                                var totalMB = total / (1024.0 * 1024.0);
                                var pct = Math.Min(100, (int)(read * 100 / total));
                                var etaSec = (int)((total - read) / Math.Max(speed, 1));
                                var eta = etaSec >= 60 ? $"{etaSec / 60}m {etaSec % 60}s" : $"{etaSec}s";
                                var txt = $"{doneMB:N1} MB / {totalMB:N1} MB - {speedMB:N1} MB/s - ETA: {eta}";
                                UI(() =>
                                {
                                    ProgressBar.IsIndeterminate = false;
                                    ProgressBar.Value = pct;
                                    StatusText.Text = txt;
                                });
                            }
                            lastUI.Restart();
                        }
                    }
                }

                UI(() =>
                {
                    ProgressBar.IsIndeterminate = true;
                    SetStepState(PackageDownloadRing, FetchLatestReleaseText, "Completed");
                });

                // 6. Install MSIX via PackageManager
                UpdateStatus("Installing Yorii Launcher...");
                UI(() => SetStepState(PackageInstallRing, LatestReleaseText, "Active"));

                var pm = new PackageManager();
                var result = await pm.AddPackageAsync(
                    new Uri(dlPath),
                    null,
                    DeploymentOptions.ForceUpdateFromAnyVersion | DeploymentOptions.ForceApplicationShutdown);

                if (result.ErrorText != null)
                    throw new InstallException(result.ErrorText);

                UI(() => SetStepState(PackageInstallRing, LatestReleaseText, "Completed"));
                success = true;
                try { File.Delete(dlPath); } catch { }

                UpdateStatus("");
                UpdateStatus("Yorii Launcher has been installed successfully!");
            }
            catch (OperationCanceledException)
            {
                _cancelled = true;
            }
            catch (InstallException ex)
            {
                UI(() => { StatusText.Text = ex.Message; StatusText.Foreground = FindBrush("#F44747"); });
            }
            catch (Exception ex)
            {
                UI(() => { StatusText.Text = ex.Message; StatusText.Foreground = FindBrush("#F44747"); });
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _installing = false;
                UI(() =>
                {
                    ProgressBar.Visibility = Visibility.Collapsed;
                    ProgressBar.IsIndeterminate = false;
                    if (_cancelled)
                    {
                        StatusText.Foreground = FindBrush("#9E9E9E");
                        StatusText.Text = "Installation cancelled.";
                        InstallBtn.Visibility = Visibility.Visible;
                    }
                    else if (success)
                    {
                        StatusText.Foreground = FindBrush("#79C979");
                        StatusText.Text = "Installation complete! You can close this window.";
                        InstallBtn.Visibility = Visibility.Hidden;
                    }
                    else
                    {
                        InstallBtn.Visibility = Visibility.Visible;
                    }
                });
            }
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_installing)
            {
                SetStepState(CertDownloadRing, DownloadCertificateText, "Pending");
                SetStepState(CertInstallRing, InstallCertificateText, "Pending");
                SetStepState(PackageDownloadRing, FetchLatestReleaseText, "Pending");
                SetStepState(PackageInstallRing, LatestReleaseText, "Pending");
                CertDownloadRing.Visibility = Visibility.Hidden;
                CertInstallRing.Visibility = Visibility.Hidden;
                PackageDownloadRing.Visibility = Visibility.Hidden;
                PackageInstallRing.Visibility = Visibility.Hidden;
                _cts?.Cancel();
            }
            else
            {
                Close();
            }
        }

        private void UI(Action a) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(a));

        private void UpdateStatus(string msg) =>
            UI(() => { if (!string.IsNullOrEmpty(msg)) StatusText.Text = msg; });

        private static Brush FindBrush(string hex) =>
            new BrushConverter().ConvertFromString(hex) as Brush;

        private class InstallException : Exception
        {
            public InstallException(string msg) : base(msg) { }
        }
    }
}
