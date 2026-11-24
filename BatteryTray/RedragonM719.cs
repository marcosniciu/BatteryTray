using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryTray;

// Mouse Redragon M719 Invader Pro, receptor 25A7:FA7E.
// Protocolo observado no software oficial (OemDrv.exe): feature report 08 de 17 bytes na coleção FF02:0002,
// resposta no input report 09 de 17 bytes na coleção FF01. Último byte = 0x55 - soma dos 16 primeiros.
// Envia só as consultas que o software oficial envia (02 = conexão, 04 = bateria). Nada é gravado no mouse.
// Na resposta ao 04, o byte 6 é o nível em escala 0..10. Confirmado contra o software oficial: 4 = 40%, 3 = 30%.
// Byte 7 = 01 enquanto carrega pelo cabo.
internal static class RedragonM719
{
    private const string Receiver = "vid_25a7&pid_fa7e&mi_01";

    public static DeviceBattery? TryRead()
    {
        try
        {
            string? req = HidPaths.Find(Receiver + "&col07").FirstOrDefault();
            string? rsp = HidPaths.Find(Receiver + "&col05").FirstOrDefault();
            if (req is null || rsp is null) return null;

            IntPtr hq = CreateFileW(req, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (hq == new IntPtr(-1)) return null;
            var hr = new SafeFileHandle(CreateFileW(rsp, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero), true);
            if (hr.IsInvalid) { CloseHandle(hq); return null; }

            using var fs = new FileStream(hr, FileAccess.Read, 17, true);
            try
            {
                var link = Query(hq, fs, 0x02);
                if (link is null || link[2] != 0x01) return null; // mouse desligado ou fora do alcance

                var bat = Query(hq, fs, 0x04);
                if (bat is null || bat[5] < 1) return null;
                int level = Math.Clamp(bat[6] * 10, 0, 100);
                // Byte 7: 01 com o cabo conectado (carregando), 00 sem o cabo. Observado neste mouse.
                bool charging = bat[7] == 0x01;
                return new DeviceBattery("Redragon M719 Invader Pro", level, DeviceKind.Mouse, charging);
            }
            finally { CloseHandle(hq); }
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? Query(IntPtr hq, FileStream fs, byte cmd)
    {
        var buf = new byte[17];
        buf[0] = 0x08; buf[1] = cmd;
        buf[16] = Checksum(buf);

        var rd = new byte[17];
        var task = fs.ReadAsync(rd, 0, 17);
        if (!HidD_SetFeature(hq, buf, 17)) return null;
        if (!task.Wait(1500)) return null;
        // Só aceita a resposta certa: relatório 09, comando ecoado e checksum válido.
        if (rd[0] != 0x09 || rd[1] != cmd || rd[16] != Checksum(rd)) return null;
        return rd;
    }

    private static byte Checksum(byte[] b)
    {
        int sum = 0;
        for (int i = 0; i < 16; i++) sum += b[i];
        return (byte)((0x55 - sum) & 0xFF);
    }

    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetFeature(IntPtr h, byte[] buf, int len);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}

// Enumeração de caminhos HID compartilhada.
internal static class HidPaths
{
    public static List<string> Find(string needle)
    {
        var list = new List<string>();
        HidD_GetHidGuid(out Guid guid);
        IntPtr info = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, 0x10 | 0x02);
        for (uint idx = 0; ; idx++)
        {
            var ifd = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            if (!SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref guid, idx, ref ifd)) break;
            var detail = Marshal.AllocHGlobal(1024);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (SetupDiGetDeviceInterfaceDetailW(info, ref ifd, detail, 1024, out _, IntPtr.Zero))
                {
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (path.Contains(needle, StringComparison.OrdinalIgnoreCase)) list.Add(path);
                }
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        SetupDiDestroyDeviceInfoList(info);
        return list;
    }

    // Para o diagnóstico: um item por aparelho (VID:PID), com fabricante e nome que ele informa.
    public static List<string> Describe()
    {
        var seen = new SortedDictionary<string, string>();
        foreach (var path in Find("hid#"))
        {
            var m = System.Text.RegularExpressions.Regex.Match(path, @"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            string key = $"{m.Groups[1].Value.ToUpperInvariant()}:{m.Groups[2].Value.ToUpperInvariant()}";
            if (seen.TryGetValue(key, out var have) && have.Length > 0) continue;
            string name = "";
            IntPtr h = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h != new IntPtr(-1))
            {
                var b = new byte[256];
                string maker = HidD_GetManufacturerString(h, b, b.Length) ? Clean(b) : "";
                Array.Clear(b);
                string prod = HidD_GetProductString(h, b, b.Length) ? Clean(b) : "";
                name = $"{maker} {prod}".Trim();
                CloseHandle(h);
            }
            seen[key] = name;
        }
        return seen.Select(kv => $"  {kv.Key} {kv.Value}").ToList();
    }

    private static string Clean(byte[] b) => System.Text.Encoding.Unicode.GetString(b).Split('\0')[0].Trim();

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("hid.dll")] private static extern bool HidD_GetManufacturerString(IntPtr h, byte[] b, int len);
    [DllImport("hid.dll")] private static extern bool HidD_GetProductString(IntPtr h, byte[] b, int len);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr p, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr info, IntPtr dev, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr info, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr dev);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr info);
}
