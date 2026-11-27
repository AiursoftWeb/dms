using System.Net;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using Aiursoft.DMS.Contracts;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.TextField;

using ClipboardManager = Android.Content.ClipboardManager;
using Orientation = Android.Widget.Orientation;
namespace Aiursoft.DMS.Lockbox.Android;

[Activity(
    Label = "审批详情",
    Exported = false,
    Theme = "@style/AppTheme")]
public sealed class LockboxDetailActivity : WorkspaceActivity
{
    internal const string ExtraLockboxId = "lockbox_id";
    internal const string ExtraFromHistory = "from_history";
    private const int MenuRefreshId = 201;

    private readonly CancellationTokenSource _lifetime = new();
    private LockboxId _lockboxId;
    private string _draftReason = string.Empty;
    private TextInputEditText? _reasonInput;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        if (!Session.IsAuthenticated && !Session.TryRestoreFromDisk())
        {
            ReturnToLogin();
            return;
        }

        var rawId = Intent?.GetStringExtra(ExtraLockboxId);
        if (string.IsNullOrWhiteSpace(rawId) || !LockboxId.TryParse(rawId, out _lockboxId))
        {
            Toast.MakeText(this, "无效的审批请求 ID。", ToastLength.Short)?.Show();
            Finish();
            return;
        }

        var fromHistory = Intent?.GetBooleanExtra(ExtraFromHistory, false) == true;
        SetupWorkspace(fromHistory ? "审批记录详情" : "审批详情", showBack: true, onPullRefresh: Reload);

        var refresh = Toolbar.Menu!.Add(0, MenuRefreshId, 0, "刷新")!;
        refresh.SetShowAsAction(ShowAsAction.Always);
        refresh.SetIcon(Resource.Drawable.ic_refresh);
        Toolbar.MenuItemClick += (_, args) =>
        {
            if (args.Item?.ItemId == MenuRefreshId) Reload();
        };

        Reload();
    }

    protected override void OnDestroy()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnDestroy();
    }

    private void Reload()
    {
        if (_reasonInput is not null)
        {
            _draftReason = _reasonInput.Text ?? string.Empty;
        }
        _ = RunWorkspaceAsync(LoadDetailCoreAsync);
    }

    private async Task LoadDetailCoreAsync()
    {
        ShowNotice(null);
        var status = await Session.Client!.StatusAsync(_lockboxId, _lifetime.Token);
        RenderStatus(status);
    }

    private void RenderStatus(LockboxStatus status)
    {
        Body.RemoveAllViews();
        _reasonInput = null;

        var detail = status.Details;
        var definition = detail?.Definition ?? "审批请求";
        var friendly = LockboxFormatting.FriendlyWorkflowTitle(definition);
        Toolbar.Subtitle = friendly ?? definition;

        // 1. Summary Hero Card
        AddSurfaceCard(Body, out var summary, topDp: 2, paddingDp: 18);

        var badgeRow = CreateRow(summary);
        var (decBg, decFg) = LockboxFormatting.DecisionColors(status.Decision);
        AddPill(badgeRow, LockboxFormatting.DecisionLabel(status.Decision), decBg, decFg);

        if (detail is not null)
        {
            var (riskBg, riskFg, _) = LockboxFormatting.RiskColors(detail.Risk);
            AddPill(badgeRow, LockboxFormatting.RiskLabel(detail.Risk), riskBg, riskFg);
        }

        if (status.Decision == DecisionState.Pending)
        {
            var expiryText = LockboxFormatting.ExpiryCountdown(status.ExpiresAt);
            var expired = status.ExpiresAt <= DateTime.UtcNow;
            AddPill(badgeRow, expiryText,
                expired ? Resource.Color.danger_container : Resource.Color.neutral_container,
                expired ? Resource.Color.on_danger_container : Resource.Color.on_neutral_container,
                endMarginDp: 0);
        }

        if (friendly is not null)
        {
            AddText(summary, friendly, 21f, Resource.Color.text_primary, bold: true, topDp: 12);
            AddText(summary, definition, 13f, Resource.Color.text_secondary, topDp: 3, mono: true, selectable: true);
        }
        else
        {
            AddText(summary, definition, 20f, Resource.Color.text_primary, bold: true, topDp: 12, selectable: true);
        }

        // Copyable Lockbox ID pill
        var idRow = CreateRow(summary, topDp: 12);
        var idPill = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
            Clickable = true,
            Focusable = true
        };
        idPill.SetGravity(GravityFlags.CenterVertical);
        idPill.Background = RoundedRect(GetColorRes(Resource.Color.surface_variant), 10);
        idPill.SetPadding(Dp(10), Dp(6), Dp(10), Dp(6));

        var idText = new TextView(this) { Text = status.LockboxId.ToString(), TextSize = 11.5f };
        idText.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        idText.SetTextColor(GetColorRes(Resource.Color.text_secondary));
        idPill.AddView(idText, new LinearLayout.LayoutParams(0, -2, 1f));

        var copyIcon = new ImageView(this) { ImportantForAccessibility = ImportantForAccessibility.No };
        copyIcon.SetImageResource(Resource.Drawable.ic_copy);
        copyIcon.ImageTintList = ColorStateList.ValueOf(GetColorRes(Resource.Color.brand_primary));
        idPill.AddView(copyIcon, new LinearLayout.LayoutParams(Dp(16), Dp(16)) { MarginStart = Dp(8) });
        idPill.Click += (_, _) => CopyToClipboard("Lockbox ID", status.LockboxId.ToString());
        idRow.AddView(idPill, new LinearLayout.LayoutParams(-1, -2));

        // Highlighted requester reason callout (if present)
        if (detail is not null && !string.IsNullOrWhiteSpace(detail.Reason))
        {
            var reasonBox = CreateColumn();
            reasonBox.Background = RoundedRect(
                GetColorRes(Resource.Color.brand_container),
                12,
                GetColorRes(Resource.Color.brand_primary));
            reasonBox.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));
            AddText(reasonBox, "申请原因", 11.5f, Resource.Color.on_brand_container, bold: true);
            AddText(reasonBox, detail.Reason, 14f, Resource.Color.on_brand_container, topDp: 4, selectable: true);
            summary.AddView(reasonBox, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(14) });
        }

        if (detail is null) return;

        // 2. Metadata Card
        AddSurfaceCard(Body, out var metaCard, topDp: 12, paddingDp: 16);
        AddText(metaCard, "申请信息", 12f, Resource.Color.text_muted, bold: true);
        AddMetaRow(metaCard, "申请人", status.RequestedBy, first: true);
        AddMetaRow(metaCard, "提交时间", $"{LockboxFormatting.LocalTime(status.RequestedAt)}（{LockboxFormatting.RelativeTime(status.RequestedAt)}）");
        AddMetaRow(metaCard, "过期时间", LockboxFormatting.LocalTime(status.ExpiresAt));
        if (status.DecidedAt is { } decidedAt)
        {
            AddMetaRow(metaCard, "处理人", status.DecidedBy ?? (status.Automatic ? "自动审批" : "系统"));
            AddMetaRow(metaCard, "处理时间", LockboxFormatting.LocalTime(decidedAt));
        }
        if (!string.IsNullOrWhiteSpace(detail.DecisionReason))
        {
            AddMetaRow(metaCard, "审批说明", detail.DecisionReason);
        }

        // 3. Target Scope & Parameters Cards
        var targetCount = 0;
        try
        {
            using var requestDoc = JsonDocument.Parse(detail.Parameters);
            using var targetsDoc = JsonDocument.Parse(detail.TargetMachines);
            var ids = targetsDoc.RootElement.EnumerateArray().Select(t => t.GetGuid()).ToArray();
            targetCount = ids.Length;
            var allMachines = requestDoc.RootElement.TryGetProperty("AllMachines", out var allProp) &&
                              allProp.ValueKind == JsonValueKind.True;

            AddSurfaceCard(Body, out var scopeCard, topDp: 12, paddingDp: 16);
            var scopeHeader = CreateRow(scopeCard);
            AddText(scopeHeader, "目标范围", 12f, Resource.Color.text_muted, bold: true, weight: true);
            var scopeBadge = ids.Length == 0
                ? "无机器目标"
                : $"{(allMachines ? "全部目标 · " : "")}{ids.Length} 台机器";
            AddPill(scopeHeader, scopeBadge, Resource.Color.surface_variant, Resource.Color.text_primary, endMarginDp: 0);

            if (ids.Length == 0)
            {
                AddText(scopeCard, "该操作不直接针对特定主机。", 13.5f, Resource.Color.text_secondary, topDp: 10);
            }
            else
            {
                for (var i = 0; i < ids.Length; i++)
                {
                    var machineId = ids[i];
                    if (i > 0) AddDivider(scopeCard, verticalMarginDp: 8);
                    var machineRow = CreateColumn();
                    if (status.MachineNames?.TryGetValue(machineId, out var hostName) == true)
                    {
                        AddText(machineRow, hostName, 14.5f, Resource.Color.text_primary, bold: true, selectable: true);
                        AddText(machineRow, machineId.ToString(), 11.5f, Resource.Color.text_muted, topDp: 2, mono: true, selectable: true);
                    }
                    else
                    {
                        AddText(machineRow, machineId.ToString(), 13f, Resource.Color.text_primary, mono: true, selectable: true);
                    }
                    scopeCard.AddView(machineRow, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(i == 0 ? 10 : 0) });
                }
            }

            if (requestDoc.RootElement.TryGetProperty("TargetWorkflowId", out var wfProp) &&
                wfProp.ValueKind == JsonValueKind.String && wfProp.TryGetGuid(out var wfId))
            {
                AddMetaRow(scopeCard, "目标 Workflow", wfId.ToString(), mono: true);
            }

            if (requestDoc.RootElement.TryGetProperty("MachineName", out var nameProp) &&
                nameProp.ValueKind == JsonValueKind.String &&
                nameProp.GetString() is { Length: > 0 } proposedName)
            {
                AddMetaRow(scopeCard, "申请机器名称", proposedName);
            }

            // Parameters Card
            var rawInput = requestDoc.RootElement.TryGetProperty("Input", out var inputProp)
                ? inputProp.GetString() ?? string.Empty
                : detail.Parameters;
            var formattedPayload = LockboxFormatting.PrettyFormatPayload(rawInput);

            AddSurfaceCard(Body, out var paramCard, topDp: 12, paddingDp: 16);
            var paramHeader = CreateRow(paramCard);
            AddText(paramHeader, "请求参数", 12f, Resource.Color.text_muted, bold: true, weight: true);

            var copyBtn = new MaterialButton(this, null, global::Android.Resource.Attribute.BorderlessButtonStyle)
            {
                Text = "复制",
                TextSize = 12.5f
            };
            copyBtn.SetAllCaps(false);
            copyBtn.SetTextColor(GetColorRes(Resource.Color.brand_primary));
            copyBtn.SetMinWidth(0);
            copyBtn.SetMinimumWidth(0);
            copyBtn.SetPadding(Dp(10), Dp(2), Dp(10), Dp(2));
            copyBtn.Click += (_, _) => CopyToClipboard("请求参数", formattedPayload);
            paramHeader.AddView(copyBtn, new LinearLayout.LayoutParams(-2, Dp(32)));

            var codeScroll = new HorizontalScrollView(this) { HorizontalScrollBarEnabled = true };
            codeScroll.Background = RoundedRect(GetColorRes(Resource.Color.surface_variant), 12);
            codeScroll.SetPadding(Dp(12), Dp(12), Dp(12), Dp(12));
            var codeView = new TextView(this) { Text = formattedPayload, TextSize = 12.5f };
            codeView.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
            codeView.SetTextColor(GetColorRes(Resource.Color.text_primary));
            codeView.SetTextIsSelectable(true);
            codeScroll.AddView(codeView, new ViewGroup.LayoutParams(-2, -2));
            paramCard.AddView(codeScroll, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(8) });
        }
        catch
        {
            AddSurfaceCard(Body, out var fallback, topDp: 12, paddingDp: 16);
            AddText(fallback, "请求详情解析失败，请点击右上角刷新重试。", 13.5f, Resource.Color.on_danger_container);
            return;
        }

        // 4. Decision Card
        if (status.Decision == DecisionState.Pending)
        {
            if (status.CanApprove == false)
            {
                AddSurfaceCard(Body, out var noPerm, topDp: 12, paddingDp: 16);
                AddText(noPerm, "当前账号无法审批此请求", 15f, Resource.Color.text_primary, bold: true);
                AddText(noPerm, "可能是风险等级超出授权范围，或该操作要求独立审批人（不可自批）。", 13.5f, Resource.Color.text_secondary, topDp: 4);
                return;
            }

            AddSurfaceCard(Body, out var decisionCard, topDp: 14, paddingDp: 18);
            AddText(decisionCard, "审批决定", 12f, Resource.Color.text_muted, bold: true);
            AddText(decisionCard, "核对目标机器和参数后做出决定。", 13.5f, Resource.Color.text_secondary, topDp: 4);

            var reasonLayout = new TextInputLayout(this)
            {
                Hint = "审批说明（拒绝时必填，同意时可选）",
                BoxBackgroundMode = TextInputLayout.BoxBackgroundOutline
            };
            reasonLayout.SetBoxCornerRadii(Dp(12), Dp(12), Dp(12), Dp(12));
            _reasonInput = new TextInputEditText(this)
            {
                Text = _draftReason,
                InputType = InputTypes.ClassText | InputTypes.TextFlagMultiLine
            };
            _reasonInput.SetMinLines(2);
            _reasonInput.SetTextColor(GetColorRes(Resource.Color.text_primary));
            reasonLayout.AddView(_reasonInput, new LinearLayout.LayoutParams(-1, -2));
            decisionCard.AddView(reasonLayout, new LinearLayout.LayoutParams(-1, -2) { TopMargin = Dp(12) });

            var actionRow = CreateRow(decisionCard, topDp: 14);
            AddActionButton(actionRow, "拒绝", () => SubmitDecision(approve: false, detail, targetCount),
                danger: true, topDp: 0, weight: true, endMarginDp: 10);
            AddActionButton(actionRow, "同意批准", () => SubmitDecision(approve: true, detail, targetCount),
                primary: true, topDp: 0, weight: true);
        }
    }

    private void SubmitDecision(bool approve, LockboxDetails detail, int targetCount)
    {
        var reason = _reasonInput?.Text?.Trim() ?? string.Empty;
        _draftReason = reason;

        if (!approve && string.IsNullOrWhiteSpace(reason))
        {
            ShowNotice("拒绝请求时请填写审批说明，以便申请人了解原因。", isError: true);
            return;
        }

        if (approve && detail.Risk is RiskLevel.High or RiskLevel.VeryHigh)
        {
            var title = LockboxFormatting.FriendlyWorkflowTitle(detail.Definition) ?? detail.Definition;
            new MaterialAlertDialogBuilder(this)
                .SetTitle($"确认批准{LockboxFormatting.RiskLabel(detail.Risk)}操作？")!
                .SetMessage($"操作：{title}\n定义：{detail.Definition}\n目标机器：{targetCount} 台\n\n批准后将立即下发执行，请确认无误。")!
                .SetNegativeButton("取消", (_, _) => { })!
                .SetPositiveButton("确认批准", (_, _) => ExecuteDecision(approve: true, reason))!
                .Show();
            return;
        }

        ExecuteDecision(approve, reason);
    }

    private void ExecuteDecision(bool approve, string reason)
    {
        _ = RunWorkspaceAsync(async () =>
        {
            try
            {
                await Session.Client!.DecideAsync(_lockboxId, approve, reason, _lifetime.Token);
                _draftReason = string.Empty;
                SetResult(Result.Ok);
                var updated = await Session.Client!.StatusAsync(_lockboxId, _lifetime.Token);
                RenderStatus(updated);
                ShowNotice(approve ? "已同意该审批请求。" : "已拒绝该审批请求。");
            }
            catch (LockboxHttpException ex) when (ex.Status == HttpStatusCode.Conflict)
            {
                // Retain draft and reload fresh status so reviewer sees what changed.
                var updated = await Session.Client!.StatusAsync(_lockboxId, _lifetime.Token);
                RenderStatus(updated);
                ShowNotice("该请求状态刚刚已发生变化（已自动刷新最新状态，你的草稿已保留）。", isError: true);
            }
        });
    }

    private void AddMetaRow(LinearLayout parent, string label, string value, bool first = false, bool mono = false)
    {
        if (!first) AddDivider(parent, verticalMarginDp: 10);
        else
        {
            var spacer = new View(this);
            parent.AddView(spacer, new LinearLayout.LayoutParams(-1, Dp(8)));
        }

        AddText(parent, label, 12f, Resource.Color.text_muted);
        AddText(parent, value, 14.5f, Resource.Color.text_primary, topDp: 3, mono: mono, selectable: true);
    }

    private void CopyToClipboard(string label, string text)
    {
        var clipboard = (ClipboardManager?)GetSystemService(ClipboardService);
        if (clipboard is null) return;
        clipboard.PrimaryClip = ClipData.NewPlainText(label, text);
        Toast.MakeText(this, $"已复制{label}", ToastLength.Short)?.Show();
    }
}
