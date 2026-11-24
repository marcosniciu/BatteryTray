using System.Diagnostics;
using System.Text;

namespace BatteryTray;

// Mostra a bateria dos aparelhos sem fio numa faixa da barra de tarefas, ao lado da área de notificação.
// Fontes: propriedade de bateria que o Windows reporta (DEVPKEY_Device_BatteryLevel), fone HyperX
// e mouse Redragon M719 pelos protocolos próprios. Clique direito na faixa abre o menu.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // --dump: grava as leituras em batterytray-dump.txt (ao lado do .exe) e sai, sem abrir a faixa.
        // --startup on|off: liga/desliga o início com o Windows para este .exe e sai.
        int si = Array.IndexOf(args, "--startup");
        if (si >= 0 && si + 1 < args.Length)
        {
            Startup.Set(args[si + 1] == "on");
            return;
        }

        if (args.Contains("--dump"))
        {
            var devices = TrayApp.ReadBatteries();
            // O JBL envia a bateria quando quer: escuta 20 s para registrar o que o receptor manda.
            bool hasJbl = HidPaths.Find("vid_0ecb&pid_2088").Count > 0;
            if (hasJbl)
            {
                Thread.Sleep(20_000);
                devices.RemoveAll(d => d.Name.StartsWith("JBL"));
                var jbl = JblQuantum910.TryRead();
                if (jbl is not null) devices.Add(jbl);
            }
            var lines = devices.Select(d => $"{d.Name}: {d.Percent}%").ToList();
            if (lines.Count == 0) lines.Add("Nenhum dispositivo com bateria");
            var diag = JblQuantum910.Diagnose();
            if (diag.Count > 0) { lines.Add(""); lines.Add("Receptor JBL encontrado:"); lines.AddRange(diag); }
            if (hasJbl)
            {
                var raw = JblQuantum910.RawReports();
                lines.Add("");
                lines.Add(raw.Count > 0 ? "Relatórios recebidos do JBL (20 s):" : "Nenhum relatório recebido do JBL em 20 s");
                lines.AddRange(raw);
            }
            lines.Add("");
            lines.Add("Aparelhos USB/HID conectados (VID:PID fabricante modelo):");
            lines.AddRange(HidPaths.Describe());
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "batterytray-dump.txt"), lines);
            return;
        }

        // Uma instância só: evita duas faixas sobrepostas.
        using var single = new Mutex(true, "BatteryTray.SingleInstance", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}

// Início automático por usuário (HKCU\...\Run): não precisa de administrador.
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "BatteryTray";

    public static bool IsEnabled()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) is string v && v.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool on)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(Name, false);
    }
}

internal sealed record DeviceBattery(string Name, int Percent, DeviceKind Kind = DeviceKind.Other, bool Charging = false);

internal sealed class TrayApp : ApplicationContext
{
    private const int RefreshMs = 60_000;

    private readonly TaskbarStrip _strip;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _timer;
    private bool _reading;

    public TrayApp()
    {
        _strip = new TaskbarStrip { StripMenu = _menu };
        BuildMenu(new List<DeviceBattery>());
        _strip.SetItems(new List<DeviceBattery>());

        _timer = new System.Windows.Forms.Timer { Interval = RefreshMs };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();

        Refresh();
    }

    // Lê os aparelhos fora da thread da interface (a consulta pode levar alguns segundos).
    private async void Refresh()
    {
        if (_reading) return;
        _reading = true;
        try
        {
            var devices = await Task.Run(ReadBatteries);
            _last = devices;
            BuildMenu(devices);
            _strip.SetItems(devices);
        }
        finally { _reading = false; }
    }

    private void BuildMenu(List<DeviceBattery> devices)
    {
        _menu.Items.Clear();
        if (devices.Count == 0)
            _menu.Items.Add(new ToolStripMenuItem("Nenhum dispositivo com bateria") { Enabled = false });
        foreach (var d in devices)
            _menu.Items.Add(new ToolStripMenuItem($"{d.Name}: {d.Percent}%{(d.Charging ? " (carregando)" : "")}") { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());
        var startup = new ToolStripMenuItem("Iniciar com o Windows") { Checked = Startup.IsEnabled() };
        startup.Click += (_, _) => { Startup.Set(!Startup.IsEnabled()); startup.Checked = Startup.IsEnabled(); };
        _menu.Items.Add(startup);
        var refresh = new ToolStripMenuItem("Atualizar agora");
        refresh.Click += (_, _) => Refresh();
        _menu.Items.Add(refresh);
        var diag = new ToolStripMenuItem(_diagnosing ? "Gerando diagnóstico (20 s)..." : "Gerar diagnóstico") { Enabled = !_diagnosing };
        diag.Click += (_, _) => RunDiagnostic();
        _menu.Items.Add(diag);
        var exit = new ToolStripMenuItem("Sair");
        exit.Click += (_, _) => ExitThread();
        _menu.Items.Add(exit);
    }

    // Roda este mesmo .exe com --dump e abre o arquivo gerado no Bloco de Notas.
    private bool _diagnosing;
    private List<DeviceBattery> _last = new();

    private async void RunDiagnostic()
    {
        if (_diagnosing || Environment.ProcessPath is not string exe) return;
        _diagnosing = true;
        BuildMenu(_last);
        try
        {
            string file = Path.Combine(AppContext.BaseDirectory, "batterytray-dump.txt");
            await Task.Run(() =>
            {
                using var p = Process.Start(new ProcessStartInfo(exe, "--dump") { UseShellExecute = false, CreateNoWindow = true });
                p?.WaitForExit(60_000);
            });
            if (File.Exists(file)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally
        {
            _diagnosing = false;
            BuildMenu(_last);
        }
    }

    internal static List<DeviceBattery> ReadBatteries()
    {
        // Consulta via PowerShell: Get-PnpDevice + Get-PnpDeviceProperty para cada dispositivo presente.
        const string script = """
            Get-PnpDevice -PresentOnly | ForEach-Object {
              $p = Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName '{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2' -ErrorAction SilentlyContinue
              if ($p -and $p.Data -ne $null) { Write-Output ($_.FriendlyName + '|' + $p.Data) }
            }
            """;

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ")}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        var result = new List<DeviceBattery>();
        try
        {
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(20_000);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = line.Split('|');
                    if (parts.Length == 2 && int.TryParse(parts[1], out int pct))
                        result.Add(new DeviceBattery(parts[0], Math.Clamp(pct, 0, 100), GuessKind(parts[0])));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        // Fone HyperX via protocolo próprio (receptor USB), já que o Windows não reporta a bateria dele.
        var hyperx = HyperxStinger.TryRead();
        if (hyperx is not null) result.Add(hyperx);

        // Fone JBL Quantum 910 (receptor 0ECB:2088): o fone envia a bateria sozinho; usamos o último valor ouvido.
        var jbl = JblQuantum910.TryRead();
        if (jbl is not null) result.Add(jbl);

        // Mouse Redragon M719 (receptor 25A7:FA7E), protocolo observado no software oficial.
        var mouse = RedragonM719.TryRead();
        if (mouse is not null) result.Insert(0, mouse);

        return result;
    }

    private static DeviceKind GuessKind(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("controller") || n.Contains("controle") || n.Contains("gamepad")) return DeviceKind.Controller;
        if (n.Contains("mouse")) return DeviceKind.Mouse;
        if (n.Contains("headset") || n.Contains("headphone") || n.Contains("buds") || n.Contains("fone")) return DeviceKind.Headset;
        if (n.Contains("keyboard") || n.Contains("teclado")) return DeviceKind.Keyboard;
        return DeviceKind.Other;
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _strip.Close();
        base.ExitThreadCore();
    }
}
