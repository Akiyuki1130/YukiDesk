using System.Windows;
using System.Windows.Controls;

namespace DesktopBar.UI
{
    public sealed class CapsuleBorder : Border
    {
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            CornerRadius = new CornerRadius(sizeInfo.NewSize.Height / 2);
            base.OnRenderSizeChanged(sizeInfo);
        }
    }
}
