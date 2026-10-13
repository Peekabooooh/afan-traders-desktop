using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using AfanTraders.Core;

namespace AfanTraders.App;

/// <summary>Everything the screens share: data, sync engines, cost toggle and refresh events.</summary>
sealed class AppServices
{
    public static AppServices Current { get; private set; } = null!;

    public Store Store { get; }
    public CloudSync Cloud { get; }
    public LanSync Lan { get; }
    public ISecretStore Secrets { get; }

    /// <summary>Cost prices are hidden every time the app starts.</summary>
    public bool ShowCost { get => showCost; set { showCost = value; CostChanged?.Invoke(); } }
    bool showCost;

    /// <summary>Data changed (always raised on the UI thread).</summary>
    public event Action? Refreshed;
    public event Action? CostChanged;
    public event Action? SyncChanged;

    public AppServices(string? folder)
    {
        Current = this;
        Store = new Store(folder);
        Secrets = new DpapiSecrets(Path.Combine(Store.DataFolder, "secrets.dat"));
        Cloud = new CloudSync(Store, Secrets);
        Lan = new LanSync(Store, Secrets, Cloud.DeviceId);
        var ui = Application.Current.Dispatcher;
        Store.DataChanged += () => ui.BeginInvoke(() => Refreshed?.Invoke());
        Cloud.StatusChanged += () => ui.BeginInvoke(() => SyncChanged?.Invoke());
        Lan.StatusChanged += () => ui.BeginInvoke(() => SyncChanged?.Invoke());
    }

    public void Shutdown()
    {
        try { Lan.Dispose(); } catch { }
        try { Cloud.Dispose(); } catch { }
        Store.Dispose();
    }
}

/// <summary>Sign-in token and office code, encrypted for this Windows user (DPAPI).</summary>
sealed class DpapiSecrets : ISecretStore
{
    readonly string path;
    readonly object gate = new();

    public DpapiSecrets(string path) => this.path = path;

    Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(path)) return new();
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new();
        }
        catch { return new(); }
    }

    void Save(Dictionary<string, string> d) =>
        File.WriteAllBytes(path, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(d), null, DataProtectionScope.CurrentUser));

    public string? Get(string account) { lock (gate) return Load().GetValueOrDefault(account); }
    public void Set(string account, string value) { lock (gate) { var d = Load(); d[account] = value; Save(d); } }
    public void Delete(string account) { lock (gate) { var d = Load(); if (d.Remove(account)) Save(d); } }
}

static class Images
{
    public static BitmapImage? Load(byte[]? data)
    {
        if (data is not { Length: > 0 }) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.StreamSource = new MemoryStream(data);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    /// <summary>Product photos as JPEG, at most 600 px (small database and sync). Logos keep PNG.</summary>
    public static byte[]? Compress(byte[] data, int maxSide = 600, bool keepTransparency = false)
    {
        try
        {
            var src = BitmapFrame.Create(new MemoryStream(data), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            double scale = Math.Min(1.0, (double)maxSide / Math.Max(src.PixelWidth, src.PixelHeight));
            BitmapSource img = scale < 1 ? new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale)) : src;
            BitmapEncoder enc;
            if (keepTransparency) enc = new PngBitmapEncoder();
            else
            {
                // JPEG has no transparency: put the picture on white.
                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
                    dc.DrawImage(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
                }
                var rt = new RenderTargetBitmap(img.PixelWidth, img.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rt.Render(dv);
                img = rt;
                enc = new JpegBitmapEncoder { QualityLevel = 72 };
            }
            enc.Frames.Add(BitmapFrame.Create(img));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    public static byte[]? Pick(int maxSide = 600, bool keepTransparency = false)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All files|*.*" };
        if (dlg.ShowDialog() != true) return null;
        var data = Compress(File.ReadAllBytes(dlg.FileName), maxSide, keepTransparency);
        if (data == null) Ui.Message("That file could not be read as a picture.");
        return data;
    }
}
