using System.Runtime.InteropServices;

namespace BatteryTray;

// Leitura somente de consulta do HyperX Cloud Stinger Core Wireless (receptor 03F0:0A93).
// Protocolo documentado no HyperBatteryMonitor (MIT). Envia apenas as duas consultas de conexão e bateria.
internal static class HyperxStinger
{
    private const ushort Vid = 0x03F0, Pid = 0x0A93;

    private static readonly byte[] Connection =
        { 0xFF, 0x07, 0x00, 0xFD, 0x04, 0x00, 0x00, 0x02, 0x82, 0x03, 0x05, 0x29, 0x0F, 0x00, 0x00, 0x03, 0x68, 0x10, 0x00, 0x00 };
    private static readonly byte[] Battery =
        { 0xFF, 0x07, 0x00, 0xFD, 0x04, 0x0C, 0xF1, 0x02, 0x01, 0x04, 0xF0, 0x0C };

    private static readonly int[] DischargingMv = { 4121, 4042, 3954, 3905, 3838, 3785, 3763, 3738, 3700, 3651, 3517 };
    private static readonly int[] ChargingMv = { 4130, 4090, 4050, 4000, 3967, 3946, 3930, 3916, 3871, 3831, 3400 };
    private static readonly int[] Pct = { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10, 0 };

    // Retorna null se o fone não estiver ligado ou não responder.
    public static DeviceBattery? TryRead()
    {
        try
        {
            foreach (var path in FindHidPaths($"vid_{Vid:x4}&pid_{Pid:x4}"))
            {
                IntPtr h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h == new IntPtr(-1)) continue;
                try
                {
                    if (!HidD_GetPreparsedData(h, out IntPtr pp)) continue;
                    var caps = new byte[256];
                    HidP_GetCaps(pp, caps);
                    HidD_FreePreparsedData(pp);
                    ushort page = BitConverter.ToUInt16(caps, 2);
                    ushort usage = BitConverter.ToUInt16(caps, 0);
                    ushort featLen = BitConverter.ToUInt16(caps, 8);
                    if (page != 0xFF00 || usage != 0x0001 || featLen < 32) continue;

                    var link = Exchange(h, featLen, Connection, isConnection: true);
                    if (link is null || link[11] != 0x01) return null; // fone desligado ou fora do alcance

                    var bat = Exchange(h, featLen, Battery, isConnection: false);
                    if (bat is null) return null;
                    return ParseBattery(bat);
                }
                finally { CloseHandle(h); }
            }
        }
        catch
        {
            // Dispositivo ausente ou erro de acesso: o app simplesmente não mostra o fone.
        }
        return null;
    }

    private static byte[]? Exchange(IntPtr h, ushort featLen, byte[] cmd, bool isConnection)
    {
        var buf = new byte[featLen];
        Array.Copy(cmd, buf, cmd.Length);
        if (!HidD_SetFeature(h, buf, featLen)) return null;

        // No receptor PlayStation a resposta pode vir atrasada; repete até 10 vezes.
        for (int i = 0; i < 10; i++)
        {
            Thread.Sleep(20);
            var r = new byte[featLen];
            r[0] = 0xFF;
            if (!HidD_GetFeature(h, r, featLen)) return null;
            if (isConnection)
            {
                if (!(r[1] == 0x01 && r[2] == 0x00)) return r;
            }
            else if (r[0] == 0xFF && r[1] == 0x12 && r[2] == 0x05)
            {
                return r;
            }
        }
        return null;
    }

    private static DeviceBattery? ParseBattery(byte[] r)
    {
        byte code = r[9];
        int mv = BitConverter.ToUInt16(r, 11);
        // Códigos documentados: 03 descarregando, 05 carregando, 06 cheio. Neste PC o fone também respondeu 09
        // (não documentado) com tensão válida; códigos desconhecidos são tratados como descarregando
        // se a tensão estiver numa faixa plausível de bateria de lítio.
        if (code is not (0x03 or 0x05 or 0x06) && (mv < 3300 || mv > 4300)) return null;
        int pct = code switch
        {
            0x06 => 100,
            0x05 => Interpolate(mv, ChargingMv),
            _ => Interpolate(mv, DischargingMv),
        };
        // 05 = carregando (documentado). 09 = não documentado; neste fone apareceu enquanto ele carregava,
        // com a tensão parada, por isso também é tratado como carregando.
        bool charging = code is 0x05 or 0x09;
        pct = Stabilize(pct, charging);
        return new DeviceBattery("HyperX Cloud Stinger Core", pct, DeviceKind.Headset, charging);
    }

    // A tensão oscila ±1-2 mV entre leituras, o que faz o número pular (ex.: 50% <-> 49% na divisa).
    // Carregando, o valor só sobe; descarregando, só desce. Saltos de 5 pontos ou mais passam direto
    // (troca de estado, carga completa, fone religado).
    private static int _shown = -1;
    private static bool _shownCharging;

    private static int Stabilize(int pct, bool charging)
    {
        if (_shown < 0 || charging != _shownCharging || Math.Abs(pct - _shown) >= 5)
            _shown = pct;
        else if (charging)
            _shown = Math.Max(_shown, pct);
        else
            _shown = Math.Min(_shown, pct);
        _shownCharging = charging;
        return _shown;
    }

    // Tabela de tensão decrescente -> percentual (interpolação linear entre os pontos).
    private static int Interpolate(int mv, int[] mvTable)
    {
        if (mv >= mvTable[0]) return Pct[0];
        if (mv <= mvTable[^1]) return Pct[^1];
        for (int i = 0; i < mvTable.Length - 1; i++)
        {
            if (mv <= mvTable[i] && mv >= mvTable[i + 1])
            {
                double t = (double)(mv - mvTable[i + 1]) / (mvTable[i] - mvTable[i + 1]);
                return (int)Math.Round(Pct[i + 1] + t * (Pct[i] - Pct[i + 1]));
            }
        }
        return Pct[^1];
    }

    private static List<string> FindHidPaths(string needle)
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_SetFeature(IntPtr h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr p, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr info, IntPtr dev, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr info, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr dev);
    [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr info);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
