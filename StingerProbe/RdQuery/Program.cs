using System.Runtime.InteropServices;

// Somente leitura: envia os dois comandos de consulta documentados pelo HyperBatteryMonitor
// (conexão e bateria) para o Cloud Stinger Core Wireless (03F0:0A93) via feature report FF.
// Nada além dessas duas consultas é enviado.
internal static class Program
{
    const ushort Vid = 0x03F0, Pid = 0x0A93;

    static readonly byte[] StingerConnection =
        { 0xFF, 0x07, 0x00, 0xFD, 0x04, 0x00, 0x00, 0x02, 0x82, 0x03, 0x05, 0x29, 0x0F, 0x00, 0x00, 0x03, 0x68, 0x10, 0x00, 0x00 };
    static readonly byte[] StingerBattery =
        { 0xFF, 0x07, 0x00, 0xFD, 0x04, 0x0C, 0xF1, 0x02, 0x01, 0x04, 0xF0, 0x0C };

    // Envia só as consultas que o software oficial envia (08 04 e 08 02) e lê a resposta (relatório 09).
    static void Main()
    {
        string? req = null, rsp = null;
        foreach (var p in FindHidPaths("vid_25a7&pid_fa7e&mi_01&col07")) req = p;
        foreach (var p in FindHidPaths("vid_25a7&pid_fa7e&mi_01&col05")) rsp = p;
        if (req is null || rsp is null) { Console.WriteLine("receptor nao encontrado"); return; }
        IntPtr hq = CreateFileW(req, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        var fs = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(CreateFileW(rsp, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero), true), FileAccess.Read, 17, true);
        foreach (byte cmd in new byte[] { 0x02, 0x04, 0x04 })
        {
            var buf = new byte[17]; buf[0] = 0x08; buf[1] = cmd;
            int sum = 0; for (int i = 0; i < 16; i++) sum += buf[i];
            buf[16] = (byte)((0x55 - sum) & 0xFF);
            var rd = new byte[17];
            var task = fs.ReadAsync(rd, 0, 17);
            bool ok = HidD_SetFeature(hq, buf, 17);
            bool got = task.Wait(1500);
            Console.WriteLine($"cmd {cmd:X2} set={ok} resp={(got ? BitConverter.ToString(rd) : "timeout")}");
            if (!got) break;
        }
        CloseHandle(hq);
    }

    // Replica o fluxo do HyperBatteryMonitor: SetFeature(cmd), 20 ms, GetFeature(FF); repete no receptor PS.
    static string? Exchange(IntPtr h, ushort featLen, byte[] cmd, bool ps, bool isConn)
    {
        var buf = new byte[featLen];
        Array.Copy(cmd, buf, cmd.Length);
        if (!HidD_SetFeature(h, buf, featLen)) return "SetFeature falhou: " + Marshal.GetLastWin32Error();

        for (int i = 0; i < (ps ? 10 : 1); i++)
        {
            Thread.Sleep(20);
            var r = new byte[featLen];
            r[0] = 0xFF;
            if (!HidD_GetFeature(h, r, featLen)) return "GetFeature falhou: " + Marshal.GetLastWin32Error();
            string hex = BitConverter.ToString(r, 0, Math.Min((int)featLen, 16));
            if (!isConn && r[0] == 0xFF && r[1] == 0x12 && r[2] == 0x05)
            {
                return $"{hex}  | modo={r[9]:X2} mV={BitConverter.ToUInt16(r, 11)}";
            }
            if (isConn && !(r[1] == 0x01 && r[2] == 0x00))
            {
                return $"{hex}  | headset ligado={(r[11] == 0x01)}";
            }
            if (i == (ps ? 9 : 0)) return hex + "  (último)";
        }
        return null;
    }

    static List<string> FindHidPaths(string needle)
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
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetFeature(IntPtr h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr p, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr info, IntPtr dev, ref Guid g, uint idx, ref SP_DEVICE_INTERFACE_DATA d);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr info, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr dev);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr info);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
