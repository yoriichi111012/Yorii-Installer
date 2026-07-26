using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MicaWPF.Controls;
using Windows.UI;
using Windows.UI.Xaml.Media;

namespace YoriiInstaller
{
    public partial class MainWindow : MicaWindow
    {
        private const string InstallScript = @"$ProgressPreference = 'SilentlyContinue'
$ErrorActionPreference = ""Stop""
$Owner      = ""yoriichi111012""
$Repository = ""Yorii-Launcher""

$CertificatePath = Join-Path $env:TEMP ""Yorii Launcher.cer""
$Headers = @{ ""User-Agent"" = ""Yorii Launcher Installer"" }

function Write-Step($Message) { Write-Output $Message }
function Write-Ok($Message)   { Write-Output $Message }
function Fail($Message)       { Write-Output ""[ERROR] $Message""; exit 1 }

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
if ([Enum]::GetNames([Net.SecurityProtocolType]) -contains ""Tls13"") {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
}

Write-Step ""Checking internet connection...""
try {
    Invoke-WebRequest -Uri ""https://www.google.com"" -Method Head -Headers $Headers -UseBasicParsing -TimeoutSec 10 | Out-Null
} catch {
    Fail ""No internet connection available.""
}
Write-Ok ""Internet connection verified.""

Write-Step ""Downloading signing certificate...""
$CertUrl = ""https://raw.githubusercontent.com/yoriichi111012/Yorii-Launcher/main/Yorii-Launcher-Certificate.cer""
try {
    Invoke-WebRequest -Uri $CertUrl -Headers $Headers -UseBasicParsing -OutFile $CertificatePath -MaximumRedirection 5
    if (!(Test-Path $CertificatePath) -or (Get-Item $CertificatePath).Length -eq 0) {
        Fail ""Certificate file is empty or missing.""
    }
} catch {
    Fail ""Failed to download signing certificate: $_""
}

Write-Step ""Verifying signing certificate...""
try {
    $Certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($CertificatePath)
    if ($Certificate.NotAfter -lt (Get-Date)) {
        Fail ""Signing certificate expired on $($Certificate.NotAfter).""
    }
    $Thumbprint = $Certificate.Thumbprint
    $Installed = Get-ChildItem Cert:\LocalMachine\Root -ErrorAction SilentlyContinue | Where-Object Thumbprint -eq $Thumbprint
    if ($null -eq $Installed) {
        Write-Step ""Installing signing certificate (UAC prompt)...""
        $CertCmd = ""Import-Certificate -FilePath '$CertificatePath' -CertStoreLocation Cert:\LocalMachine\Root""
        try {
            Start-Process powershell.exe -ArgumentList ""-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -Command `""$CertCmd`"""" -Verb RunAs -Wait -ErrorAction Stop
            Write-Ok ""Certificate installed.""
        } catch {
            Fail ""Certificate install denied or failed.""
        }
    } else {
        Write-Ok ""Certificate already installed.""
    }
} catch {
    Fail ""Certificate processing failed: $_""
} finally {
    if ($Certificate -is [System.IDisposable]) { $Certificate.Dispose() }
    if (Test-Path $CertificatePath) { Remove-Item $CertificatePath -Force -ErrorAction SilentlyContinue }
}

Write-Step ""Detecting system architecture...""
if ([Environment]::Is64BitOperatingSystem) {
    $Architecture = if ($env:PROCESSOR_ARCHITECTURE -eq ""ARM64"") { ""arm64"" } else { ""x64"" }
} else {
    Fail ""Yorii Launcher requires a 64-bit version of Windows.""
}
Write-Ok ""Architecture: $Architecture""

$PackageFileName = ""Yorii.Launcher_${Architecture}.msix""
$DownloadUrl = ""https://github.com/$Owner/$Repository/releases/latest/download/$PackageFileName""
$DownloadPath = Join-Path $env:TEMP $PackageFileName

Write-Step ""Downloading package...""
$ResponseStream = $null; $FileStream = $null; $Response = $null
try {
    $Request = [System.Net.HttpWebRequest]::Create($DownloadUrl)
    $Request.UserAgent = ""Yorii Launcher Installer""
    $Request.Timeout = 600000
    $Request.AllowAutoRedirect = $true
    $Response = $Request.GetResponse()
    $TotalBytes = $Response.ContentLength
    $ResponseStream = $Response.GetResponseStream()
    $FileStream = New-Object System.IO.FileStream($DownloadPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $Buffer = New-Object byte[] 65536
    $TotalBytesRead = 0L
    $Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $LastUiUpdate = [System.Diagnostics.Stopwatch]::StartNew()
    while (($BytesRead = $ResponseStream.Read($Buffer, 0, $Buffer.Length)) -gt 0) {
        $FileStream.Write($Buffer, 0, $BytesRead)
        $TotalBytesRead += $BytesRead
        if ($LastUiUpdate.ElapsedMilliseconds -ge 150 -or $TotalBytesRead -eq $TotalBytes) {
            $Elapsed = [Math]::Max($Stopwatch.Elapsed.TotalSeconds, 0.001)
            $SpeedBps = $TotalBytesRead / $Elapsed
            $SpeedMBps = $SpeedBps / 1MB
            $DownloadedMB = $TotalBytesRead / 1MB
            if ($TotalBytes -gt 0) {
                $TotalMB = $TotalBytes / 1MB
                $Percent = [Math]::Min(100, [int](($TotalBytesRead / $TotalBytes) * 100))
                $Remaining = $TotalBytes - $TotalBytesRead
                $Eta = if ($SpeedBps -gt 0) { [int]($Remaining / $SpeedBps) } else { 0 }
                $EtaStr = if ($Eta -ge 60) { ""{0}m {1}s"" -f [math]::Floor($Eta / 60), ($Eta % 60) } else { ""{0}s"" -f $Eta }
                $StatusText = ""{0:N1} MB / {1:N1} MB - {2:N1} MB/s - ETA: {3}"" -f $DownloadedMB, $TotalMB, $SpeedMBps, $EtaStr
                Write-Output ""[PROGRESS] $Percent|$StatusText""
            }
            $LastUiUpdate.Restart()
        }
    }
} catch {
    Fail ""Download failed: $($_.Exception.Message)""
} finally {
    Write-Output ""[PROGRESS] DONE""
    if ($null -ne $FileStream) { $FileStream.Dispose() }
    if ($null -ne $ResponseStream) { $ResponseStream.Dispose() }
    if ($null -ne $Response) { $Response.Close() }
}

if (!(Test-Path $DownloadPath) -or (Get-Item $DownloadPath).Length -eq 0) { 
    Fail ""Package could not be downloaded or was empty."" 
}

Write-Ok ""Download completed.""

Write-Step ""Installing Yorii Launcher...""
try {
    $AppInstaller = Get-AppxPackage Microsoft.DesktopAppInstaller -ErrorAction SilentlyContinue
    if ($null -eq $AppInstaller) { Fail ""Microsoft App Installer is not installed."" }
    Add-AppxPackage -Path $DownloadPath -ForceUpdateFromAnyVersion -ForceApplicationShutdown
} catch {
    Remove-Item $DownloadPath -Force -ErrorAction SilentlyContinue
    Fail ""$($_.Exception.Message)""
}

Write-Ok ""Installation completed.""
Remove-Item $DownloadPath -Force -ErrorAction SilentlyContinue
Write-Output """"
Write-Output ""Yorii Launcher has been installed successfully!""";

        private CancellationTokenSource _cts;
        private Process _process;
        private bool _cancelled;
        private bool _installing;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void SetStepState(MicaWPF.Controls.ProgressRing ring, System.Windows.Controls.TextBlock text, string state)
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

            // Reset UI step elements
            SetStepState(CertDownloadRing, DownloadCertificateText, "Pending");
            SetStepState(CertInstallRing, InstallCertificateText, "Pending");
            SetStepState(PackageDownloadRing, FetchLatestReleaseText, "Pending");
            SetStepState(PackageInstallRing, LatestReleaseText, "Pending");

            bool success = false;

            await Task.Run(() =>
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                byte[] scriptBytes = Encoding.Unicode.GetBytes(InstallScript);
                string encoded = Convert.ToBase64String(scriptBytes);
                startInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -NoLogo -EncodedCommand {encoded}";

                _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

                _process.OutputDataReceived += (s, args) =>
                {
                    if (args.Data == null || _cts.Token.IsCancellationRequested) return;
                    string line = args.Data;

                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        if (line.StartsWith("[PROGRESS] "))
                        {
                            string data = line.Substring("[PROGRESS] ".Length);
                            if (data == "DONE")
                            {
                                ProgressBar.IsIndeterminate = true;
                                StatusText.Text = "Installing package...";
                            }
                            else
                            {
                                ProgressBar.IsIndeterminate = false;
                                int pipe = data.IndexOf('|');
                                if (pipe > 0)
                                {
                                    if (int.TryParse(data.Substring(0, pipe), out int pct) && pct >= 0)
                                        ProgressBar.Value = pct;
                                    StatusText.Text = data.Substring(pipe + 1);
                                }
                            }
                        }
                        else if (line.StartsWith("[ERROR] "))
                        {
                            StatusText.Text = line.Substring(8);
                            StatusText.Foreground = FindBrush("#F44747");
                        }
                        else
                        {
                            StatusText.Text = line;

                            // Map steps to UI updates
                            if (line == "Downloading signing certificate...")
                            {
                                SetStepState(CertDownloadRing, DownloadCertificateText, "Active");
                            }
                            else if (line == "Verifying signing certificate...")
                            {
                                SetStepState(CertDownloadRing, DownloadCertificateText, "Completed");
                                SetStepState(CertInstallRing, InstallCertificateText, "Active");
                            }
                            else if (line == "Installing signing certificate (UAC prompt)...")
                            {
                                SetStepState(CertInstallRing, InstallCertificateText, "Active");
                            }
                            else if (line == "Certificate installed." || line == "Certificate already installed.")
                            {
                                SetStepState(CertInstallRing, InstallCertificateText, "Completed");
                            }
                            else if (line == "Downloading package...")
                            {
                                SetStepState(PackageDownloadRing, FetchLatestReleaseText, "Active");
                            }
                            else if (line == "Download completed.")
                            {
                                SetStepState(PackageDownloadRing, FetchLatestReleaseText, "Completed");
                            }
                            else if (line == "Installing Yorii Launcher...")
                            {
                                SetStepState(PackageInstallRing, LatestReleaseText, "Active");
                            }
                            else if (line == "Installation completed.")
                            {
                                SetStepState(PackageInstallRing, LatestReleaseText, "Completed");
                            }
                        }
                    }));
                };

                _process.ErrorDataReceived += (s, args) =>
                {
                    if (string.IsNullOrWhiteSpace(args.Data)) return;

                    if (args.Data.StartsWith("<Objs") || args.Data.StartsWith("_x0032_")) return;

                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        StatusText.Text = args.Data;
                        StatusText.Foreground = FindBrush("#FFAA8B");
                    }));
                };

                try
                {
                    _process.Start();
                    _process.BeginOutputReadLine();
                    _process.BeginErrorReadLine();

                    _cts.Token.Register(() =>
                    {
                        try { _process.Kill(); } catch { }
                    });

                    _process.WaitForExit();
                    _cancelled = _cts.Token.IsCancellationRequested;
                    success = _process.ExitCode == 0 && !_cancelled;
                }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        StatusText.Text = "Failed to start installer: " + ex.Message;
                        StatusText.Foreground = FindBrush("#F44747");
                    }));
                }
                finally
                {
                    _process.Dispose();
                    _process = null;
                }
            });

            _cts.Dispose();
            _cts = null;
            _installing = false;

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
                StatusText.Foreground = FindBrush("#F44747");
                InstallBtn.Visibility = Visibility.Visible;
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

        private static System.Windows.Media.Brush FindBrush(string hex)
        {
            return new System.Windows.Media.BrushConverter().ConvertFromString(hex) as System.Windows.Media.Brush;
        }
    }
}