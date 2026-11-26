using System.Globalization;
using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Lockbox.Android;

internal static class LockboxFormatting
{
    public static string RiskLabel(RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => "安全",
        RiskLevel.Low => "低风险",
        RiskLevel.Medium => "中风险",
        RiskLevel.High => "高风险",
        RiskLevel.VeryHigh => "极高风险",
        RiskLevel.Open => "开放",
        _ => risk.ToString()
    };

    public static string RiskShortLabel(RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => "安全",
        RiskLevel.Low => "低",
        RiskLevel.Medium => "中",
        RiskLevel.High => "高",
        RiskLevel.VeryHigh => "极高",
        RiskLevel.Open => "开放",
        _ => risk.ToString()
    };

    public static (int Background, int Foreground, int Stripe) RiskColors(RiskLevel risk) => risk switch
    {
        RiskLevel.Safe => (Resource.Color.brand_container, Resource.Color.on_brand_container, Resource.Color.stripe_safe),
        RiskLevel.Low => (Resource.Color.info_container, Resource.Color.on_info_container, Resource.Color.stripe_low),
        RiskLevel.Medium => (Resource.Color.warning_container, Resource.Color.on_warning_container, Resource.Color.stripe_medium),
        RiskLevel.High => (Resource.Color.danger_container, Resource.Color.on_danger_container, Resource.Color.stripe_high),
        RiskLevel.VeryHigh => (Resource.Color.critical_container, Resource.Color.on_critical_container, Resource.Color.stripe_very_high),
        _ => (Resource.Color.neutral_container, Resource.Color.on_neutral_container, Resource.Color.stripe_open)
    };

    public static string DecisionLabel(DecisionState decision) => decision switch
    {
        DecisionState.Pending => "待审批",
        DecisionState.Approved => "已同意",
        DecisionState.Denied => "已拒绝",
        DecisionState.Withdrawn => "已撤回",
        DecisionState.Expired => "已过期",
        _ => decision.ToString()
    };

    public static (int Background, int Foreground) DecisionColors(DecisionState decision) => decision switch
    {
        DecisionState.Pending => (Resource.Color.brand_container, Resource.Color.on_brand_container),
        DecisionState.Approved => (Resource.Color.success_container, Resource.Color.on_success_container),
        DecisionState.Denied => (Resource.Color.danger_container, Resource.Color.on_danger_container),
        DecisionState.Expired => (Resource.Color.warning_container, Resource.Color.on_warning_container),
        _ => (Resource.Color.neutral_container, Resource.Color.on_neutral_container)
    };

    public static string? FriendlyWorkflowTitle(string definition) => definition switch
    {
        "machine.ssh.grant" => "临时 SSH 授权",
        "machine.ssh.disable" => "停用 SSH 服务",
        "machine.agent.upgrade" => "DMS Agent 升级",
        "machine.agent.status" => "查询 Agent 状态",
        "machine.systemctl.restart" => "重启系统服务",
        "machine.box.puller.force-update" => "Box 强制拉取更新",
        "machine.box.secret.get-raw" => "读取 Box 明文密钥",
        "machine.box.deploy" => "部署 Box 服务",
        "machine.performance.info" => "采集主机性能信息",
        "machine.poweroff" => "主机关机",
        "machine.reboot" => "主机重启",
        "machine.apt.upgrade" => "系统 APT 升级",
        "machine.frps.deploy" => "部署 FRPS 服务",
        _ when definition.Contains("ssh", StringComparison.OrdinalIgnoreCase) => "SSH 运维操作",
        _ when definition.Contains("secret", StringComparison.OrdinalIgnoreCase) => "密钥敏感操作",
        _ when definition.Contains("docker", StringComparison.OrdinalIgnoreCase) => "Docker 容器操作",
        _ => null
    };

    public static string LocalTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public static string RelativeTime(DateTime utc)
    {
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        var delta = DateTime.Now - local;
        if (delta.TotalSeconds < 60) return "刚刚";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes} 分钟前";
        if (delta.TotalHours < 24 && local.Date == DateTime.Today) return $"今天 {local:HH:mm}";
        if (local.Date == DateTime.Today.AddDays(-1)) return $"昨天 {local:HH:mm}";
        return local.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    public static string ExpiryCountdown(DateTime expiresUtc)
    {
        var remaining = DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc) - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero) return "已过期";
        if (remaining.TotalMinutes < 1) return "即将过期 (<1 分钟)";
        if (remaining.TotalHours < 1) return $"剩余 {(int)Math.Ceiling(remaining.TotalMinutes)} 分钟";
        if (remaining.TotalDays < 1) return $"剩余 {(int)remaining.TotalHours} 小时 {remaining.Minutes} 分";
        return $"剩余 {(int)remaining.TotalDays} 天";
    }

    public static string PrettyFormatPayload(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(无参数)";
        var trimmed = raw.Trim();
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) ||
            (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                // Fall back to original string.
            }
        }
        return raw;
    }

    public static string FriendlyError(Exception exception) => exception switch
    {
        LockboxHttpException { Status: System.Net.HttpStatusCode.Unauthorized } http =>
            string.IsNullOrWhiteSpace(http.ServerMessage) ? "身份验证无效或已过期，请重新登录。" : http.ServerMessage,
        LockboxHttpException { Status: System.Net.HttpStatusCode.Forbidden } http =>
            string.IsNullOrWhiteSpace(http.ServerMessage) ? "当前账号没有执行该操作的权限。" : http.ServerMessage,
        LockboxHttpException { Status: System.Net.HttpStatusCode.NotFound } =>
            "未找到该审批记录，可能已被移除或服务版本不匹配。",
        LockboxHttpException { Status: System.Net.HttpStatusCode.Conflict } =>
            "该请求状态已变更（可能已被他人处理或申请人已撤回）。",
        LockboxHttpException { ServerMessage: { Length: > 0 } msg } => msg,
        LockboxHttpException http => $"DMS 返回 HTTP {(int)http.Status}。",
        HttpRequestException => "无法连接到 DMS 服务器，请检查网络、HTTPS 证书或服务器地址。",
        TaskCanceledException => "请求超时，请稍后重试。",
        _ => string.IsNullOrWhiteSpace(exception.Message) ? "操作失败，请稍后重试。" : exception.Message
    };
}
