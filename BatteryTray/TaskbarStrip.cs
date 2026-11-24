using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BatteryTray;

internal enum DeviceKind { Other, Mouse, Headset, Controller, Keyboard }

// Faixa na barra de tarefas, à esquerda da área de notificação (igual ao BatteryHub):
// desenho do aparelho + porcentagem. É uma janela sem borda, transparente por pixel, que fica sobre a barra.
internal sealed class TaskbarStrip : Form
{
    private List<DeviceBattery> _items = new();
    private readonly System.Windows.Forms.Timer _follow;
    private readonly string _iconFont;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ContextMenuStrip? StripMenu { get; set; }

    public TaskbarStrip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        _iconFont = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        // Acompanha a barra de tarefas (inclusive com "ocultar automaticamente", que desliza a barra para baixo):
        // a cada 100 ms só move a janela; redesenha apenas quando altura ou tema mudam.
        _follow = new System.Windows.Forms.Timer { Interval = 100 };
        _follow.Tick += (_, _) => Follow();
        _follow.Start();
    }

    private int _lastH = -1, _width, _ticks;
    private bool _lastLight;
    private Point _lastPos = new(int.MinValue, int.MinValue);

    private void Follow()
    {
        if (!TryGetTaskbar(out Rectangle bar, out int rightEdge)) return;

        // Barra recolhida ("ocultar automaticamente") ou jogo em tela cheia: some junto com ela.
        int screenBottom = Screen.PrimaryScreen?.Bounds.Bottom ?? bar.Bottom;
        bool barShown = bar.Top <= screenBottom - bar.Height / 2;
        if (!barShown) { if (Visible) Hide(); return; }
        if (!Visible) { Show(); _lastH = -1; }

        bool light = IsLightTaskbar();
        if (bar.Height != _lastH || light != _lastLight) { Render(); return; }

        var pos = new Point(rightEdge - _width, bar.Top);
        bool moved = pos != _lastPos;
        if (moved || ++_ticks % 20 == 0) // a cada 2 s reafirma que fica por cima da barra
        {
            SetWindowPos(Handle, new IntPtr(-1), pos.X, pos.Y, 0, 0, 0x0001 | 0x0010 | (moved ? 0u : 0x0002u));
            _lastPos = pos;
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style = unchecked((int)0x80000000);            // WS_POPUP
            cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000 | 0x00000008; // LAYERED | TOOLWINDOW | NOACTIVATE | TOPMOST
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public void SetItems(List<DeviceBattery> items)
    {
        _items = items;
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        StripMenu?.Show(Cursor.Position);
    }

    private void Render()
    {
        if (!TryGetTaskbar(out Rectangle bar, out int rightEdge)) return;
        if (!Visible) Show();

        int h = bar.Height;
        bool light = IsLightTaskbar();
        Color fg = light ? Color.FromArgb(255, 20, 20, 20) : Color.FromArgb(255, 245, 245, 245);

        using var textFont = new Font("Segoe UI", h * 0.30f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var glyphFont = new Font(_iconFont, h * 0.34f, FontStyle.Regular, GraphicsUnit.Pixel);

        // Mede cada item: ícone + espaço + "40%".
        int pad = (int)(h * 0.25f), gap = (int)(h * 0.12f), between = (int)(h * 0.40f);
        var parts = new List<(string glyph, string text, Color color, int gw, int tw, bool charging)>();
        Color boltColor = Color.FromArgb(255, 0, 120, 215);
        int bw = 0;
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
        {
            pg.TextRenderingHint = TextRenderingHint.AntiAlias;
            // Sem dispositivos: mostra um ícone de bateria com "--", para a faixa (e o menu Sair) continuar acessível.
            var shown = _items.Count > 0 ? _items : new List<DeviceBattery> { new("Nenhum dispositivo", -1) };
            foreach (var d in shown)
            {
                string t = d.Percent < 0 ? "--" : $"{d.Percent}%";
                Color c = d.Percent is >= 0 and <= 15 ? Color.FromArgb(255, 220, 40, 40) : fg;
                string gl = Glyph(d.Kind);
                int gw = (int)Math.Ceiling(pg.MeasureString(gl, glyphFont, PointF.Empty, StringFormat.GenericTypographic).Width);
                int tw = (int)Math.Ceiling(pg.MeasureString(t, textFont, PointF.Empty, StringFormat.GenericTypographic).Width);
                parts.Add((gl, t, c, gw, tw, d.Charging));
            }
            bw = (int)Math.Ceiling(pg.MeasureString("", glyphFont, PointF.Empty, StringFormat.GenericTypographic).Width);
        }

        // Carregando: raio azul logo depois da porcentagem, como no BatteryHub.
        int width = pad * 2 + parts.Sum(p => p.gw + gap + p.tw + (p.charging ? gap / 2 + bw : 0)) + between * (parts.Count - 1);
        using var bmp = new Bitmap(width, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            // Fundo quase transparente: invisível, mas recebe o clique direito em toda a faixa.
            g.Clear(Color.FromArgb(1, 0, 0, 0));
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float x = pad;
            foreach (var p in parts)
            {
                using var brush = new SolidBrush(p.color);
                float gy = (h - glyphFont.GetHeight(g)) / 2f + h * 0.02f;
                float ty = (h - textFont.GetHeight(g)) / 2f;
                g.DrawString(p.glyph, glyphFont, brush, x, gy, StringFormat.GenericTypographic);
                x += p.gw + gap;
                g.DrawString(p.text, textFont, brush, x, ty, StringFormat.GenericTypographic);
                x += p.tw;
                if (p.charging)
                {
                    using var bolt = new SolidBrush(boltColor);
                    x += gap / 2;
                    g.DrawString("", glyphFont, bolt, x, gy, StringFormat.GenericTypographic);
                    x += bw;
                }
                x += between;
            }
        }

        var pos = new Point(rightEdge - width, bar.Top);
        Push(bmp, pos);
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // TOPMOST, sem mover/redimensionar/ativar
        _lastH = h; _lastLight = light; _width = width; _lastPos = pos;
    }

    private static string Glyph(DeviceKind k) => k switch
    {
        DeviceKind.Mouse => "",
        DeviceKind.Headset => "",
        DeviceKind.Controller => "",
        DeviceKind.Keyboard => "",
        _ => "",
    };

    // Encontra a barra de tarefas principal e a borda esquerda da área de notificação (onde fica a setinha ^).
    private static bool TryGetTaskbar(out Rectangle bar, out int rightEdge)
    {
        bar = Rectangle.Empty; rightEdge = 0;
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out RECT tr)) return false;
        bar = Rectangle.FromLTRB(tr.Left, tr.Top, tr.Right, tr.Bottom);
        if (bar.Width < bar.Height) return false; // barra vertical: não suportado nesta versão

        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && GetWindowRect(notify, out RECT nr) && nr.Right > nr.Left)
            rightEdge = nr.Left;
        else
            rightEdge = bar.Right - (int)(bar.Height * 5.5); // estimativa se a janela da bandeja não for encontrada
        return true;
    }

    private static bool IsLightTaskbar()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    private static bool FontExists(string name)
    {
        using var f = new Font(name, 10f);
        return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);
    }

    // Copia o bitmap com transparência por pixel para a janela.
    private void Push(Bitmap bmp, Point pos)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = SelectObject(mem, hbmp);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = pos.X, y = pos.Y };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            bool ok = UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2);
            if (Environment.GetEnvironmentVariable("BATTERYTRAY_DEBUG") == "1")
            {
                bmp.Save(Path.Combine(AppContext.BaseDirectory, "strip-debug.png"));
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "strip-debug.log"),
                    $"{DateTime.Now:HH:mm:ss} ok={ok} err={Marshal.GetLastWin32Error()} pos={pos} size={bmp.Size}\n");
            }
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(hbmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _follow.Dispose();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pd, ref SIZE ps, IntPtr src, ref POINT pp, int key, ref BLENDFUNCTION bf, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
