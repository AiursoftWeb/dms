using Android.Views;

namespace Aiursoft.DMS.Lockbox.Android;

internal sealed class SystemBarInsetListener(
    int baseLeft,
    int baseTop,
    int baseRight,
    int baseBottom,
    bool applyTop,
    bool applyBottom,
    bool applyIme = false) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
{
    public WindowInsets OnApplyWindowInsets(View view, WindowInsets insets)
    {
        int left, top, right, bottom;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var mask = WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout();
            if (applyIme) mask |= WindowInsets.Type.Ime();
            var rect = insets.GetInsets(mask);
            left = rect.Left;
            top = rect.Top;
            right = rect.Right;
            bottom = rect.Bottom;
        }
        else
        {
#pragma warning disable CS0618
            left = insets.SystemWindowInsetLeft;
            top = insets.SystemWindowInsetTop;
            right = insets.SystemWindowInsetRight;
            bottom = insets.SystemWindowInsetBottom;
#pragma warning restore CS0618
        }

        view.SetPadding(
            baseLeft + left,
            baseTop + (applyTop ? top : 0),
            baseRight + right,
            baseBottom + (applyBottom ? bottom : 0));
        return insets;
    }
}
