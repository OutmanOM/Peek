using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SetupExperience;

internal sealed class SetupForm : Form
{
    private readonly WebView2 _video = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(12, 14, 18) };
    private readonly CheckBox _launchAtStartup = new();
    private readonly Button _install = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private bool _installing;
    private bool _installed;

    public SetupForm()
    {
        Text = "Peek Setup";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.None;
        ClientSize = new Size(1050, 680);
        MinimumSize = MaximumSize = Size;
        BackColor = Color.FromArgb(12, 14, 18);
        Padding = new Padding(1);
        BuildLayout();
        Shown += InitializeVideoAsync;
        FormClosing += (_, e) => { if (_installing) e.Cancel = true; };
    }

    private void BuildLayout()
    {
        var shell = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(12, 14, 18), Padding = new Padding(1) };
        Controls.Add(shell);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(12, 14, 18), ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        shell.Controls.Add(layout);
        var videoHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(12, 14, 18) };
        videoHost.Controls.Add(_video);
        layout.Controls.Add(videoHost, 0, 0);

        var footer = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        layout.Controls.Add(footer, 0, 1);
        _launchAtStartup.Text = "Launch Peek when I sign in";
        _launchAtStartup.AutoSize = true;
        _launchAtStartup.Checked = true;
        _launchAtStartup.Font = new Font("Segoe UI", 10f);
        _launchAtStartup.ForeColor = Color.FromArgb(57, 62, 74);
        _launchAtStartup.Location = new Point(24, 34);
        footer.Controls.Add(_launchAtStartup);

        _status.Text = "Ready to install Peek";
        _status.Font = new Font("Segoe UI", 9f);
        _status.ForeColor = Color.FromArgb(119, 126, 141);
        _status.AutoSize = true;
        _status.Location = new Point(24, 8);
        footer.Controls.Add(_status);

        _progress.Visible = false;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Location = new Point(24, 13);
        _progress.Size = new Size(580, 7);
        footer.Controls.Add(_progress);

        _install.Text = "Install Peek";
        _install.Font = new Font("Segoe UI Semibold", 10f);
        _install.Size = new Size(154, 44);
        _install.Location = new Point(864, 24);
        _install.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _install.FlatStyle = FlatStyle.Flat;
        _install.FlatAppearance.BorderSize = 0;
        _install.BackColor = Color.FromArgb(89, 119, 235);
        _install.ForeColor = Color.White;
        _install.Cursor = Cursors.Hand;
        _install.Click += InstallOrLaunch;
        footer.Controls.Add(_install);
        footer.Resize += (_, _) => _install.Left = footer.ClientSize.Width - _install.Width - 24;

        var close = new Label
        {
            Text = "×", Font = new Font("Segoe UI", 24f), ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter, Size = new Size(46, 46), Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(ClientSize.Width - 58, 8), BackColor = Color.FromArgb(96, 12, 14, 18), Cursor = Cursors.Hand
        };
        close.Click += (_, _) => Close();
        videoHost.Controls.Add(close);
        videoHost.Resize += (_, _) => close.Left = videoHost.ClientSize.Width - close.Width - 12;
        videoHost.MouseDown += DragWindow;
        close.MouseDown += DragWindow;
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
        }
    }

    private async void InitializeVideoAsync(object? sender, EventArgs e)
    {
        try
        {
            string htmlPath = MaterializePromoVideo();
            await _video.EnsureCoreWebView2Async();
            _video.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _video.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _video.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _video.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
        }
        catch
        {
            _status.Text = "Install Peek";
        }
    }

    private static string MaterializePromoVideo()
    {
        const string resourceName = "SetupExperience.Peek_Promo_v2.mp4";
        using Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("The Peek setup video is missing.");
        string folder = Path.Combine(Path.GetTempPath(), "PeekSetupMedia");
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, "Peek_Promo_v2.mp4");
        if (!File.Exists(destination) || new FileInfo(destination).Length != source.Length)
        {
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read);
            source.CopyTo(output);
        }
        string htmlPath = Path.Combine(folder, "setup.html");
        const string html = "<!doctype html><html><head><meta name='viewport' content='width=device-width,initial-scale=1'><style>*{box-sizing:border-box}html,body{margin:0;width:100%;height:100%;overflow:hidden;background:#0c0e12}video{width:100%;height:100%;object-fit:cover}</style></head><body><video id='promo' src='Peek_Promo_v2.mp4' autoplay muted loop playsinline></video><script>const v=document.getElementById('promo');v.muted=true;v.play().catch(()=>{});</script></body></html>";
        File.WriteAllText(htmlPath, html);
        return htmlPath;
    }
    private async void InstallOrLaunch(object? sender, EventArgs e)
    {
        if (_installed)
        {
            LaunchPeek();
            return;
        }
        if (_installing) return;
        _installing = true;
        _install.Enabled = false;
        _install.Text = "Installing…";
        _launchAtStartup.Enabled = false;
        _status.Text = "Installing Peek";
        _status.Visible = false;
        _progress.Visible = true;
        try
        {
            var progress = new Progress<InstallProgress>(value =>
            {
                _progress.Value = Math.Clamp(value.Percent, 0, 100);
                _status.Text = value.Detail;
            });
            bool launchAtStartup = _launchAtStartup.Checked;
            await Task.Run(() => InstallPayload(progress, launchAtStartup));
            _progress.Value = 100;
            _progress.Visible = false;
            _status.Visible = true;
            _status.Text = "Peek is ready";
            _install.Text = "Launch Peek";
            _install.Enabled = true;
            _installed = true;
        }
        catch (Exception ex)
        {
            _progress.Visible = false;
            _status.Visible = true;
            _status.Text = ex.Message.Length > 56 ? ex.Message[..53] + "…" : ex.Message;
            _install.Text = "Try again";
            _install.Enabled = true;
            MessageBox.Show(this, ex.Message, "Peek setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _installing = false;
            _launchAtStartup.Enabled = true;
        }
    }

    private static void InstallPayload(IProgress<InstallProgress> progress, bool launchAtStartup)
    {
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        string target = Path.Combine(programs, "Peek");
        string staging = Path.Combine(programs, $".Peek-{Guid.NewGuid():N}.installing");
        string backup = Path.Combine(programs, $".Peek-{Guid.NewGuid():N}.previous");
        Directory.CreateDirectory(staging);
        try
        {
            using Stream archiveStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SetupExperience.PeekPayload.zip")
                ?? throw new FileNotFoundException("The Peek app files are missing from this setup.");
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
            long total = Math.Max(1, archive.Entries.Sum(entry => entry.Length));
            long written = 0;
            byte[] buffer = new byte[128 * 1024];
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string destination = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                string prefix = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid path in app package.");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using Stream input = entry.Open();
                using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length);
                int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                {
                    output.Write(buffer, 0, count);
                    written += count;
                    progress.Report(new InstallProgress((int)Math.Min(96, written * 96 / total), "Installing Peek…"));
                }
            }
            string executable = Path.Combine(staging, "Peek.exe");
            if (!File.Exists(executable) || !File.Exists(Path.Combine(staging, "resources.pri")))
                throw new FileNotFoundException("The Peek app package is incomplete.");
            StopPreviousPeek(target);
            if (Directory.Exists(target)) Directory.Move(target, backup);
            try { Directory.Move(staging, target); }
            catch
            {
                if (Directory.Exists(backup) && !Directory.Exists(target)) Directory.Move(backup, target);
                throw;
            }
            executable = Path.Combine(target, "Peek.exe");
            CreateShortcut(executable, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Peek.lnk"));
            SetLaunchAtStartup(executable, launchAtStartup);
            progress.Report(new InstallProgress(100, "Ready to launch"));
            try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch { }
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            throw;
        }
    }

    private static void StopPreviousPeek(string installFolder)
    {
        string expectedExecutable = Path.GetFullPath(Path.Combine(installFolder, "Peek.exe"));
        foreach (Process process in Process.GetProcessesByName("Peek"))
        {
            using (process)
            {
                string? runningExecutable;
                try { runningExecutable = process.MainModule?.FileName; }
                catch { continue; }
                if (string.IsNullOrWhiteSpace(runningExecutable)
                    || !string.Equals(Path.GetFullPath(runningExecutable), expectedExecutable, StringComparison.OrdinalIgnoreCase))
                    continue;
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(8000))
                    throw new TimeoutException("The previous Peek instance is still closing. Try setup again in a moment.");
            }
        }
    }
    private static void SetLaunchAtStartup(string executable, bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
            ?? throw new InvalidOperationException("Could not open the current-user startup settings.");
        if (enabled) key.SetValue("PeekStartup", $"\"{executable}\" --tray", RegistryValueKind.String);
        else key.DeleteValue("PeekStartup", false);
    }

    private static void CreateShortcut(string executable, string shortcutPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable, so setup could not create the Start menu shortcut.");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = ((dynamic)shell!).CreateShortcut(shortcutPath);
            ((dynamic)shortcut).TargetPath = executable;
            ((dynamic)shortcut).WorkingDirectory = Path.GetDirectoryName(executable);
            ((dynamic)shortcut).Arguments = "--show";
            ((dynamic)shortcut).Description = "Preview files with Peek";
            ((dynamic)shortcut).Save();
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private async void LaunchPeek()
    {
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Peek", "Peek.exe");
        try
        {
            Hide();
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--tray",
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Windows did not start Peek.");
            _install.Enabled = false;
            _install.Text = "Opening…";
            try { await Task.Run(() => process.WaitForInputIdle(10000)); } catch { }
            for (int attempt = 0; attempt < 24 && !process.HasExited && process.MainWindowHandle == IntPtr.Zero; attempt++)
                await Task.Delay(250);
            if (process.HasExited)
                throw new InvalidOperationException($"Peek exited during startup (code {process.ExitCode}).");
            if (process.MainWindowHandle == IntPtr.Zero)
                throw new InvalidOperationException("Peek started but did not create its preview window.");
            Close();
        }
        catch (Exception ex)
        {
            Show();
            Activate();
            _install.Enabled = true;
            _install.Text = "Launch Peek";
            MessageBox.Show(this, $"Peek is installed, but Windows could not open its window.\n\n{ex.Message}", "Peek setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(Color.FromArgb(52, 57, 69), 1);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    private sealed record InstallProgress(int Percent, string Detail);

    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
