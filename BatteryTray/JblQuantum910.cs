using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryTray;

// JBL Quantum 910 Wireless pelo receptor 2.4 GHz 0ECB:2088.
// Protocolo documentado por projetos independentes (jbl-quantum-linux de Tymon3310, HaloBattery,
// JBL_Battery_Tray): o fone ENVIA sozinho o input report 0x08 com a porcentagem no byte 1.
// Uma captura bateu com o app da JBL (95%). As fontes divergem sobre a interface (3 ou 5), então
// escutamos todas as coleções de fornecedor (página >= FF00) do receptor. Nada é enviado ao fone.
// O fone pode ficar muito tempo sem enviar: guardamos o último valor ouvido.
// No PC de teste, o receptor só expõe a interface 5 (coleções FF13 e FF99) e ficou 20 s sem enviar 08.
// Por isso também consultamos os feature reports de leitura 0x68 (link) e 0x49 (bateria), mapeados no
// Quantum 610 pelo jbl-quantum-linux (docs/QUANTUM610_PACKETS.md). Ainda não confirmado no 910.
internal static class JblQuantum910
{
    private const string Receiver = "vid_0ecb&pid_2088";
    private static readonly object Gate = new();
    private static readonly HashSet<string> Listening = new(StringComparer.OrdinalIgnoreCase);
    private static int _level = -1;

    public static DeviceBattery? TryRead()
    {
        List<string> paths;
        try { paths = HidPaths.Find(Receiver); }
        catch { return null; }

        if (paths.Count == 0)
        {
            lock (Gate) { _level = -1; } // receptor desconectado
            return null;
        }

        foreach (var p in paths) StartListener(p);

        // Consulta direta (GET_REPORT de leitura): 0x68 = fone ligado, 0x49 = bateria 0..100.
        foreach (var p in paths)
        {
            var link = GetFeature(p, 0x68);
            if (link is not null && link[1] == 0x00) { lock (Gate) _level = -1; return null; } // fone desligado
            var bat = GetFeature(p, 0x49);
            if (bat is not null && bat[1] >= 1 && bat[1] <= 100) { lock (Gate) _level = bat[1]; break; }
        }

        int level;
        lock (Gate) level = _level;
        return level >= 0 ? new DeviceBattery("JBL Quantum 910", level, DeviceKind.Headset) : null;
    }

    // Lê o feature report `id` de uma coleção de fornecedor. O firmware devolve outro relatório quando
    // não implementa o pedido, então só aceita se o primeiro byte for o próprio id.
    private static byte[]? GetFeature(string path, byte id)
    {
        try
        {
            IntPtr raw = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            using var h = new SafeFileHandle(raw, true);
            if (h.IsInvalid) return null;
            if (!HidD_GetPreparsedData(h, out IntPtr pp)) return null;
            var caps = new byte[256];
            HidP_GetCaps(pp, caps);
            HidD_FreePreparsedData(pp);
            ushort page = BitConverter.ToUInt16(caps, 2);
            int featLen = BitConverter.ToUInt16(caps, 8);
            if (page < 0xFF00 || featLen < 2) return null;

            var buf = new byte[featLen];
            buf[0] = id;
            bool ok = HidD_GetFeature(h, buf, featLen);
            Log($"{Tag(path)} page={page:X4} feature {id:X2} {(ok ? "ok" : "falhou")}", ok ? buf[..Math.Min(featLen, 8)] : new byte[] { id });
            return ok && buf[0] == id ? buf : null;
        }
        catch { return null; }
    }

    // Para o --dump: descreve as coleções do receptor JBL (ajuda a diagnosticar no PC de quem tem o fone).
    public static List<string> Diagnose()
    {
        var lines = new List<string>();
        foreach (var path in HidPaths.Find("vid_0ecb"))
        {
            IntPtr raw = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            using var h = new SafeFileHandle(raw, true);
            string tag = path.Length > 60 ? path.Substring(path.IndexOf("vid_", StringComparison.OrdinalIgnoreCase), 40) : path;
            if (h.IsInvalid) { lines.Add($"  JBL {tag}: sem acesso"); continue; }
            if (!HidD_GetPreparsedData(h, out IntPtr pp)) { lines.Add($"  JBL {tag}: sem descritor"); continue; }
            var caps = new byte[256];
            HidP_GetCaps(pp, caps);
            HidD_FreePreparsedData(pp);
            lines.Add($"  JBL {tag}: page={BitConverter.ToUInt16(caps, 2):X4} usage={BitConverter.ToUInt16(caps, 0):X4} in={BitConverter.ToUInt16(caps, 4)} out={BitConverter.ToUInt16(caps, 6)} feat={BitConverter.ToUInt16(caps, 8)}");
        }
        return lines;
    }

    private static void StartListener(string path)
    {
        lock (Gate)
        {
            if (Listening.Contains(path)) return;
            Listening.Add(path);
        }

        var t = new Thread(() => Listen(path)) { IsBackground = true, Name = "JBL listener" };
        t.Start();
    }

    private static void Listen(string path)
    {
        try
        {
            IntPtr raw = CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // só leitura
            using var h = new SafeFileHandle(raw, true);
            if (h.IsInvalid) return;

            if (!HidD_GetPreparsedData(h, out IntPtr pp)) return;
            var caps = new byte[256];
            HidP_GetCaps(pp, caps);
            HidD_FreePreparsedData(pp);
            ushort page = BitConverter.ToUInt16(caps, 2);
            int inLen = BitConverter.ToUInt16(caps, 4);
            if (page < 0xFF00 || inLen < 2) return; // só coleções de fornecedor com relatório de entrada

            // Tenta ler o último relatório 0x08 na hora, sem esperar o fone enviar (pode não ser suportado).
            string tag = $"{Tag(path)} page={page:X4}";
            var now = new byte[inLen];
            now[0] = 0x08;
            // Só registra: o 08 é relatório de interrupção; pedido direto devolve preenchimento, não bateria.
            if (HidD_GetInputReport(h, now, inLen)) Log(tag + " get", now);

            using var fs = new FileStream(h, FileAccess.Read, inLen, false);
            var buf = new byte[inLen];
            while (true)
            {
                int n = fs.Read(buf, 0, inLen);
                if (n <= 0) break;
                Log(tag + " read", buf);
                Accept(buf);
            }
        }
        catch
        {
            // Receptor removido ou acesso negado: a thread termina e outra será criada se ele voltar.
        }
        finally
        {
            lock (Gate) Listening.Remove(path);
        }
    }

    // 0 é ignorado: um relatório 08 zerado (ex.: GetInputReport sem dado guardado) não é bateria real,
    // e com 0% o fone já estaria desligado.
    private static void Accept(byte[] r)
    {
        if (r.Length >= 2 && r[0] == 0x08 && r[1] >= 1 && r[1] <= 100)
            lock (Gate) _level = r[1];
    }

    // Últimos relatórios brutos recebidos, para o --dump (ajuda a descobrir o formato no PC com o fone).
    private static readonly Queue<string> Raw = new();

    private static void Log(string tag, byte[] r)
    {
        int len = r.Length;
        while (len > 1 && r[len - 1] == 0) len--;
        string line = $"  {DateTime.Now:HH:mm:ss} {tag}: {BitConverter.ToString(r, 0, Math.Max(len, 2))}";
        lock (Gate)
        {
            Raw.Enqueue(line);
            while (Raw.Count > 40) Raw.Dequeue();
        }
    }

    public static List<string> RawReports()
    {
        lock (Gate) return Raw.ToList();
    }

    private static string Tag(string path)
    {
        int i = path.IndexOf("mi_", StringComparison.OrdinalIgnoreCase);
        int j = path.IndexOf('#', Math.Max(i, 0) + 1);
        return i >= 0 && j > i ? path[i..j] : "";
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("hid.dll")] private static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
}
