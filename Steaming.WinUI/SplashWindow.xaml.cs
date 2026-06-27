using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Steaming.Core;
using Windows.Graphics;

namespace Steaming.WinUI;

public sealed partial class SplashWindow : Window
{
    private const int SplashW = 520;
    private const int SplashH = 390;
    private bool _dragSetup;

    public SplashWindow()
    {
        InitializeComponent();
        LoadLogo();
        VersionText.Text = VersionInfo.DisplayVersion;
        ConfigureWindow();
        Activated += OnFirstActivated;
    }

    // Load the logo from the app.ico that sits next to the exe. Done in code-behind (not via an
    // ms-appx markup Source) because that URI is not indexed in the published/unpackaged build and a
    // failed parse there took down the whole splash window before anything showed. Any failure here
    // must be swallowed: the splash — and therefore startup — must never crash over a missing icon.
    private void LoadLogo()
    {
        try
        {
            var icoPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (System.IO.File.Exists(icoPath))
                LogoImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(icoPath));
        }
        catch { }
    }

    private void ConfigureWindow()
    {
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        AppWindow.Resize(new SizeInt32(SplashW, SplashH));

        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        AppWindow.Move(new PointInt32(
            display.WorkArea.X + (display.WorkArea.Width  - SplashW) / 2,
            display.WorkArea.Y + (display.WorkArea.Height - SplashH) / 2));
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_dragSetup) return;
        _dragSetup = true;
        try
        {
            // Make the entire splash draggable — there are no interactive controls here.
            var src = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            src.SetRegionRects(NonClientRegionKind.Caption,
                new[] { new RectInt32(0, 0, SplashW, SplashH) });
        }
        catch { }
    }

    public void SetStatus(string text) =>
        DispatcherQueue.TryEnqueue(() => StatusText.Text = text);
}
