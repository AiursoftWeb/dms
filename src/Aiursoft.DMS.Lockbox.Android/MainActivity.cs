using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using Aiursoft.DMS.Contracts;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.Chip;

using Orientation = Android.Widget.Orientation;
namespace Aiursoft.DMS.Lockbox.Android;

[Activity(
    Label = "@string/app_name",
    Exported = false,
    LaunchMode = LaunchMode.SingleTop,
    Theme = "@style/AppTheme")]
public sealed class MainActivity : WorkspaceActivity
{
    private const int NotificationPermissionRequestCode = 1001;
    private const int DetailActivityRequestCode = 2001;
    private const int MenuOnCallId = 101;
    private const int MenuRefreshId = 102;

    private enum ViewMode { Queue, History }

    private readonly CancellationTokenSource _lifetime = new();
    private readonly DeferredReviewNavigation _notificationNavigation = new();

    private ViewMode _mode = ViewMode.Queue;
    private int _page;
    private RiskLevel? _selectedRisk;
    private LockboxReviewPage? _queuePage;
    private LockboxHistoryPage? _historyPage;
    private IMenuItem? _onCallMenuItem;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (!Session.IsAuthenticated && !Session.TryRestoreFromDisk())
        {
            ReturnToLogin();
            return;
        }

        SetupWorkspace("待我审批", enableDrawer: true, onPullRefresh: ReloadCurrent);
        SetupToolbarMenu();
        RebuildDrawer();

        if (Intent?.Action == ReviewNotificationService.OpenQueue)
        {
            _mode = ViewMode.Queue;
            _page = 0;
        }

        _ = LoadInitialAsync();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (!Session.IsAuthenticated) return;
        UpdateOnCallIndicators();
        RebuildDrawer();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        if (intent?.Action != ReviewNotificationService.OpenQueue) return;
        _notificationNavigation.Request();
        TryConsumeNotificationNavigation();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == DetailActivityRequestCode && resultCode == Result.Ok)
        {
            ReloadCurrent();
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != NotificationPermissionRequestCode) return;
        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            StartOnCallNotifications();
        }
        else
        {
            ShowNotice("未授予通知权限，无法开启值班提醒。请在系统设置中允许通知。", isError: true);
        }
    }

    protected override void OnDestroy()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnDestroy();
    }

    protected override void OnBusyStateChanged()
    {
        TryConsumeNotificationNavigation();
    }

    private void SetupToolbarMenu()
    {
        var menu = Toolbar.Menu!;
        _onCallMenuItem = menu.Add(0, MenuOnCallId, 0, "值班提醒")!;
        _onCallMenuItem.SetShowAsAction(ShowAsAction.Always);
        _onCallMenuItem.SetIcon(ReviewNotificationService.Active
            ? Resource.Drawable.ic_notifications_active
            : Resource.Drawable.ic_notifications_idle);

        var refresh = menu.Add(0, MenuRefreshId, 1, "刷新")!;
        refresh.SetShowAsAction(ShowAsAction.Always);
        refresh.SetIcon(Resource.Drawable.ic_refresh);

        Toolbar.MenuItemClick += (_, args) =>
        {
            if (args.Item is null) return;
            switch (args.Item.ItemId)
            {
                case MenuOnCallId:
                    ToggleOnCallNotifications();
                    break;
                case MenuRefreshId:
                    ReloadCurrent();
                    break;
            }
        };
    }

    private void UpdateOnCallIndicators()
    {
        var active = ReviewNotificationService.Active;
        _onCallMenuItem?.SetIcon(active
            ? Resource.Drawable.ic_notifications_active
            : Resource.Drawable.ic_notifications_idle);
        _onCallMenuItem?.SetTitle(active ? "停止值班提醒" : "开启值班提醒");
    }

    private void RebuildDrawer()
    {
        if (DrawerBody is null) return;
        DrawerBody.RemoveAllViews();

        // Branded Profile Header Card
        var headerCard = new LinearLayout(this) { Orientation = Orientation.Vertical };
        headerCard.Background = Resources!.GetDrawable(Resource.Drawable.brand_emblem_background, Theme);
        headerCard.SetPadding(Dp(18), Dp(18), Dp(18), Dp(18));

        var topRow = CreateRow(headerCard);
        var emblem = new ImageView(this) { ImportantForAccessibility = ImportantForAccessibility.No };
        emblem.SetImageResource(Resource.Drawable.ic_lockbox);
        topRow.AddView(emblem, new LinearLayout.LayoutParams(Dp(28), Dp(28)) { MarginEnd = Dp(10) });

        var brandTitle = new TextView(this) { Text = "DMS Lockbox", TextSize = 17 };
        brandTitle.SetTextColor(Color.White);
        brandTitle.SetTypeface(null, TypefaceStyle.Bold);
        topRow.AddView(brandTitle, new LinearLayout.LayoutParams(0, -2, 1f));

        var userLabel = new TextView(this)
        {
            Text = Session.User?.UserName ?? "已连接",
            TextSize = 15
        };
        userLabel.SetTextColor(Color.White);
        userLabel.SetTypeface(null, TypefaceStyle.Bold);
        headerCard.AddView(userLabel, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(14) });

        var serverHost = new TextView(this)
        {
            Text = $"{Session.Server?.Host ?? ""}  ·  {Session.AuthKindLabel}",
            TextSize = 12
        };
        serverHost.SetTextColor(Color.ParseColor("#CCFBF1"));
        headerCard.AddView(serverHost, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(3) });

        DrawerBody.AddView(headerCard, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(16) });

        // Primary Navigation
        AddText(DrawerBody, "工作区", 11.5f, Resource.Color.text_muted, bold: true, topDp: 4);
        AddDrawerNavItem(DrawerBody, Resource.Drawable.ic_queue, "待我审批", _mode == ViewMode.Queue, () =>
        {
            Drawer?.CloseDrawers();
            if (_mode != ViewMode.Queue || _page != 0)
            {
                _mode = ViewMode.Queue;
                _page = 0;
                _selectedRisk = null;
                ReloadCurrent();
            }
        });

        AddDrawerNavItem(DrawerBody, Resource.Drawable.ic_history, "我的审批记录", _mode == ViewMode.History, () =>
        {
            Drawer?.CloseDrawers();
            if (_mode != ViewMode.History || _page != 0)
            {
                _mode = ViewMode.History;
                _page = 0;
                _selectedRisk = null;
                ReloadCurrent();
            }
        });

        AddDivider(DrawerBody, verticalMarginDp: 14);

        // On-call Monitoring Status Card inside Drawer
        AddText(DrawerBody, "值班监听", 11.5f, Resource.Color.text_muted, bold: true);
        var active = ReviewNotificationService.Active;
        var onCallCard = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            Clickable = true,
            Focusable = true
        };
        onCallCard.Background = RoundedRect(
            GetColorRes(active ? Resource.Color.brand_container : Resource.Color.surface_variant),
            14,
            GetColorRes(active ? Resource.Color.brand_primary : Resource.Color.outline));
        onCallCard.SetPadding(Dp(14), Dp(12), Dp(14), Dp(12));

        var onCallRow = CreateRow(onCallCard);
        var onCallTitle = new TextView(this)
        {
            Text = active ? "● 值班提醒已开启" : "○ 值班提醒未开启",
            TextSize = 14
        };
        onCallTitle.SetTypeface(null, TypefaceStyle.Bold);
        onCallTitle.SetTextColor(GetColorRes(active ? Resource.Color.on_brand_container : Resource.Color.text_primary));
        onCallRow.AddView(onCallTitle, new LinearLayout.LayoutParams(0, -2, 1f));

        var onCallHint = new TextView(this)
        {
            Text = active ? "点击停止后台审批监听" : "点击开启前台长轮询锁屏提醒",
            TextSize = 12
        };
        onCallHint.SetTextColor(GetColorRes(active ? Resource.Color.on_brand_container : Resource.Color.text_secondary));
        onCallCard.AddView(onCallHint, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(4) });
        onCallCard.Click += (_, _) =>
        {
            Drawer?.CloseDrawers();
            ToggleOnCallNotifications();
        };
        DrawerBody.AddView(onCallCard, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(8) });

        AddDivider(DrawerBody, verticalMarginDp: 14);

        // Account & Sign Out
        AddText(DrawerBody, "账号", 11.5f, Resource.Color.text_muted, bold: true);
        AddDrawerNavItem(DrawerBody, Resource.Drawable.ic_person, "账号与审批权限", selected: false, () =>
        {
            Drawer?.CloseDrawers();
            ShowAccountPermissionsDialog();
        });
        AddDrawerNavItem(DrawerBody, Resource.Drawable.ic_logout, "退出登录", selected: false, () =>
        {
            Drawer?.CloseDrawers();
            ConfirmSignOut();
        });
    }

    private async Task LoadInitialAsync()
    {
        await RunWorkspaceAsync(async () =>
        {
            if (Session.User is null)
            {
                await Session.RefreshIdentityAsync(_lifetime.Token);
                RebuildDrawer();
            }
            await FetchCurrentPageCoreAsync();
        });
    }

    private void ReloadCurrent()
    {
        _ = RunWorkspaceAsync(FetchCurrentPageCoreAsync);
    }

    private async Task FetchCurrentPageCoreAsync()
    {
        ShowNotice(null);
        Toolbar.Title = _mode == ViewMode.Queue ? "待我审批" : "我的审批记录";
        Toolbar.Subtitle = _page > 0 ? $"第 {_page + 1} 页" : Session.Server?.Host;

        if (_mode == ViewMode.Queue)
        {
            _queuePage = await Session.Client!.QueueAsync(_page, _lifetime.Token);
        }
        else
        {
            _historyPage = await Session.Client!.HistoryAsync(_page, _lifetime.Token);
        }

        RebuildDrawer();
        RenderCurrentMode();
    }

    private void RenderCurrentMode()
    {
        Body.RemoveAllViews();
        UpdateOnCallIndicators();

        if (ReviewNotificationService.Active)
        {
            RenderOnCallActiveBanner();
        }

        if (_mode == ViewMode.Queue)
        {
            RenderQueue();
        }
        else
        {
            RenderHistory();
        }
    }

    private void RenderOnCallActiveBanner()
    {
        var banner = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        banner.SetGravity(GravityFlags.CenterVertical);
        banner.Background = RoundedRect(
            GetColorRes(Resource.Color.brand_container),
            14,
            GetColorRes(Resource.Color.brand_primary));
        banner.SetPadding(Dp(14), Dp(10), Dp(10), Dp(10));

        var label = new TextView(this)
        {
            Text = "● 值班提醒监听中 · 有新待审将推送通知",
            TextSize = 13
        };
        label.SetTypeface(null, TypefaceStyle.Bold);
        label.SetTextColor(GetColorRes(Resource.Color.on_brand_container));
        banner.AddView(label, new LinearLayout.LayoutParams(0, -2, 1f));

        var stop = new MaterialButton(this, null, global::Android.Resource.Attribute.BorderlessButtonStyle)
        {
            Text = "停止",
            TextSize = 12.5f
        };
        stop.SetAllCaps(false);
        stop.SetTextColor(GetColorRes(Resource.Color.on_brand_container));
        stop.SetMinWidth(0);
        stop.SetMinimumWidth(0);
        stop.SetPadding(Dp(10), Dp(4), Dp(10), Dp(4));
        stop.Click += (_, _) => ToggleOnCallNotifications();
        banner.AddView(stop, new LinearLayout.LayoutParams(-2, Dp(34)));

        Body.AddView(banner, new LinearLayout.LayoutParams(-1, -2) { BottomMargin = Dp(12) });
    }

    private void RenderQueue()
    {
        var entries = _queuePage?.Entries ?? [];

        // Summary header row
        var headerRow = CreateRow(Body, topDp: 2);
        var countText = entries.Count == 0
            ? "当前没有待处理请求"
            : $"共 {entries.Count} 条待审批" + (_queuePage?.HasNextPage == true ? "+" : "");
        AddText(headerRow, countText, 13.5f, Resource.Color.text_secondary, bold: true, weight: true);

        if (entries.Count > 0)
        {
            RenderRiskFilterStrip(entries.Select(e => e.Risk).ToList());
        }

        var filtered = _selectedRisk is { } risk
            ? entries.Where(e => e.Risk == risk).ToList()
            : entries.ToList();

        if (filtered.Count == 0)
        {
            RenderEmptyState(
                _selectedRisk is null ? "暂无待你审批的请求" : $"当前没有「{LockboxFormatting.RiskLabel(_selectedRisk.Value)}」待审请求",
                _selectedRisk is null
                    ? "下拉随时刷新，或开启右上角值班提醒，有新请求将第一时间通知你。"
                    : "可以切换回「全部」查看其它风险等级的待审批。",
                ctaLabel: _selectedRisk is not null
                    ? "查看全部风险等级"
                    : (!ReviewNotificationService.Active ? "开启值班提醒" : null),
                onCta: _selectedRisk is not null
                    ? () => { _selectedRisk = null; RenderCurrentMode(); }
                    : ToggleOnCallNotifications);
        }
        else
        {
            foreach (var row in filtered)
            {
                RenderRequestCard(
                    row.LockboxId,
                    row.Definition,
                    row.Requester,
                    row.Risk,
                    LockboxFormatting.RelativeTime(row.CreatedAt),
                    outcome: null,
                    fromHistory: false);
            }
        }

        RenderPagination(_queuePage?.HasNextPage == true);
    }

    private void RenderHistory()
    {
        var entries = _historyPage?.Entries ?? [];

        var headerRow = CreateRow(Body, topDp: 2);
        var countText = entries.Count == 0
            ? "暂无审批历史"
            : $"本页 {entries.Count} 条处理记录";
        AddText(headerRow, countText, 13.5f, Resource.Color.text_secondary, bold: true, weight: true);

        if (entries.Count > 0)
        {
            RenderRiskFilterStrip(entries.Select(e => e.Risk).ToList());
        }

        var filtered = _selectedRisk is { } risk
            ? entries.Where(e => e.Risk == risk).ToList()
            : entries.ToList();

        if (filtered.Count == 0)
        {
            RenderEmptyState(
                _selectedRisk is null ? "还没有审批记录" : $"本页没有「{LockboxFormatting.RiskLabel(_selectedRisk.Value)}」处理记录",
                "你亲自同意或拒绝的 Lockbox 请求都会记录在这里，方便事后审计与追溯。",
                ctaLabel: _selectedRisk is not null ? "查看全部风险等级" : null,
                onCta: _selectedRisk is not null ? () => { _selectedRisk = null; RenderCurrentMode(); } : null);
        }
        else
        {
            foreach (var entry in filtered)
            {
                RenderRequestCard(
                    entry.LockboxId,
                    entry.Definition,
                    entry.Requester,
                    entry.Risk,
                    "处理于 " + LockboxFormatting.RelativeTime(entry.DecidedAt),
                    outcome: entry.Decision,
                    fromHistory: true);
            }
        }

        RenderPagination(_historyPage?.HasNextPage == true);
    }

    private void RenderRiskFilterStrip(IReadOnlyList<RiskLevel> presentRisks)
    {
        var hsv = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = false };
        var chipGroup = new ChipGroup(this) { SingleSelection = true };
        chipGroup.ChipSpacingHorizontal = Dp(8);

        void AddFilterChip(string label, RiskLevel? target)
        {
            var selected = _selectedRisk == target;
            var chip = new Chip(this)
            {
                Text = label,
                Checkable = true,
                Checked = selected,
                TextSize = 12.5f
            };
            chip.ShapeAppearanceModel = chip.ShapeAppearanceModel.WithCornerSize(Dp(12));
            chip.ChipBackgroundColor = ColorStateList.ValueOf(GetColorRes(
                selected ? Resource.Color.brand_container : Resource.Color.surface));
            chip.SetTextColor(GetColorRes(selected ? Resource.Color.on_brand_container : Resource.Color.text_secondary));
            chip.ChipStrokeWidth = Dp(1);
            chip.ChipStrokeColor = ColorStateList.ValueOf(GetColorRes(
                selected ? Resource.Color.brand_primary : Resource.Color.outline));
            chip.Click += (_, _) =>
            {
                _selectedRisk = target;
                RenderCurrentMode();
            };
            chipGroup.AddView(chip);
        }

        AddFilterChip($"全部 ({presentRisks.Count})", null);
        foreach (var level in new[] { RiskLevel.VeryHigh, RiskLevel.High, RiskLevel.Medium, RiskLevel.Low, RiskLevel.Safe })
        {
            var count = presentRisks.Count(r => r == level);
            if (count > 0 || _selectedRisk == level)
            {
                AddFilterChip($"{LockboxFormatting.RiskShortLabel(level)} ({count})", level);
            }
        }

        hsv.AddView(chipGroup);
        Body.AddView(hsv, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(10), BottomMargin = Dp(4) });
    }

    private void RenderRequestCard(
        LockboxId id,
        string definition,
        string requester,
        RiskLevel risk,
        string timeLabel,
        DecisionState? outcome,
        bool fromHistory)
    {
        var (riskBg, riskFg, stripeColor) = LockboxFormatting.RiskColors(risk);
        var card = new MaterialCardView(this)
        {
            Radius = Dp(18),
            CardElevation = Dp(1),
            StrokeWidth = Dp(1),
            StrokeColor = GetColorRes(Resource.Color.outline),
            Clickable = true,
            Focusable = true
        };
        card.SetCardBackgroundColor(GetColorRes(Resource.Color.surface));
        card.Click += (_, _) =>
        {
            if (IsBusy) return;
            OpenDetail(id, fromHistory);
        };

        var outerRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };

        // Left 4dp semantic risk accent stripe
        var stripe = new View(this);
        stripe.SetBackgroundColor(GetColorRes(stripeColor));
        outerRow.AddView(stripe, new LinearLayout.LayoutParams(Dp(4), -1));

        var content = CreateColumn();
        content.SetPadding(Dp(15), Dp(14), Dp(14), Dp(14));

        // Top row: Risk badge + optional Decision badge + Relative time right-aligned
        var topRow = CreateRow(content);
        AddPill(topRow, LockboxFormatting.RiskLabel(risk), riskBg, riskFg);
        if (outcome is { } decision)
        {
            var (decBg, decFg) = LockboxFormatting.DecisionColors(decision);
            AddPill(topRow, LockboxFormatting.DecisionLabel(decision), decBg, decFg);
        }
        var spacer = new View(this);
        topRow.AddView(spacer, new LinearLayout.LayoutParams(0, 1, 1f));
        var timeView = new TextView(this) { Text = timeLabel, TextSize = 12 };
        timeView.SetTextColor(GetColorRes(Resource.Color.text_muted));
        topRow.AddView(timeView, new LinearLayout.LayoutParams(-2, -2));

        // Title: Friendly label (if known) + technical definition
        var friendly = LockboxFormatting.FriendlyWorkflowTitle(definition);
        if (friendly is not null)
        {
            AddText(content, friendly, 16.5f, Resource.Color.text_primary, bold: true, topDp: 10);
            AddText(content, definition, 12.5f, Resource.Color.text_secondary, topDp: 2, mono: true);
        }
        else
        {
            AddText(content, definition, 16f, Resource.Color.text_primary, bold: true, topDp: 10);
        }

        // Bottom row: Requester + Chevron
        var bottomRow = CreateRow(content, topDp: 10);
        var personIcon = new ImageView(this) { ImportantForAccessibility = ImportantForAccessibility.No };
        personIcon.SetImageResource(Resource.Drawable.ic_person);
        personIcon.ImageTintList = ColorStateList.ValueOf(GetColorRes(Resource.Color.text_muted));
        bottomRow.AddView(personIcon, new LinearLayout.LayoutParams(Dp(16), Dp(16)) { MarginEnd = Dp(6) });

        AddText(bottomRow, requester, 13f, Resource.Color.text_secondary, weight: true);

        var chevron = new ImageView(this) { ImportantForAccessibility = ImportantForAccessibility.No };
        chevron.SetImageResource(Resource.Drawable.ic_chevron_right);
        chevron.ImageTintList = ColorStateList.ValueOf(GetColorRes(Resource.Color.text_muted));
        bottomRow.AddView(chevron, new LinearLayout.LayoutParams(Dp(18), Dp(18)));

        outerRow.AddView(content, new LinearLayout.LayoutParams(0, -2, 1f));
        card.AddView(outerRow, new ViewGroup.LayoutParams(-1, -2));
        card.ContentDescription = $"{friendly ?? definition}，{requester}，{LockboxFormatting.RiskLabel(risk)}，{timeLabel}";

        Body.AddView(card, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(10) });
    }

    private void RenderEmptyState(string title, string subtitle, string? ctaLabel = null, Action? onCta = null)
    {
        AddSurfaceCard(Body, out var box, topDp: 14, paddingDp: 28);
        box.SetGravity(GravityFlags.CenterHorizontal);

        var iconWrap = new FrameLayout(this);
        iconWrap.Background = RoundedRect(GetColorRes(Resource.Color.brand_container), 999);
        var icon = new ImageView(this) { ImportantForAccessibility = ImportantForAccessibility.No };
        icon.SetImageResource(Resource.Drawable.ic_lockbox);
        icon.ImageTintList = ColorStateList.ValueOf(GetColorRes(Resource.Color.on_brand_container));
        iconWrap.AddView(icon, new FrameLayout.LayoutParams(Dp(28), Dp(28)) { Gravity = GravityFlags.Center });
        box.AddView(iconWrap, new LinearLayout.LayoutParams(Dp(60), Dp(60)));

        var titleView = AddText(box, title, 17f, Resource.Color.text_primary, bold: true, topDp: 16);
        titleView.Gravity = GravityFlags.Center;

        var subView = AddText(box, subtitle, 13.5f, Resource.Color.text_secondary, topDp: 6);
        subView.Gravity = GravityFlags.Center;
        subView.SetLineSpacing(Dp(3), 1f);

        if (!string.IsNullOrWhiteSpace(ctaLabel) && onCta is not null)
        {
            var btn = AddActionButton(box, ctaLabel, onCta, primary: true, topDp: 18);
            var lp = (LinearLayout.LayoutParams)btn.LayoutParameters!;
            lp.Width = ViewGroup.LayoutParams.WrapContent;
            btn.LayoutParameters = lp;
        }
    }

    private void RenderPagination(bool hasNext)
    {
        if (_page <= 0 && !hasNext) return;
        var row = CreateRow(Body, topDp: 16);
        if (_page > 0)
        {
            AddActionButton(row, "上一页", () =>
            {
                _page--;
                ReloadCurrent();
            }, weight: true, endMarginDp: hasNext ? 10 : 0);
        }
        if (hasNext)
        {
            AddActionButton(row, "下一页", () =>
            {
                _page++;
                ReloadCurrent();
            }, primary: true, weight: true);
        }
    }

    private void OpenDetail(LockboxId id, bool fromHistory)
    {
        var intent = new Intent(this, typeof(LockboxDetailActivity));
        intent.PutExtra(LockboxDetailActivity.ExtraLockboxId, id.ToString());
        intent.PutExtra(LockboxDetailActivity.ExtraFromHistory, fromHistory);
#pragma warning disable CS0618
        StartActivityForResult(intent, DetailActivityRequestCode);
#pragma warning restore CS0618
    }

    private void ToggleOnCallNotifications()
    {
        if (ReviewNotificationService.Active)
        {
            ReviewNotificationService.StopListening(this);
            UpdateOnCallIndicators();
            RebuildDrawer();
            RenderCurrentMode();
            ShowNotice("已停止值班提醒。");
            return;
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Manifest.Permission.PostNotifications], NotificationPermissionRequestCode);
            return;
        }

        StartOnCallNotifications();
    }

    private void StartOnCallNotifications()
    {
        if (!ReviewNotificationService.CanNotify(this))
        {
            ShowNotice("系统通知通道已被关闭，请先在系统设置中开启 DMS Lockbox 的通知权限。", isError: true);
            return;
        }

        ReviewNotificationService.Start(this);
        UpdateOnCallIndicators();
        RebuildDrawer();
        RenderCurrentMode();
        ShowNotice("值班提醒已开启，新待审请求将直接推送到通知栏。");
    }

    private void TryConsumeNotificationNavigation()
    {
        if (!_notificationNavigation.TryConsume(IsBusy, _lifetime.Token)) return;
        if (!Session.IsAuthenticated) return;
        _mode = ViewMode.Queue;
        _page = 0;
        _selectedRisk = null;
        ReloadCurrent();
    }
}
