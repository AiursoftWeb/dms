using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using AndroidX.DrawerLayout.Widget;
using AndroidX.SwipeRefreshLayout.Widget;
using Aiursoft.DMS.Lockbox.Android.Oidc;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.Dialog;

using OperationCanceledException = System.OperationCanceledException;
using Orientation = Android.Widget.Orientation;
namespace Aiursoft.DMS.Lockbox.Android;

public abstract class WorkspaceActivity : AppCompatActivity
{
    internal AppSession Session => ((LockboxApplication)Application!).Session;
    protected MaterialToolbar Toolbar { get; private set; } = null!;
    protected SwipeRefreshLayout SwipeRefresh { get; private set; } = null!;
    protected LinearLayout Body { get; private set; } = null!;
    protected LinearLayout? DrawerBody { get; private set; }
    protected DrawerLayout? Drawer { get; private set; }
    protected TextView NoticeBanner { get; private set; } = null!;

    private ProgressBar _progress = null!;
    private bool _busy;
    protected bool IsBusy => _busy;

    protected override void OnResume()
    {
        base.OnResume();
        if (!Session.IsAuthenticated && !Session.TryRestoreFromDisk())
        {
            ReturnToLogin();
        }
    }

    protected void SetupWorkspace(string title, bool showBack = false, bool enableDrawer = false, Action? onPullRefresh = null)
    {
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(GetColorRes(Resource.Color.app_bar));
        root.SetOnApplyWindowInsetsListener(new SystemBarInsetListener(0, 0, 0, 0, applyTop: true, applyBottom: true, applyIme: true));

        Toolbar = new MaterialToolbar(this) { Title = title };
        Toolbar.SetBackgroundColor(GetColorRes(Resource.Color.app_bar));
        Toolbar.SetTitleTextColor(GetColorRes(Resource.Color.on_app_bar));
        Toolbar.SetSubtitleTextColor(GetColorRes(Resource.Color.on_app_bar_subtle));
        root.AddView(Toolbar, new LinearLayout.LayoutParams(-1, Dp(56)));

        _progress = new ProgressBar(this, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal)
        {
            Indeterminate = true,
            Visibility = ViewStates.Invisible
        };
        _progress.IndeterminateTintList = ColorStateList.ValueOf(GetColorRes(Resource.Color.app_bar_accent));
        root.AddView(_progress, new LinearLayout.LayoutParams(-1, Dp(3)));

        SwipeRefresh = new SwipeRefreshLayout(this);
        SwipeRefresh.SetBackgroundColor(GetColorRes(Resource.Color.surface_canvas));
        SwipeRefresh.SetColorSchemeColors(GetColorRes(Resource.Color.brand_primary));
        SwipeRefresh.SetProgressBackgroundColorSchemeColor(GetColorRes(Resource.Color.surface));
        if (onPullRefresh is not null)
        {
            SwipeRefresh.Refresh += (_, _) =>
            {
                if (_busy)
                {
                    SwipeRefresh.Refreshing = false;
                    return;
                }
                onPullRefresh();
            };
        }
        else
        {
            SwipeRefresh.Enabled = false;
        }

        var scroll = new ScrollView(this) { FillViewport = true, VerticalScrollBarEnabled = false };
        scroll.SetBackgroundColor(GetColorRes(Resource.Color.surface_canvas));
        var container = new LinearLayout(this) { Orientation = Orientation.Vertical };
        container.SetPadding(Dp(18), Dp(14), Dp(18), Dp(28));

        NoticeBanner = new TextView(this)
        {
            TextSize = 13.5f,
            Visibility = ViewStates.Gone,
            AccessibilityLiveRegion = AccessibilityLiveRegion.Polite
        };
        NoticeBanner.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));
        var noticeLp = new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(12) };
        container.AddView(NoticeBanner, noticeLp);

        Body = new LinearLayout(this) { Orientation = Orientation.Vertical };
        container.AddView(Body, new LinearLayout.LayoutParams(-1, -2));

        scroll.AddView(container, new ViewGroup.LayoutParams(-1, -2));
        SwipeRefresh.AddView(scroll, new ViewGroup.LayoutParams(-1, -1));
        root.AddView(SwipeRefresh, new LinearLayout.LayoutParams(-1, 0, 1));

        if (enableDrawer)
        {
            Drawer = new DrawerLayout(this);
            Drawer.AddView(root, new DrawerLayout.LayoutParams(-1, -1));

            var navScroll = new ScrollView(this) { FillViewport = true };
            navScroll.SetBackgroundColor(GetColorRes(Resource.Color.surface));
            DrawerBody = new LinearLayout(this) { Orientation = Orientation.Vertical };
            DrawerBody.SetPadding(Dp(16), Dp(44), Dp(16), Dp(24));
            navScroll.AddView(DrawerBody, new ViewGroup.LayoutParams(-1, -2));

            Drawer.AddView(navScroll, new DrawerLayout.LayoutParams(Dp(304), -1) { Gravity = (int)GravityFlags.Start });
            SetContentView(Drawer);

            Toolbar.SetNavigationIcon(Resource.Drawable.ic_menu);
            Toolbar.NavigationContentDescription = "打开导航菜单";
            Toolbar.NavigationClick += (_, _) => Drawer.OpenDrawer((int)GravityFlags.Start);
        }
        else
        {
            SetContentView(root);
            if (showBack)
            {
                Toolbar.SetNavigationIcon(Resource.Drawable.ic_arrow_back);
                Toolbar.NavigationContentDescription = "返回";
                Toolbar.NavigationClick += (_, _) => Finish();
            }
        }

        WindowCompat.GetInsetsController(Window!, root)!.AppearanceLightStatusBars = false;
    }

    protected async Task RunWorkspaceAsync(Func<Task> action, Action? onErrorRefresh = null)
    {
        if (_busy) return;
        _busy = true;
        _progress.Visibility = ViewStates.Visible;
        try
        {
            await action();
        }
        catch (LockboxAuthRequiredException ex)
        {
            Toast.MakeText(this, ex.Message, ToastLength.Long)?.Show();
            ReturnToLogin();
        }
        catch (LockboxHttpException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            Toast.MakeText(this, LockboxFormatting.FriendlyError(ex), ToastLength.Long)?.Show();
            Session.SignOut();
            ReturnToLogin();
        }
        catch (OperationCanceledException)
        {
            // Activity destroyed or cancelled.
        }
        catch (Exception ex)
        {
            ShowNotice(LockboxFormatting.FriendlyError(ex), isError: true);
            onErrorRefresh?.Invoke();
        }
        finally
        {
            _busy = false;
            _progress.Visibility = ViewStates.Invisible;
            SwipeRefresh.Refreshing = false;
            OnBusyStateChanged();
        }
    }

    protected virtual void OnBusyStateChanged() { }

    protected void ShowNotice(string? text, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            NoticeBanner.Visibility = ViewStates.Gone;
            NoticeBanner.Text = string.Empty;
            return;
        }

        NoticeBanner.Text = text;
        NoticeBanner.SetTextColor(GetColorRes(isError ? Resource.Color.on_danger_container : Resource.Color.on_brand_container));
        NoticeBanner.Background = RoundedRect(
            GetColorRes(isError ? Resource.Color.danger_container : Resource.Color.brand_container),
            12);
        NoticeBanner.Visibility = ViewStates.Visible;
    }

    protected void ReturnToLogin()
    {
        var intent = new Intent(this, typeof(LoginActivity));
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
        StartActivity(intent);
        Finish();
    }

    protected void ConfirmSignOut()
    {
        new MaterialAlertDialogBuilder(this)
            .SetTitle("退出登录")!
            .SetMessage("退出后将停止值班提醒并清除本机保存的加密会话（不会撤销服务器上的个人 API Key）。")!
            .SetNegativeButton("取消", (_, _) => { })!
            .SetPositiveButton("退出登录", (_, _) =>
            {
                Session.SignOut();
                ReturnToLogin();
            })!
            .Show();
    }

    protected void ShowAccountPermissionsDialog()
    {
        var user = Session.User;
        if (user is null)
        {
            Toast.MakeText(this, "尚未加载账号权限信息，请先刷新。", ToastLength.Short)?.Show();
            return;
        }

        var lines = new List<string>
        {
            $"账号：{user.UserName}",
            $"服务器：{Session.Server?.AbsoluteUri.TrimEnd('/')}",
            $"登录方式：{Session.AuthKindLabel}",
            $"角色：{(user.Roles.Count > 0 ? string.Join("、", user.Roles) : "无")}",
            $"可自批请求：{(user.CanApproveOwnRequests ? "是" : "否")}",
            "",
            "审批权限范围："
        };

        var approvable = user.RiskPermissions
            .Where(rp => rp.CanApprove)
            .Select(rp => LockboxFormatting.RiskLabel(rp.Risk))
            .ToArray();
        lines.Add(approvable.Length > 0 ? "• 可审批风险等级：" + string.Join(" / ", approvable) : "• 当前无审批权限");

        new MaterialAlertDialogBuilder(this)
            .SetTitle("当前账号与权限")!
            .SetMessage(string.Join('\n', lines))!
            .SetPositiveButton("知道了", (_, _) => { })!
            .Show();
    }

    protected int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density + 0.5f);

    protected Color GetColorRes(int colorResId) => new(Resources!.GetColor(colorResId, Theme));

    protected GradientDrawable RoundedRect(Color fill, int radiusDp, Color? stroke = null, int strokeDp = 1)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(fill);
        drawable.SetCornerRadius(Dp(radiusDp));
        if (stroke is { } s)
        {
            drawable.SetStroke(Dp(strokeDp), s);
        }
        return drawable;
    }

    protected LinearLayout CreateColumn() => new(this) { Orientation = Orientation.Vertical };

    protected LinearLayout CreateRow(ViewGroup parent, int topDp = 0)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        parent.AddView(row, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(topDp) });
        return row;
    }

    protected MaterialCardView AddSurfaceCard(ViewGroup parent, out LinearLayout content, int topDp = 12, int paddingDp = 16, Action? onClick = null)
    {
        var card = new MaterialCardView(this)
        {
            Radius = Dp(18),
            CardElevation = Dp(1),
            StrokeWidth = Dp(1),
            StrokeColor = GetColorRes(Resource.Color.outline),
            Clickable = onClick is not null,
            Focusable = onClick is not null
        };
        card.SetCardBackgroundColor(GetColorRes(Resource.Color.surface));
        content = CreateColumn();
        content.SetPadding(Dp(paddingDp), Dp(paddingDp), Dp(paddingDp), Dp(paddingDp));
        card.AddView(content, new ViewGroup.LayoutParams(-1, -2));
        if (onClick is not null)
        {
            card.Click += (_, _) => { if (!_busy) onClick(); };
        }
        parent.AddView(card, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(topDp) });
        return card;
    }

    protected TextView AddText(
        ViewGroup parent,
        string value,
        float sp,
        int colorRes,
        bool bold = false,
        int topDp = 0,
        bool mono = false,
        bool selectable = false,
        bool weight = false)
    {
        var view = new TextView(this) { Text = value, TextSize = sp };
        view.SetTextColor(GetColorRes(colorRes));
        if (mono)
        {
            view.SetTypeface(Typeface.Monospace, bold ? TypefaceStyle.Bold : TypefaceStyle.Normal);
        }
        else if (bold)
        {
            view.SetTypeface(null, TypefaceStyle.Bold);
        }
        if (selectable)
        {
            view.SetTextIsSelectable(true);
        }
        var lp = weight
            ? new LinearLayout.LayoutParams(0, -2, 1f) { TopMargin = Dp(topDp) }
            : new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(topDp) };
        parent.AddView(view, lp);
        return view;
    }

    protected TextView AddPill(ViewGroup parent, string label, int bgColorRes, int fgColorRes, int endMarginDp = 8)
    {
        var pill = new TextView(this)
        {
            Text = label,
            TextSize = 11.5f
        };
        pill.SetTypeface(null, TypefaceStyle.Bold);
        pill.SetTextColor(GetColorRes(fgColorRes));
        pill.Background = RoundedRect(GetColorRes(bgColorRes), 999);
        pill.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
        var lp = new LinearLayout.LayoutParams(-2, -2) { MarginEnd = Dp(endMarginDp) };
        parent.AddView(pill, lp);
        return pill;
    }

    protected View AddDivider(ViewGroup parent, int verticalMarginDp = 10)
    {
        var divider = new View(this);
        divider.SetBackgroundColor(GetColorRes(Resource.Color.outline_subtle));
        parent.AddView(divider, new LinearLayout.LayoutParams(-1, Dp(1))
        {
            TopMargin = Dp(verticalMarginDp),
            BottomMargin = Dp(verticalMarginDp)
        });
        return divider;
    }

    protected MaterialButton AddActionButton(
        ViewGroup parent,
        string label,
        Action onClick,
        bool primary = false,
        bool danger = false,
        int topDp = 10,
        bool weight = false,
        int endMarginDp = 0)
    {
        var button = new MaterialButton(this)
        {
            Text = label,
            CornerRadius = Dp(14)
        };
        button.SetAllCaps(false);
        button.TextSize = 14.5f;
        button.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));

        if (primary)
        {
            button.BackgroundTintList = ColorStateList.ValueOf(GetColorRes(Resource.Color.brand_primary));
            button.SetTextColor(GetColorRes(Resource.Color.on_brand_primary));
            button.SetTypeface(null, TypefaceStyle.Bold);
        }
        else if (danger)
        {
            button.BackgroundTintList = ColorStateList.ValueOf(GetColorRes(Resource.Color.danger_container));
            button.SetTextColor(GetColorRes(Resource.Color.on_danger_container));
            button.StrokeWidth = Dp(1);
            button.StrokeColor = ColorStateList.ValueOf(GetColorRes(Resource.Color.stripe_high));
            button.SetTypeface(null, TypefaceStyle.Bold);
        }
        else
        {
            button.BackgroundTintList = ColorStateList.ValueOf(GetColorRes(Resource.Color.surface_variant));
            button.SetTextColor(GetColorRes(Resource.Color.text_primary));
            button.StrokeWidth = Dp(1);
            button.StrokeColor = ColorStateList.ValueOf(GetColorRes(Resource.Color.outline));
        }

        var lp = weight
            ? new LinearLayout.LayoutParams(0, -2, 1f) { TopMargin = Dp(topDp), MarginEnd = Dp(endMarginDp) }
            : new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(topDp), MarginEnd = Dp(endMarginDp) };
        parent.AddView(button, lp);
        button.Click += (_, _) => { if (!_busy) onClick(); };
        return button;
    }

    protected void AddDrawerNavItem(LinearLayout parent, int iconRes, string label, bool selected, Action onClick)
    {
        var row = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
            Clickable = true,
            Focusable = true
        };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(14), Dp(12), Dp(14), Dp(12));
        if (selected)
        {
            row.Background = RoundedRect(GetColorRes(Resource.Color.brand_container), 14);
        }

        var icon = new ImageView(this) { ImportantForAccessibility = ImportantForAccessibility.No };
        icon.SetImageResource(iconRes);
        icon.ImageTintList = ColorStateList.ValueOf(GetColorRes(selected ? Resource.Color.on_brand_container : Resource.Color.text_secondary));
        row.AddView(icon, new LinearLayout.LayoutParams(Dp(20), Dp(20)) { MarginEnd = Dp(14) });

        var text = new TextView(this) { Text = label, TextSize = 14.5f };
        text.SetTextColor(GetColorRes(selected ? Resource.Color.on_brand_container : Resource.Color.text_primary));
        if (selected) text.SetTypeface(null, TypefaceStyle.Bold);
        row.AddView(text, new LinearLayout.LayoutParams(0, -2, 1f));

        row.Click += (_, _) => onClick();
        parent.AddView(row, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(4) });
    }
}
