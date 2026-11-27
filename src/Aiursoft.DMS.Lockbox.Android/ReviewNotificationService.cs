using System.Net;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Aiursoft.DMS.Lockbox.Android.Oidc;
using Cancelled = System.OperationCanceledException;

namespace Aiursoft.DMS.Lockbox.Android;

[Service(Name = "com.aiursoft.dms.lockbox.ReviewNotificationService", Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class ReviewNotificationService : Service
{
    internal const string OpenQueue = "com.aiursoft.dms.lockbox.OPEN_QUEUE";
    private const string Stop = "com.aiursoft.dms.lockbox.STOP_NOTIFICATIONS";
    private const string SessionChannel = "lockbox-session";
    private const string ReviewChannel = "lockbox-review";
    private const int SessionNotification = 1;
    private const int ReviewNotification = 2;
    private CancellationTokenSource? _session;
    internal static bool Active { get; private set; }

    internal static void Start(Context context)
    {
        using var intent = new Intent(context, typeof(ReviewNotificationService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
        else context.StartService(intent);
    }

    internal static void StopListening(Context context)
    {
        using var intent = new Intent(context, typeof(ReviewNotificationService));
        context.StopService(intent);
        ((NotificationManager?)context.GetSystemService(NotificationService))?.Cancel(ReviewNotification);
    }

    internal static bool CanNotify(Context context)
    {
        var notifications = (NotificationManager?)context.GetSystemService(NotificationService);
        if (notifications is null) return false;
        if (OperatingSystem.IsAndroidVersionAtLeast(24) && !notifications.AreNotificationsEnabled()) return false;
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return true;
        using var channel = notifications.GetNotificationChannel(ReviewChannel);
        return channel?.Importance != NotificationImportance.None;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == Stop)
        {
            StopListening(this);
            return StartCommandResult.NotSticky;
        }
        if (_session is not null) return StartCommandResult.NotSticky;
        CreateChannels();
        using var foreground = Build(SessionChannel, "Lockbox 值班提醒已开启", "正在监听 DMS 待审请求；下班后可随时停止。", ongoing: true);
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(SessionNotification, foreground, ForegroundService.TypeSpecialUse);
        else
            StartForeground(SessionNotification, foreground);
        _session = new CancellationTokenSource();
        Active = true;
        _ = ListenAsync(_session.Token);
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        Active = false;
        _session?.Cancel();
        _session?.Dispose();
        base.OnDestroy();
    }

    private async Task ListenAsync(CancellationToken token)
    {
        try
        {
            var session = ((LockboxApplication)Application!).Session;
            if (!session.IsAuthenticated && !session.TryRestoreFromDisk()) return;
            var client = session.Client;
            if (client is null) return;

            DateTime? cursor = null;
            var delay = 1;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var update = await client.WaitForUpdateAsync(cursor, token);
                    if (token.IsCancellationRequested) return;
                    delay = 1;
                    if (update.LatestPendingAt is not { } latest || latest <= cursor) continue;
                    cursor = latest;
                    Notify("有 Lockbox 请求待你审批", "点击打开 DMS Lockbox 查看待审列表。");
                }
                catch (LockboxAuthRequiredException)
                {
                    if (!token.IsCancellationRequested)
                        Notify("Lockbox 值班提醒已停止", "登录会话已过期，请打开应用重新登录。");
                    return;
                }
                catch (LockboxHttpException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    if (!token.IsCancellationRequested)
                        Notify("Lockbox 值班提醒已停止", "请检查 DMS 登录状态和审批权限。");
                    return;
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), token);
                    delay = Math.Min(30, delay * 2);
                }
            }
        }
        catch (Cancelled) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!token.IsCancellationRequested) Notify("Lockbox 值班提醒已停止", "无法读取本机登录会话，请重新登录 DMS。");
        }
        finally { if (!token.IsCancellationRequested) StopSelf(); }
    }

    private void CreateChannels()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        using var session = new NotificationChannel(SessionChannel, "Lockbox 值班状态", NotificationImportance.Low);
        using var review = new NotificationChannel(ReviewChannel, "Lockbox 待审提醒", NotificationImportance.High)
        { LockscreenVisibility = NotificationVisibility.Public };
        manager.CreateNotificationChannel(session);
        manager.CreateNotificationChannel(review);
    }

    private void Notify(string title, string text)
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        using var notification = Build(ReviewChannel, title, text, ongoing: false);
        manager.Notify(ReviewNotification, notification);
    }

    private Notification Build(string channel, string title, string text, bool ongoing)
    {
        using var open = new Intent(this, typeof(MainActivity)).SetAction(OpenQueue)!
            .AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop)!;
        using var launch = PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
#pragma warning disable CS0618
        using var builder = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? new Notification.Builder(this, channel)
            : new Notification.Builder(this);
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            builder.SetPriority(ongoing ? (int)NotificationPriority.Low : (int)NotificationPriority.High);
            if (!ongoing) builder.SetDefaults(NotificationDefaults.All);
        }
#pragma warning restore CS0618
        builder.SetContentTitle(title);
        builder.SetContentText(text);
        builder.SetSmallIcon(Resource.Drawable.ic_lockbox);
        builder.SetOngoing(ongoing);
        builder.SetAutoCancel(!ongoing);
        builder.SetContentIntent(launch);
        builder.SetVisibility(NotificationVisibility.Public);
        if (ongoing)
        {
            using var stop = new Intent(this, typeof(ReviewNotificationService)).SetAction(Stop)!;
            using var stopPending = PendingIntent.GetService(this, 1, stop, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
            using var action = new Notification.Action.Builder(null, new Java.Lang.String("停止提醒"), stopPending).Build();
            builder.AddAction(action);
        }
        return builder.Build()!;
    }
}
