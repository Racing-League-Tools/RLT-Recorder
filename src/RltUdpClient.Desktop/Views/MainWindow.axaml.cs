using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using RltUdpClient.Desktop.ViewModels;

namespace RltUdpClient.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => ApplyDarkTitleBar();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel)
                viewModel.PickFolder = PickFolderAsync;
        };
    }

    private async Task<string?> PickFolderAsync(string startFrom)
    {
        var storage = StorageProvider;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where should recordings be saved?",
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetStartFolderAsync(storage, startFrom),
        });

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    /// <summary>Opens the picker where the current folder is, when it still exists.</summary>
    private static async Task<IStorageFolder?> TryGetStartFolderAsync(IStorageProvider storage, string path)
    {
        try
        {
            return Directory.Exists(path) ? await storage.TryGetFolderFromPathAsync(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Paints the system title bar black to match the main Racing League Tools
    /// window. Windows 11 only; elsewhere the default chrome is left alone,
    /// which costs nothing but a slightly lighter bar.
    /// </summary>
    private void ApplyDarkTitleBar()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;

        var handle = TryGetPlatformHandle()?.Handle;
        if (handle is null || handle == IntPtr.Zero)
            return;

        try
        {
            Dwm.SetDarkTitleBar(handle.Value);
        }
        catch (DllNotFoundException)
        {
            // Not Windows after all; the window works regardless.
        }
    }
}

[SupportedOSPlatform("windows")]
internal static class Dwm
{
    private const int UseImmersiveDarkMode = 20;
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    // DWM colours are COLORREF: 0x00BBGGRR, not RGB.
    private const int Black = 0x00000000;
    private const int Line = 0x002B2B2B;
    private const int Text = 0x00E4E4E4;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static void SetDarkTitleBar(IntPtr window)
    {
        var enabled = 1;
        Set(window, UseImmersiveDarkMode, ref enabled);

        var black = Black;
        Set(window, CaptionColor, ref black);

        var line = Line;
        Set(window, BorderColor, ref line);

        var text = Text;
        Set(window, TextColor, ref text);
    }

    private static void Set(IntPtr window, int attribute, ref int value) =>
        DwmSetWindowAttribute(window, attribute, ref value, sizeof(int));
}
