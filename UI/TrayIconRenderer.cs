using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using KSnakeBatteryTray.Models;
using KSnakeBatteryTray.Settings;
using Microsoft.Win32;

namespace KSnakeBatteryTray.UI;

public static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            var val = key?.GetValue("SystemUsesLightTheme");
            return val is int i && i == 1;
        }
        catch
        {
            return false;
        }
    }

    public static Color ResolveAccentColor(BatteryReading reading, bool lightTaskbar)
    {
        if (!reading.IsConnected || reading.BatteryPercent is null)
            return lightTaskbar ? Color.FromArgb(113, 113, 122) : Color.FromArgb(161, 161, 170);

        if (reading.IsCharging)
            return lightTaskbar ? Color.FromArgb(2, 132, 199) : Color.FromArgb(56, 189, 248);

        int pct = reading.BatteryPercent.Value;
        // Critical low battery always stays red for immediate visibility
        if (pct <= 15)
            return lightTaskbar ? Color.FromArgb(220, 38, 38) : Color.FromArgb(255, 82, 82);

        if (!string.IsNullOrWhiteSpace(reading.CustomAccentHex))
        {
            try
            {
                return ColorTranslator.FromHtml(reading.CustomAccentHex);
            }
            catch
            {
                // Ignore invalid custom hex
            }
        }

        if (pct <= 20)
            return lightTaskbar ? Color.FromArgb(220, 38, 38) : Color.FromArgb(255, 82, 82);
        if (pct <= 49)
            return lightTaskbar ? Color.FromArgb(217, 119, 6) : Color.FromArgb(251, 191, 36);

        return lightTaskbar ? Color.FromArgb(22, 163, 74) : Color.FromArgb(74, 222, 128);
    }

    public static Icon CreateIcon(BatteryReading reading, TrayIconStyle style, bool showTypeBadge = false)
    {
        bool lightTaskbar = IsLightTaskbar();
        int size = Math.Max(32, SystemInformation.SmallIconSize.Width);

        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        Color accent = ResolveAccentColor(reading, lightTaskbar);
        Color fg = lightTaskbar ? Color.FromArgb(20, 20, 24) : Color.FromArgb(255, 255, 255);
        Color muted = lightTaskbar ? Color.FromArgb(113, 113, 122) : Color.FromArgb(161, 161, 170);

        switch (style)
        {
            case TrayIconStyle.BatteryIconOnly:
                DrawBatteryIconOnly(g, size, reading, accent, fg, muted);
                break;

            case TrayIconStyle.NumberOnly:
                DrawNumberOnly(g, size, reading, accent, muted);
                break;

            case TrayIconStyle.BatteryAndNumber:
            default:
                DrawBatteryAndNumber(g, size, reading, accent, fg, muted);
                break;
        }

        if (showTypeBadge && reading.IsConnected)
        {
            DrawDeviceTypeBadge(g, size, reading.Kind, accent, lightTaskbar);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void DrawBatteryAndNumber(
        Graphics g, int size, BatteryReading reading, Color accent, Color fg, Color muted)
    {
        string text = FormatShortText(reading);
        Color textColor = reading.IsConnected && reading.BatteryPercent is not null
            ? (reading.BatteryPercent <= 20 && !reading.IsCharging ? accent : fg)
            : muted;

        float barHeight = Math.Max(5f, size * 0.21f);
        float barY = size - barHeight;
        float barWidth = size;

        float textAreaHeight = barY - Math.Max(2f, size * 0.08f);
        var textArea = new RectangleF(0.5f, 0f, size - 1f, textAreaHeight);
        DrawTightText(g, text, textArea, textColor);

        using var trackBrush = new SolidBrush(Color.FromArgb(95, muted));
        FillRoundedRect(g, trackBrush, 0f, barY, barWidth, barHeight, barHeight / 2f);

        if (reading.BatteryPercent is int pct)
        {
            float fillRatio = Math.Clamp(pct / 100f, 0.14f, 1f);
            using var fillBrush = new SolidBrush(accent);
            FillRoundedRect(g, fillBrush, 0f, barY, barWidth * fillRatio, barHeight, barHeight / 2f);
        }

        if (reading.IsCharging)
        {
            float dot = Math.Max(6f, size * 0.22f);
            using var dotBrush = new SolidBrush(accent);
            g.FillEllipse(dotBrush, size - dot, 0f, dot, dot);
        }
    }

    private static void DrawNumberOnly(
        Graphics g, int size, BatteryReading reading, Color accent, Color muted)
    {
        string text = FormatShortText(reading);
        Color color = reading.IsConnected && reading.BatteryPercent is not null ? accent : muted;

        var area = new RectangleF(size * 0.02f, size * 0.04f, size * 0.96f, size * 0.92f);
        DrawTightText(g, text, area, color);
    }

    private static void DrawBatteryIconOnly(
        Graphics g, int size, BatteryReading reading, Color accent, Color fg, Color muted)
    {
        Color borderColor = reading.IsConnected ? fg : muted;
        float penWidth = Math.Max(2.2f, size * 0.085f);

        float capW = Math.Max(3f, size * 0.11f);
        float capH = size * 0.34f;
        float bodyX = penWidth / 2f;
        float bodyW = size - capW - penWidth * 1.1f;
        float bodyH = size * 0.68f;
        float bodyY = (size - bodyH) / 2f;

        float capX = bodyX + bodyW + penWidth * 0.3f;
        float capY = (size - capH) / 2f;
        using var capBrush = new SolidBrush(borderColor);
        FillRoundedRect(g, capBrush, capX, capY, capW, capH, 2f);

        if (reading.BatteryPercent is int pct && pct > 0)
        {
            float pad = penWidth + 1.2f;
            float innerMaxW = bodyW - pad * 2f;
            float innerH = bodyH - pad * 2f;
            float fillW = Math.Max(3f, innerMaxW * Math.Clamp(pct / 100f, 0f, 1f));

            using var fillBrush = new SolidBrush(accent);
            FillRoundedRect(g, fillBrush, bodyX + pad, bodyY + pad, fillW, innerH, 2f);
        }

        using var pen = new Pen(borderColor, penWidth);
        DrawRoundedRect(g, pen, bodyX, bodyY, bodyW, bodyH, 4.5f);

        if (reading.IsCharging)
        {
            float cx = bodyX + bodyW * 0.5f;
            float cy = bodyY + bodyH * 0.5f;
            float s = size * 0.16f;
            PointF[] bolt =
            [
                new(cx + s * 0.25f, cy - s * 1.55f),
                new(cx - s * 1.0f, cy + s * 0.1f),
                new(cx - s * 0.05f, cy + s * 0.1f),
                new(cx - s * 0.3f, cy + s * 1.55f),
                new(cx + s * 1.0f, cy - s * 0.15f),
                new(cx + s * 0.05f, cy - s * 0.15f)
            ];
            using var boltBrush = new SolidBrush(Color.White);
            using var boltOutline = new Pen(Color.FromArgb(220, 0, 0, 0), 1.2f);
            g.FillPolygon(boltBrush, bolt);
            g.DrawPolygon(boltOutline, bolt);
        }
    }

    private static void DrawDeviceTypeBadge(Graphics g, int size, DeviceKind kind, Color accent, bool lightTaskbar)
    {
        // Compact high-contrast badge in top-left corner so multiple tray icons are easy to tell apart.
        float badgeSize = Math.Max(9f, size * 0.31f);
        var bgColor = lightTaskbar ? Color.FromArgb(235, 250, 250, 252) : Color.FromArgb(235, 18, 20, 26);
        var fgColor = accent;

        using var bgBrush = new SolidBrush(bgColor);
        g.FillEllipse(bgBrush, 0f, 0f, badgeSize, badgeSize);

        using var pen = new Pen(fgColor, Math.Max(1.3f, size * 0.045f));
        using var brush = new SolidBrush(fgColor);

        float cx = badgeSize / 2f;
        float cy = badgeSize / 2f;
        float r = badgeSize * 0.28f;

        switch (kind)
        {
            case DeviceKind.Mouse:
                // Small mouse oval
                g.FillEllipse(brush, cx - r * 0.8f, cy - r * 1.05f, r * 1.6f, r * 2.1f);
                break;

            case DeviceKind.Audio:
                // Mini headphone arc
                g.DrawArc(pen, cx - r, cy - r * 0.8f, r * 2f, r * 1.9f, 180, 180);
                g.FillRectangle(brush, cx - r - 0.6f, cy, r * 0.55f, r * 0.9f);
                g.FillRectangle(brush, cx + r * 0.45f, cy, r * 0.55f, r * 0.9f);
                break;

            case DeviceKind.Keyboard:
                // Mini keycap rectangle
                g.FillRectangle(brush, cx - r, cy - r * 0.7f, r * 2f, r * 1.4f);
                break;

            default:
                // Diamond
                PointF[] diamond = [new(cx, cy - r), new(cx + r, cy), new(cx, cy + r), new(cx - r, cy)];
                g.FillPolygon(brush, diamond);
                break;
        }
    }

    private static void DrawTightText(Graphics g, string text, RectangleF targetRect, Color color)
    {
        if (string.IsNullOrEmpty(text) || targetRect.Width <= 1f || targetRect.Height <= 1f)
            return;

        string fontFamilyName = text.Length >= 3 ? "Bahnschrift" : "Segoe UI";
        FontFamily family;
        try
        {
            family = new FontFamily(fontFamilyName);
        }
        catch
        {
            family = FontFamily.GenericSansSerif;
        }

        using (family)
        using (var path = new GraphicsPath())
        using (var sf = StringFormat.GenericTypographic)
        {
            var style = family.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
            path.AddString(text, family, (int)style, 64f, PointF.Empty, sf);

            var bounds = path.GetBounds();
            if (bounds.Width <= 0.1f || bounds.Height <= 0.1f)
                return;

            float scaleX = targetRect.Width / bounds.Width;
            float scaleY = targetRect.Height / bounds.Height;

            float sx, sy;
            if (text.Length >= 3 && scaleX < scaleY)
            {
                sx = scaleX;
                sy = Math.Min(scaleY, scaleX * 1.22f);
            }
            else
            {
                float s = Math.Min(scaleX, scaleY);
                sx = s;
                sy = s;
            }

            float finalW = bounds.Width * sx;
            float finalH = bounds.Height * sy;
            float offsetX = targetRect.X + (targetRect.Width - finalW) / 2f - bounds.X * sx;
            float offsetY = targetRect.Y + (targetRect.Height - finalH) / 2f - bounds.Y * sy;

            using var matrix = new Matrix();
            matrix.Translate(offsetX, offsetY);
            matrix.Scale(sx, sy);
            path.Transform(matrix);

            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
    }

    private static string FormatShortText(BatteryReading reading)
    {
        if (reading.Mode == ConnectionMode.Disconnected)
            return "--";
        if (reading.Mode == ConnectionMode.Sleep24G)
            return "Zzz";
        if (reading.BatteryPercent is null)
            return "?";
        return reading.BatteryPercent.Value.ToString();
    }

    private static void FillRoundedRect(Graphics g, Brush brush, float x, float y, float w, float h, float r)
    {
        if (w <= 0 || h <= 0) return;
        using var path = CreateRoundedPath(x, y, w, h, Math.Min(r, Math.Min(w, h) / 2f));
        g.FillPath(brush, path);
    }

    private static void DrawRoundedRect(Graphics g, Pen pen, float x, float y, float w, float h, float r)
    {
        using var path = CreateRoundedPath(x, y, w, h, Math.Min(r, Math.Min(w, h) / 2f));
        g.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedPath(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        float d = r * 2f;
        if (d <= 0.5f)
        {
            path.AddRectangle(new RectangleF(x, y, w, h));
            return path;
        }

        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
