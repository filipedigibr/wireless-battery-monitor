using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace WirelessBatterySetup;

internal static class SetupProgram
{
    internal const string AppVersion = "1.0.1";
    internal const string DisplayName = "Monitor de Bateria Wireless & Bluetooth";
    internal const string Publisher = "Wireless Battery Monitor";
    internal const string UninstallKeyName = "WirelessBatteryMonitor";
    internal const string RunValueName = "WirelessBatteryMonitor";
    internal const string LegacyRunValueName = "KSnakeX11BatteryTray";
    internal const string ShutdownEventName = @"Local\WirelessBatteryMonitor_ShutdownEvent";
    internal const string MainExeName = "KSnakeBatteryTray.exe";
    internal const string PayloadResourceName = "KSnakeBatteryTray.exe.br";
    internal const string UninstallExeName = "Uninstall.exe";
    internal const string ShortcutName = "Monitor de Bateria Wireless.lnk";

    internal static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WirelessBatteryMonitor");

    internal static string StartMenuShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", ShortcutName);

    internal static string DesktopShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ShortcutName);

    internal static string UninstallRegistryPath =>
        $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{UninstallKeyName}";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        bool isUninstall = args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        bool isQuiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));

        if (isUninstall)
        {
            RunUninstall(isQuiet);
            return;
        }

        Application.Run(new SetupForm());
    }

    internal static bool IsAlreadyInstalled() =>
        File.Exists(Path.Combine(InstallDir, MainExeName));

    internal static void StopRunningApp()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShutdownEventName, out var evt))
            {
                using (evt)
                {
                    evt.Set();
                }
            }
        }
        catch { }

        var deadline = Environment.TickCount64 + 2500;
        while (Environment.TickCount64 < deadline)
        {
            if (Process.GetProcessesByName("KSnakeBatteryTray").Length == 0)
                return;
            Thread.Sleep(150);
        }

        foreach (var proc in Process.GetProcessesByName("KSnakeBatteryTray"))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(1500);
            }
            catch { }
            finally { proc.Dispose(); }
        }
    }

    internal static void PerformInstall(bool autoStart, bool createStartMenu, bool createDesktop, bool launchAfter)
    {
        StopRunningApp();

        Directory.CreateDirectory(InstallDir);

        string targetExe = Path.Combine(InstallDir, MainExeName);
        using (var resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName)
               ?? throw new InvalidOperationException("Payload interno comprimido não encontrado no instalador."))
        using (var brotli = new BrotliStream(resStream, CompressionMode.Decompress))
        using (var fs = new FileStream(targetExe, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            brotli.CopyTo(fs);
        }

        string currentExe = Environment.ProcessPath ?? string.Empty;
        string targetUninstaller = Path.Combine(InstallDir, UninstallExeName);
        if (!string.IsNullOrWhiteSpace(currentExe) &&
            !string.Equals(Path.GetFullPath(currentExe), Path.GetFullPath(targetUninstaller), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(currentExe, targetUninstaller, overwrite: true);
        }

        if (createStartMenu)
            CreateShortcut(StartMenuShortcutPath, targetExe, InstallDir, DisplayName);
        else if (File.Exists(StartMenuShortcutPath))
            File.Delete(StartMenuShortcutPath);

        if (createDesktop)
            CreateShortcut(DesktopShortcutPath, targetExe, InstallDir, DisplayName);

        using (var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
        {
            if (runKey is not null)
            {
                runKey.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
                if (autoStart)
                    runKey.SetValue(RunValueName, $"\"{targetExe}\"");
                else
                    runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }

        long totalBytes = new FileInfo(targetExe).Length + (File.Exists(targetUninstaller) ? new FileInfo(targetUninstaller).Length : 0);
        int estimatedKb = Math.Max(1, (int)(totalBytes / 1024));

        using (var uninstKey = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath, writable: true))
        {
            uninstKey.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
            uninstKey.SetValue("DisplayVersion", AppVersion, RegistryValueKind.String);
            uninstKey.SetValue("Publisher", Publisher, RegistryValueKind.String);
            uninstKey.SetValue("InstallLocation", InstallDir, RegistryValueKind.String);
            uninstKey.SetValue("DisplayIcon", $"{targetExe},0", RegistryValueKind.String);
            uninstKey.SetValue("UninstallString", $"\"{targetUninstaller}\" --uninstall", RegistryValueKind.String);
            uninstKey.SetValue("QuietUninstallString", $"\"{targetUninstaller}\" --uninstall --quiet", RegistryValueKind.String);
            uninstKey.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
            uninstKey.SetValue("EstimatedSize", estimatedKb, RegistryValueKind.DWord);
            uninstKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
            uninstKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }

        if (launchAfter)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = targetExe,
                WorkingDirectory = InstallDir,
                UseShellExecute = true
            });
        }
    }

    internal static void RunUninstall(bool quiet)
    {
        if (!quiet)
        {
            var confirm = MessageBox.Show(
                $"Deseja desinstalar o {DisplayName}?\n\nIsso removerá o aplicativo, os atalhos e a inicialização automática.",
                $"Desinstalar — {DisplayName}",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes)
                return;
        }

        try
        {
            StopRunningApp();

            TryDeleteFile(StartMenuShortcutPath);
            TryDeleteFile(DesktopShortcutPath);

            using (var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                runKey?.DeleteValue(RunValueName, throwOnMissingValue: false);
                runKey?.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);
            }

            Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);

            TryDeleteFile(Path.Combine(InstallDir, MainExeName));

            string settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KSnakeBatteryTray");
            if (Directory.Exists(settingsDir))
            {
                try { Directory.Delete(settingsDir, recursive: true); } catch { }
            }

            if (Directory.Exists(InstallDir))
            {
                string escapedDir = InstallDir.Replace("\"", "\"\"");
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/C ping 127.0.0.1 -n 2 > nul & rd /s /q \"{escapedDir}\"",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false
                });
            }

            if (!quiet)
            {
                MessageBox.Show(
                    $"{DisplayName} foi desinstalado com sucesso.",
                    "Desinstalação concluída",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                MessageBox.Show(
                    $"Erro durante a desinstalação:\n{ex.Message}",
                    "Erro",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string workingDir, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(targetPath);
        link.SetWorkingDirectory(workingDir);
        link.SetDescription(description);
        link.SetIconLocation(targetPath, 0);
        ((IPersistFile)link).Save(shortcutPath, false);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}

internal sealed class SetupForm : Form
{
    private readonly CheckBox _chkAutoStart;
    private readonly CheckBox _chkStartMenu;
    private readonly CheckBox _chkDesktop;
    private readonly CheckBox _chkLaunch;
    private readonly Button _btnInstall;
    private readonly Button _btnUninstall;
    private readonly Label _lblStatus;

    public SetupForm()
    {
        bool installed = SetupProgram.IsAlreadyInstalled();

        Text = $"Instalador — {SetupProgram.DisplayName}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(470, 340);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(20, 21, 26);
        ForeColor = Color.FromArgb(244, 244, 245);

        try
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);
        }
        catch { }

        var lblTitle = new Label
        {
            Text = SetupProgram.DisplayName,
            Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
            Location = new Point(22, 18),
            AutoSize = true,
            ForeColor = Color.White
        };

        var lblSub = new Label
        {
            Text = $"Versão {SetupProgram.AppVersion}  •  Flyout Windows 11, DPI/Hz, Histórico e Múltiplos Dispositivos",
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(22, 46),
            Size = new Size(430, 20),
            ForeColor = Color.FromArgb(161, 161, 170)
        };

        var lblPath = new Label
        {
            Text = $"Pasta de instalação:\n{SetupProgram.InstallDir}",
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(22, 76),
            Size = new Size(430, 38),
            ForeColor = Color.FromArgb(200, 204, 212)
        };

        _chkAutoStart = new CheckBox
        {
            Text = "Iniciar automaticamente com o Windows",
            Location = new Point(24, 126),
            AutoSize = true,
            Checked = true,
            ForeColor = Color.FromArgb(244, 244, 245)
        };

        _chkStartMenu = new CheckBox
        {
            Text = "Criar atalho no Menu Iniciar",
            Location = new Point(24, 154),
            AutoSize = true,
            Checked = true,
            ForeColor = Color.FromArgb(244, 244, 245)
        };

        _chkDesktop = new CheckBox
        {
            Text = "Criar atalho na Área de Trabalho",
            Location = new Point(24, 182),
            AutoSize = true,
            Checked = false,
            ForeColor = Color.FromArgb(244, 244, 245)
        };

        _chkLaunch = new CheckBox
        {
            Text = "Abrir o aplicativo após concluir a instalação",
            Location = new Point(24, 210),
            AutoSize = true,
            Checked = true,
            ForeColor = Color.FromArgb(244, 244, 245)
        };

        _lblStatus = new Label
        {
            Text = installed
                ? "Uma instalação existente foi detectada. Você pode atualizar ou desinstalar."
                : "Será registrado em Programas e Recursos (sem precisar de administrador).",
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(22, 246),
            Size = new Size(426, 32),
            ForeColor = Color.FromArgb(148, 163, 184)
        };

        _btnInstall = new Button
        {
            Text = installed ? "Atualizar / Reinstalar" : "Instalar",
            Location = new Point(22, 290),
            Size = new Size(160, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(22, 163, 74),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        _btnInstall.FlatAppearance.BorderSize = 0;
        _btnInstall.Click += OnInstallClick;

        _btnUninstall = new Button
        {
            Text = "Desinstalar",
            Location = new Point(192, 290),
            Size = new Size(115, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(185, 28, 28),
            ForeColor = Color.White,
            Visible = installed,
            Cursor = Cursors.Hand
        };
        _btnUninstall.FlatAppearance.BorderSize = 0;
        _btnUninstall.Click += (_, _) =>
        {
            Hide();
            SetupProgram.RunUninstall(quiet: false);
            Close();
        };

        var btnCancel = new Button
        {
            Text = "Fechar",
            Location = new Point(358, 290),
            Size = new Size(90, 34),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 47, 56),
            ForeColor = Color.White,
            Cursor = Cursors.Hand
        };
        btnCancel.FlatAppearance.BorderSize = 0;
        btnCancel.Click += (_, _) => Close();

        Controls.AddRange(
        [
            lblTitle, lblSub, lblPath,
            _chkAutoStart, _chkStartMenu, _chkDesktop, _chkLaunch,
            _lblStatus, _btnInstall, _btnUninstall, btnCancel
        ]);
    }

    private void OnInstallClick(object? sender, EventArgs e)
    {
        _btnInstall.Enabled = false;
        _btnUninstall.Enabled = false;
        _lblStatus.Text = "Extraindo e registrando no Windows...";
        Application.DoEvents();

        try
        {
            SetupProgram.PerformInstall(
                _chkAutoStart.Checked,
                _chkStartMenu.Checked,
                _chkDesktop.Checked,
                _chkLaunch.Checked);

            MessageBox.Show(
                "Instalação concluída com sucesso!\n\n" +
                "• Clique esquerdo no ícone: abre o painel rápido (flyout) perto do relógio.\n" +
                "• Clique direito: estilos, alertas, múltiplos ícones e gerenciar dispositivos.\n" +
                "• Registrado no Menu Iniciar e em Aplicativos Instalados (Programas e Recursos).",
                "Instalação concluída",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            Close();
        }
        catch (Exception ex)
        {
            _lblStatus.Text = "Falha na instalação.";
            MessageBox.Show(
                $"Não foi possível concluir a instalação:\n{ex.Message}",
                "Erro na instalação",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            _btnInstall.Enabled = true;
            _btnUninstall.Enabled = true;
        }
    }
}
