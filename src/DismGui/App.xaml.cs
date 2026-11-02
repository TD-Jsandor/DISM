using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DismGui.Services;

namespace DismGui;

public partial class App : Application
{
    internal static SessionLog Log { get; private set; } = null!;
    protected override void OnStartup(StartupEventArgs e)
    {
        try { Log = new SessionLog(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Cannot create the session log. The app cannot start without logging.\n{ex.Message}", "DISM Studio");
            Shutdown(1);
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            try { Log.Write("FATAL", args.Exception.ToString()); } catch { }
            MessageBox.Show($"An unexpected error occurred. Review the log:\n{Log.FilePath}\n\n{args.Exception.Message}", "DISM Studio");
            // Do not continue servicing after an unhandled UI exception.
            Shutdown(1);
        };
        if (e.Args is ["--render-preview", var target])
        {
            // Offscreen layout smoke check; never launches DISM or requests elevation.
            base.OnStartup(e);
            try
            {
                var window = new MainWindow();
                var surface = (FrameworkElement)window.Content;
                if (surface is Panel panel) panel.Background = window.Background;
                TextElement.SetForeground(surface, window.Foreground);
                TextElement.SetFontFamily(surface, window.FontFamily);
                TextElement.SetFontSize(surface, window.FontSize);
                window.Content = null;
                surface.Measure(new Size(window.Width, window.Height));
                surface.Arrange(new Rect(0, 0, window.Width, window.Height));
                surface.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(surface);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(target);
                png.Save(stream);
                Log.Write("INFO", $"Offscreen layout preview saved: {target}");
                Shutdown(0);
            }
            catch (Exception ex) { Log.Write("ERROR", ex.ToString()); Shutdown(1); }
            return;
        }
        base.OnStartup(e);
        new MainWindow().Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        Log?.Dispose();
        base.OnExit(e);
    }
}
