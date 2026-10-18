using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;

namespace PocketDesk;

/// <summary>可调透明度的 Acrylic 系统背景。DesktopAcrylicBackdrop 没有 tint 属性，必须用 controller。</summary>
public sealed class AdjustableAcrylicBackdrop : SystemBackdrop
{
    private readonly Windows.UI.Color _tintColor;
    private readonly float _tintOpacity;
    private readonly float _luminosityOpacity;
    private DesktopAcrylicController? _controller;

    public AdjustableAcrylicBackdrop(Windows.UI.Color tintColor, double tintOpacity, double luminosityOpacity)
    {
        _tintColor = tintColor;
        _tintOpacity = (float)Math.Clamp(tintOpacity, 0.02, 1.0);
        _luminosityOpacity = (float)Math.Clamp(luminosityOpacity, 0.02, 1.0);
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        _controller = new DesktopAcrylicController
        {
            TintColor = _tintColor,
            TintOpacity = _tintOpacity,
            LuminosityOpacity = _luminosityOpacity,
            FallbackColor = _tintColor,
        };
        _controller.AddSystemBackdropTarget(connectedTarget);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop connectedTarget)
    {
        if (_controller is null)
        {
            return;
        }

        _controller.RemoveSystemBackdropTarget(connectedTarget);
        _controller.Dispose();
        _controller = null;
    }
}
