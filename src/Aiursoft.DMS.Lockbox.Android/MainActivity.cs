using System.Net;
using System.Text.Json;
using Aiursoft.DMS.Contracts;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using Google.Android.Material.Dialog;

namespace Aiursoft.DMS.Lockbox.Android;

[Activity(Label = "DMS Lockbox", MainLauncher = true, Exported = true)]
public sealed partial class MainActivity : AppCompatActivity
{
    private readonly CancellationTokenSource _lifetime = new();
    private ConnectionStore _store = null!;
    private LockboxClient? _client;
    private LinearLayout _content = null!;
    private TextView _notice = null!;
    private readonly List<View> _buttons = [];
    private const int NotificationPermissionRequest = 1;
    private bool _busy;
    private readonly DeferredReviewNavigation _notificationNavigation = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
        _store = new ConnectionStore(this);
        try { _client = _store.Load(); }
        catch (Exception)
        {
            ShowSettings("无法读取保存的连接，请重新连接。");
            return;
        }
        if (_client is null) ShowSettings();
        else OpenQueue();
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent?.Action != ReviewNotificationService.OpenQueue || _client is null) return;
        _notificationNavigation.Request();
        OpenQueueFromNotification();
    }

    private void ToggleNotifications()
    {
        if (ReviewNotificationService.Active)
        {
            ReviewNotificationService.StopListening(this);
            Message("已请求停止值班提醒。");
            return;
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([global::Android.Manifest.Permission.PostNotifications], NotificationPermissionRequest);
            return;
        }
        StartNotifications();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != NotificationPermissionRequest || _client is null) return;
        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted) StartNotifications();
        else Message("未开启通知权限。请在 Android 系统设置中允许 DMS Lockbox 通知。");
    }

    private void StartNotifications()
    {
        if (!ReviewNotificationService.CanNotify(this))
        {
            Message("请在 Android 系统设置中允许 DMS Lockbox 及待审提醒通知。");
            return;
        }
        try
        {
            ReviewNotificationService.Start(this);
            Message("已请求开启值班提醒；通知栏会显示运行状态。");
        }
        catch (Java.Lang.IllegalStateException)
        {
            Message("无法开启值班提醒，请在 App 位于前台时重试。");
        }
    }

    protected override void OnDestroy()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnDestroy();
    }

    private void ShowSettings(string notice = "")
    {
        Page("连接设置", "连接到你的 DMS Controller，开始在手机上处理审批。", _client is null ? null : OpenQueue);
        var connection = Section("服务器连接", "仅接受 HTTPS 地址和 DMS 个人 API Key。");
        var server = Field(connection, "DMS 地址（HTTPS）", InputTypes.ClassText | InputTypes.TextVariationUri);
        server.Text = _store.Server ?? "";
        var key = Field(connection, "DMS 个人 API Key", InputTypes.ClassText | InputTypes.TextVariationPassword);
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            key.ImportantForAutofill = ImportantForAutofill.No;
        ActionButton(connection, "验证并保存", () => Run(async token =>
        {
            var endpoint = LockboxClient.ParseServer(server.Text ?? "");
            var personalKey = key.Text?.Trim() ?? "";
            if (endpoint is null) { Message("请输入不含路径的 HTTPS DMS 地址。"); return; }
            if (!LockboxClient.IsPersonalKey(personalKey)) { Message("请输入有效的 DMS 个人 API Key（dmsu_ 开头）。"); return; }
            var candidate = new LockboxClient(endpoint, personalKey);
            var user = await candidate.VerifyAsync(token);
            if (token.IsCancellationRequested) return;
            _store.Save(endpoint, personalKey);
            ReviewNotificationService.StopListening(this);
            key.Text = "";
            _client = candidate;
            await LoadQueueAsync(0, token, $"已连接：{user.UserName}");
        }));
        if (_store.Server is not null)
            ActionButton(_content, "清除本机连接", () => Run(_ =>
            {
                ReviewNotificationService.StopListening(this);
                _store.Clear();
                _client = null;
                ShowSettings("已清除本机保存的连接；服务器上的 Key 不会被撤销。");
                return Task.CompletedTask;
            }), ActionStyle.Secondary);
        var security = Section("凭据安全");
        Text(security, "API Key 由 Android Keystore 保护，仅用于连接你指定的 DMS。", 14, Subtle);
        Message(notice);
    }

    private void OpenQueue() => Run(token => LoadQueueAsync(0, token));

    private void OpenQueueFromNotification()
    {
        if (_notificationNavigation.TryConsume(_busy, _lifetime.Token) && _client is not null) OpenQueue();
    }

    private async Task LoadQueueAsync(int page, CancellationToken token, string notice = "")
    {
        Page("待我审批", "查看你有权限处理的待审请求。", actionLabel: "设置", action: () => ShowSettings());
        var tools = Row(_content);
        ActionButton(tools, "刷新列表", OpenQueue, ActionStyle.Secondary, share: true);
        ActionButton(tools, "值班提醒", ToggleNotifications, ActionStyle.Secondary, share: true);
        ActionButton(_content, "我的审批记录", () => Run(ct => LoadHistoryAsync(0, ct)), ActionStyle.Secondary);
        Message("正在加载…");
        var list = await _client!.QueueAsync(page, token);
        if (token.IsCancellationRequested) return;
        Message(notice);
        Heading(_content, page == 0 ? "待处理请求" : $"待处理请求 · 第 {page + 1} 页");
        if (list.Entries.Count == 0)
            Section("暂时没有待审请求", "新的请求会显示在这里。也可以开启值班提醒。");
        foreach (var row in list.Entries)
        {
            var card = Card(_content, out var body, () => Run(ct => LoadDetailAsync(row.LockboxId, page, ct)));
            var header = Row(body, gap: 0);
            var name = Text(header, row.Definition, 16, Ink, bold: true, share: true);
            name.SetPadding(0, 0, Dp(8), 0);
            RiskChip(header, row.Risk);
            Text(body, $"{row.Requester}　·　{LocalTime(row.CreatedAt)}", 13, Subtle, top: 8);
            card.ContentDescription = $"{row.Definition}，{row.Requester}，{LocalTime(row.CreatedAt)}，{RiskText(row.Risk)}风险";
        }
        if (page > 0 || list.HasNextPage)
        {
            var pages = Row(_content);
            if (page > 0) ActionButton(pages, "上一页", () => Run(ct => LoadQueueAsync(page - 1, ct)), ActionStyle.Secondary, share: true);
            if (list.HasNextPage) ActionButton(pages, "下一页", () => Run(ct => LoadQueueAsync(page + 1, ct)), ActionStyle.Secondary, share: true);
        }
    }

    private async Task LoadHistoryAsync(int page, CancellationToken token)
    {
        Page("我的审批记录", "查看你亲自同意或拒绝的请求。", OpenQueue,
            "刷新", () => Run(ct => LoadHistoryAsync(page, ct)));
        Message("正在加载…");
        var history = await _client!.HistoryAsync(page, token);
        if (token.IsCancellationRequested) return;
        Message("");
        Heading(_content, page == 0 ? "处理记录" : $"处理记录 · 第 {page + 1} 页");
        if (history.Entries.Count == 0)
            Section("没有审批记录", "你同意或拒绝的请求会显示在这里。");
        foreach (var entry in history.Entries)
        {
            var card = Card(_content, out var body,
                () => Run(ct => LoadDetailAsync(entry.LockboxId, page, ct, fromHistory: true)));
            var header = Row(body, gap: 0);
            var name = Text(header, entry.Definition, 16, Ink, bold: true, share: true);
            name.SetPadding(0, 0, Dp(8), 0);
            var outcome = DecisionText(entry.Decision);
            var approved = entry.Decision == DecisionState.Approved;
            Chip(header, outcome, approved ? "#D7EFE7" : "#FBE2DC", approved ? "#194A40" : "#812820");
            Text(body, $"{entry.Requester}　·　{RiskText(entry.Risk)}风险", 13, Subtle, top: 8);
            Text(body, $"处理时间：{LocalTime(entry.DecidedAt)}", 13, Subtle, top: 2);
            card.ContentDescription = $"{entry.Definition}，{entry.Requester}，{RiskText(entry.Risk)}风险，{outcome}，{LocalTime(entry.DecidedAt)}";
        }
        if (page > 0 || history.HasNextPage)
        {
            var pages = Row(_content);
            if (page > 0) ActionButton(pages, "上一页", () => Run(ct => LoadHistoryAsync(page - 1, ct)), ActionStyle.Secondary, share: true);
            if (history.HasNextPage) ActionButton(pages, "下一页", () => Run(ct => LoadHistoryAsync(page + 1, ct)), ActionStyle.Secondary, share: true);
        }
    }

    private async Task LoadDetailAsync(LockboxId id, int page, CancellationToken token, string notice = "", bool fromHistory = false)
    {
        DetailPage(id, page, fromHistory);
        Message("正在加载…");
        var status = await _client!.StatusAsync(id, token);
        if (token.IsCancellationRequested) return;
        Message(notice);

        Card(_content, out var summary);
        var badges = Row(summary, gap: 0);
        Chip(badges, DecisionText(status.Decision), status.Decision == DecisionState.Pending ? "#D7EFE7" : "#EDF1F0", "#194A40");
        if (status.Details is { } item) RiskChip(badges, item.Risk);
        Text(summary, status.Details?.Definition ?? "请求摘要", 21, Ink, bold: true, top: 10);
        Text(summary, status.LockboxId.ToString(), 12, Subtle, top: 4, mono: true);
        if (status.Details is not { } detail) return;

        var requestInfo = Section("申请信息");
        Metadata(requestInfo, "申请人", status.RequestedBy);
        Metadata(requestInfo, "提交时间", LocalTime(status.RequestedAt));
        if (!string.IsNullOrWhiteSpace(detail.Reason)) Metadata(requestInfo, "申请原因", detail.Reason);
        if (status.DecidedAt is { } decided)
        {
            Metadata(requestInfo, "处理人", status.DecidedBy ?? "系统");
            Metadata(requestInfo, "处理时间", LocalTime(decided));
        }
        if (!string.IsNullOrWhiteSpace(detail.DecisionReason)) Metadata(requestInfo, "处理原因", detail.DecisionReason);

        try
        {
            using var request = JsonDocument.Parse(detail.Parameters);
            using var targets = JsonDocument.Parse(detail.TargetMachines);
            var ids = targets.RootElement.EnumerateArray().Select(target => target.GetGuid()).ToArray();
            var all = request.RootElement.TryGetProperty("AllMachines", out var allMachines) && allMachines.ValueKind == JsonValueKind.True;
            var scope = Section("目标范围", ids.Length == 0 ? "无机器目标" : $"{(all ? "提交时的全部目标 · " : "")}{ids.Length} 台机器");
            foreach (var machine in ids)
            {
                if (status.MachineNames?.TryGetValue(machine, out var name) == true)
                    Metadata(scope, name, machine.ToString(), mono: true);
                else Text(scope, machine.ToString(), 13, Ink, top: 8, mono: true);
            }
            if (request.RootElement.TryGetProperty("TargetWorkflowId", out var workflow) &&
                workflow.ValueKind == JsonValueKind.String && workflow.TryGetGuid(out var workflowId))
                Metadata(scope, "目标 Workflow", workflowId.ToString(), mono: true);
            if (request.RootElement.TryGetProperty("MachineName", out var machineName) &&
                machineName.ValueKind == JsonValueKind.String && machineName.GetString() is { Length: > 0 } proposedName)
                Metadata(scope, "申请机器名称", proposedName);
            var input = request.RootElement.GetProperty("Input").GetString() ?? "";
            var parameters = Section("请求参数");
            var code = Text(parameters, input, 13, Ink, top: 10, mono: true);
            code.Background = Rounded("#F0F5F3", 12);
            code.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Message("无法读取完整的请求参数或目标，不能在手机上处理。");
            return;
        }

        if (fromHistory || status.Decision != DecisionState.Pending) return;
        if (status.CanApprove != true)
        {
            if (status.DefinitionChanged)
                Section("工作流定义已变更", "此请求不能批准，请让申请人重新提交。");
            else
                Section("当前不能审批此请求");
            return;
        }
        var decision = Section("处理请求", "请核对操作和目标，再提交决定。");
        var reason = Field(decision, "审批原因（可选）", InputTypes.ClassText | InputTypes.TextFlagMultiLine);
        reason.SetFilters([new InputFilterLengthFilter(2000)]);
        var actions = Row(decision);
        ActionButton(actions, "拒绝", () => Confirm(id, page, false, reason.Text?.Trim() ?? ""), ActionStyle.Danger, share: true);
        ActionButton(actions, "同意", () => Confirm(id, page, true, reason.Text?.Trim() ?? ""), share: true);
    }

    private void Confirm(LockboxId id, int page, bool approve, string reason)
    {
        var action = approve ? "同意" : "拒绝";
        using var dialog = new MaterialAlertDialogBuilder(this);
        dialog.SetTitle($"确认{action}此请求？");
        dialog.SetMessage(id.ToString());
        dialog.SetNegativeButton("取消", (_, _) => { });
        dialog.SetPositiveButton(action, (_, _) => Run(token => DecideAsync(id, page, approve, reason, token)));
        dialog.Show();
    }

    private async Task DecideAsync(LockboxId id, int page, bool approve, string reason, CancellationToken token)
    {
        // Remove the decision controls before sending; an uncertain result must be read back, not reposted.
        DetailPage(id, page);
        Message("正在提交审批…");
        string notice;
        try
        {
            await _client!.DecideAsync(id, approve, reason, token);
            notice = "决定已提交。";
        }
        catch (LockboxHttpException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            notice = "请求已变化或不再可审批；未重新提交。";
        }
        catch (LockboxHttpException ex)
        {
            notice = ErrorMessage(ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            if (token.IsCancellationRequested) return;
            notice = "未确认是否提交成功；未自动重试。";
        }
        if (token.IsCancellationRequested) return;
        try { await LoadDetailAsync(id, page, token, notice); }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            Message($"{notice}\n刷新失败：{ErrorMessage(ex)} 请先刷新状态。");
        }
    }

    private async void Run(Func<CancellationToken, Task> operation)
    {
        if (_busy) return;
        _busy = true;
        var token = _lifetime.Token;
        SetButtons(false);
        try { await operation(token); }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) Message(ErrorMessage(ex));
        }
        finally
        {
            _busy = false;
            if (!token.IsCancellationRequested)
            {
                SetButtons(true);
                OpenQueueFromNotification();
            }
        }
    }

    private void DetailPage(LockboxId id, int page, bool fromHistory = false) => Page("审批详情", "核对请求信息与影响范围。",
        () => Run(ct => fromHistory ? LoadHistoryAsync(page, ct) : LoadQueueAsync(page, ct)),
        "刷新", () => Run(ct => LoadDetailAsync(id, page, ct, fromHistory: fromHistory)));

    private static string LocalTime(DateTime utc) => utc.ToLocalTime().ToString("g");
    private static string RiskText(RiskLevel risk) => risk switch
    {
        RiskLevel.Open => "开放", RiskLevel.Safe => "安全", RiskLevel.Low => "低", RiskLevel.Medium => "中",
        RiskLevel.High => "高", RiskLevel.VeryHigh => "很高", _ => risk.ToString()
    };
    private static string DecisionText(DecisionState state) => state switch
    {
        DecisionState.Pending => "待审批", DecisionState.Approved => "已同意", DecisionState.Denied => "已拒绝",
        DecisionState.Withdrawn => "已撤回", _ => state.ToString()
    };
    private static string ErrorMessage(Exception ex) => ex switch
    {
        LockboxHttpException { Status: HttpStatusCode.Unauthorized } => "DMS 个人 API Key 无效或已过期。",
        LockboxHttpException { Status: HttpStatusCode.Forbidden } => "当前账号没有执行此操作的权限。",
        LockboxHttpException { Status: HttpStatusCode.NotFound } => "未找到请求，或 DMS 还未部署 Lockbox App 接口。",
        LockboxHttpException api => $"DMS 返回 HTTP {(int)api.Status}。",
        OperationCanceledException => "DMS 连接超时。",
        HttpRequestException => "无法连接 DMS，请检查网络、地址和 HTTPS 证书。",
        JsonException or InvalidDataException => "DMS 返回了无法识别的数据。",
        _ => "操作失败，请稍后重试。"
    };
}
