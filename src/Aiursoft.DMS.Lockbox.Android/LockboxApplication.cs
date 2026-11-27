using Android.App;
using Android.Runtime;

namespace Aiursoft.DMS.Lockbox.Android;

[Application]
public sealed class LockboxApplication(IntPtr handle, JniHandleOwnership transfer) : Application(handle, transfer)
{
    internal AppSession Session { get; private set; } = null!;

    public override void OnCreate()
    {
        base.OnCreate();
        Session = new AppSession(this);
    }
}
