using System.Runtime.Versioning;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Notipet.Windows;

// Desktop acrylic for the pop-ups that follows notipet's theme and stays
// acrylic while the window is inactive.
//
// The stock DesktopAcrylicBackdrop tracks window activation, and a pop-up is
// never activated (it must not steal focus): so it always drew the inactive
// fallback, a solid colour taken from the Windows theme. With notipet set to
// Light on a dark Windows that was dark grey under dark text - unreadable.
// Here the configuration is ours: always "active", themed from the window's
// own content, and updated when that theme changes.
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class ThemedAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private FrameworkElement? _root;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        if (!DesktopAcrylicController.IsSupported()) return;

        _root = xamlRoot.Content as FrameworkElement;
        _configuration = new SystemBackdropConfiguration { IsInputActive = true, Theme = Map(_root?.ActualTheme) };
        if (_root is not null) _root.ActualThemeChanged += OnThemeChanged;

        _controller = new DesktopAcrylicController();
        _controller.AddSystemBackdropTarget(target);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        base.OnTargetDisconnected(target);
        if (_root is not null) _root.ActualThemeChanged -= OnThemeChanged;
        _root = null;
        if (_controller is null) return;
        _controller.RemoveSystemBackdropTarget(target);
        _controller.Dispose();
        _controller = null;
    }

    private void OnThemeChanged(FrameworkElement sender, object args)
    {
        if (_configuration is not null) _configuration.Theme = Map(sender.ActualTheme);
    }

    private static SystemBackdropTheme Map(ElementTheme? theme) => theme switch
    {
        ElementTheme.Light => SystemBackdropTheme.Light,
        ElementTheme.Dark => SystemBackdropTheme.Dark,
        _ => SystemBackdropTheme.Default
    };
}
