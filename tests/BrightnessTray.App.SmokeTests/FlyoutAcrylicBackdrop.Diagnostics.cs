using Microsoft.UI.Composition.SystemBackdrops;

namespace BrightnessTray.App;

internal sealed partial class FlyoutAcrylicBackdrop
{
    internal bool IsInputActive => _isInputActive;
    internal bool IsConnected => _controller is not null;
    internal SystemBackdropTheme? Theme => _configuration?.Theme;

}
