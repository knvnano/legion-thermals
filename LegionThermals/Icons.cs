// Рисование ОДНОГО значка трея штатным System.Drawing для Windows.
// На прозрачном холсте 32×32 CPU занимает верхнюю половину, GPU — нижнюю.
// Нативный HICON — ресурс Windows, его нужно уничтожить через DestroyIcon,
// даже если создание значка Avalonia завершилось ошибкой.
// Остальные Bitmap/Graphics/Font/Brush/Stream освобождаются через using.

using System.Drawing;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Avalonia.Controls;
namespace LegionThermals;

internal static class Icons
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    // One notification icon: CPU above GPU, with the same colors as the banner.
    // Создаём прозрачное изображение и отдаём Avalonia независимую копию через ICO-поток.
    public static WindowIcon Temperatures(int? cpu, int? gpu)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        Draw(cpu, 0, Color.FromArgb(82, 216, 250));
        Draw(gpu, 16, Color.FromArgb(255, 181, 93));
        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            using var stream = new MemoryStream();
            icon.Save(stream);
            stream.Position = 0;
            return new WindowIcon(stream);
        }
        finally { DestroyIcon(handle); }

        void Draw(int? value, int y, Color color)
        {
            using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            graphics.DrawString(value?.ToString() ?? "—", font, brush, new RectangleF(0, y, 32, 16), format);
        }
    }
}
