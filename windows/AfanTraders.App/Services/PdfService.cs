using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf;

namespace AfanTraders.App;

/// <summary>Shows and prints PDFs with Windows' own PDF engine (Windows.Data.Pdf).</summary>
static class PdfService
{
    /// <summary>Renders every page to a bitmap (dpi = 96 for screen, 300 for printing).
    /// Runs on a background thread: Windows' PDF objects must not live on the screen thread,
    /// or cleaning them up when the app closes can crash.</summary>
    public static Task<List<BitmapSource>> RenderPages(byte[] pdf, double dpi) => Task.Run(() => Render(pdf, dpi));

    static async Task<List<BitmapSource>> Render(byte[] pdf, double dpi)
    {
        var pages = new List<BitmapSource>();
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(pdf.AsBuffer());
        input.Seek(0);
        var doc = await WinPdf.PdfDocument.LoadFromStreamAsync(input);
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.GetPage(i);
            using var output = new InMemoryRandomAccessStream();
            var options = new WinPdf.PdfPageRenderOptions
            {
                DestinationWidth = (uint)(page.Size.Width / 96.0 * dpi),
                DestinationHeight = (uint)(page.Size.Height / 96.0 * dpi),
            };
            await page.RenderToStreamAsync(output, options);
            output.Seek(0);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = output.AsStream();
            bmp.EndInit();
            bmp.Freeze();
            pages.Add(bmp);
        }
        return pages;
    }

    public static async Task Print(byte[] pdf, string title)
    {
        var dlg = new PrintDialog { UserPageRangeEnabled = false };
        if (dlg.ShowDialog() != true) return;
        var images = await RenderPages(pdf, 300);
        var doc = new FixedDocument();
        double w = dlg.PrintableAreaWidth, h = dlg.PrintableAreaHeight;
        doc.DocumentPaginator.PageSize = new Size(w, h);
        foreach (var img in images)
        {
            var page = new FixedPage { Width = w, Height = h };
            var picture = new Image { Source = img, Width = w, Height = h, Stretch = Stretch.Uniform };
            page.Children.Add(picture);
            var content = new PageContent();
            ((System.Windows.Markup.IAddChild)content).AddChild(page);
            doc.Pages.Add(content);
        }
        dlg.PrintDocument(doc.DocumentPaginator, title);
    }

    public static void Save(byte[] pdf, string fileName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = fileName, Filter = "PDF document|*.pdf", DefaultExt = ".pdf" };
        if (dlg.ShowDialog() == true) File.WriteAllBytes(dlg.FileName, pdf);
    }
}

/// <summary>Preview window with Print and Save buttons.</summary>
sealed class PdfPreviewWindow : Window
{
    public PdfPreviewWindow(byte[] pdf, string title, string fileName)
    {
        Title = title;
        Width = 900; Height = 1000;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = Application.Current.MainWindow;
        Background = Ui.Hex("#E6E9EE");
        var pages = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 16) };
        var footer = Ui.Footer(
            new UIElement[] { Ui.Row(Ui.Icon("printer", 26), Ui.Text("  " + title, 15, FontWeights.SemiBold)) },
            new UIElement[]
            {
                Ui.Btn("Save PDF…", () => PdfService.Save(pdf, fileName)),
                Ui.Btn("Print…", () => _ = PdfService.Print(pdf, fileName), primary: true),
                Ui.Btn("Close", Close),
            });
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Top);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = pages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        Loaded += async (_, _) =>
        {
            try
            {
                foreach (var img in await PdfService.RenderPages(pdf, 110))
                    pages.Children.Add(new Border
                    {
                        Margin = new Thickness(0, 0, 0, 16), Background = Brushes.White,
                        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.25 },
                        Child = new Image { Source = img, Width = 794, Stretch = Stretch.Uniform },
                    });
            }
            catch (Exception e) { pages.Children.Add(Ui.Text("Could not show the preview: " + e.Message, 14, null, Ui.Red, TextWrapping.Wrap)); }
        };
    }
}
