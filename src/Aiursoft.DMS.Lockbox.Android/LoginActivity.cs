using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using Google.Android.Material.Button;
using Google.Android.Material.TextField;

using OperationCanceledException = System.OperationCanceledException;
namespace Aiursoft.DMS.Lockbox.Android;

[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    Theme = "@style/AppTheme")]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "com.aiursoft.dms.lockbox",
    DataPathPrefix = "/oauth2redirect")]
public sealed class LoginActivity : AppCompatActivity
{
    private const string DefaultServerPlaceholder = "https://dms.aiursoft.com";

    private TextInputLayout _serverContainer = null!;
    private TextInputEditText _serverInput = null!;
    private LinearLayout _connectedServerRow = null!;
    private TextView _connectedServerLabel = null!;
    private MaterialButton _changeServerButton = null!;
    private MaterialButton _connectButton = null!;
    private LinearLayout _authPanel = null!;
    private MaterialButton _oidcSignInButton = null!;
    private LinearLayout _localAuthSection = null!;
    private TextInputLayout _identityContainer = null!;
    private TextInputEditText _identityInput = null!;
    private TextInputLayout _passwordContainer = null!;
    private TextInputEditText _passwordInput = null!;
    private MaterialButton _localSignInButton = null!;
    private MaterialButton _registerButton = null!;
    private MaterialButton _toggleApiKeyButton = null!;
    private LinearLayout _apiKeySection = null!;
    private TextInputEditText _apiKeyInput = null!;
    private MaterialButton _apiKeySignInButton = null!;
    private TextView _statusText = null!;

    private AppSession Session => ((LockboxApplication)Application!).Session;
    private CancellationTokenSource? _activeCts;
    private bool _busy;
    private bool _registerMode;
    private bool _apiKeyExpanded;
    private Uri? _connectedServer;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
        SetContentView(Resource.Layout.activity_login);

        var root = FindViewById<View>(Resource.Id.login_root)!;
        root.SetOnApplyWindowInsetsListener(new SystemBarInsetListener(0, 0, 0, 0, applyTop: true, applyBottom: true, applyIme: true));
        WindowCompat.GetInsetsController(Window!, root)!.AppearanceLightStatusBars = false;

        BindViews();
        WireEvents();

        if (Intent?.Data is { } callbackUri && IsOidcCallback(callbackUri))
        {
            _ = HandleOidcCallbackAsync(callbackUri);
            return;
        }

        if (Session.TryRestoreFromDisk())
        {
            OpenWorkspace();
            return;
        }

        var savedServer = Session.SavedServer;
        _serverInput.Text = !string.IsNullOrWhiteSpace(savedServer)
            ? savedServer.TrimEnd('/')
            : DefaultServerPlaceholder;
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        if (intent?.Data is { } callbackUri && IsOidcCallback(callbackUri))
        {
            _ = HandleOidcCallbackAsync(callbackUri);
        }
    }

    protected override void OnDestroy()
    {
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        base.OnDestroy();
    }

    private void BindViews()
    {
        _serverContainer = FindViewById<TextInputLayout>(Resource.Id.server_container)!;
        _serverInput = FindViewById<TextInputEditText>(Resource.Id.server_input)!;
        _connectedServerRow = FindViewById<LinearLayout>(Resource.Id.connected_server_row)!;
        _connectedServerLabel = FindViewById<TextView>(Resource.Id.connected_server_label)!;
        _changeServerButton = FindViewById<MaterialButton>(Resource.Id.change_server_button)!;
        _connectButton = FindViewById<MaterialButton>(Resource.Id.connect_button)!;
        _authPanel = FindViewById<LinearLayout>(Resource.Id.auth_panel)!;
        _oidcSignInButton = FindViewById<MaterialButton>(Resource.Id.oidc_sign_in_button)!;
        _localAuthSection = FindViewById<LinearLayout>(Resource.Id.local_auth_section)!;
        _identityContainer = FindViewById<TextInputLayout>(Resource.Id.identity_container)!;
        _identityInput = FindViewById<TextInputEditText>(Resource.Id.identity_input)!;
        _passwordContainer = FindViewById<TextInputLayout>(Resource.Id.password_container)!;
        _passwordInput = FindViewById<TextInputEditText>(Resource.Id.password_input)!;
        _localSignInButton = FindViewById<MaterialButton>(Resource.Id.local_sign_in_button)!;
        _registerButton = FindViewById<MaterialButton>(Resource.Id.register_button)!;
        _toggleApiKeyButton = FindViewById<MaterialButton>(Resource.Id.toggle_api_key_button)!;
        _apiKeySection = FindViewById<LinearLayout>(Resource.Id.api_key_section)!;
        _apiKeyInput = FindViewById<TextInputEditText>(Resource.Id.api_key_input)!;
        _apiKeySignInButton = FindViewById<MaterialButton>(Resource.Id.api_key_sign_in_button)!;
        _statusText = FindViewById<TextView>(Resource.Id.login_status)!;

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            _apiKeyInput.ImportantForAutofill = ImportantForAutofill.No;
        }
    }

    private void WireEvents()
    {
        _connectButton.Click += async (_, _) =>
        {
            if (_busy)
            {
                _activeCts?.Cancel();
                return;
            }
            await ConnectServerAsync();
        };

        _serverInput.EditorAction += async (_, args) =>
        {
            if (args.ActionId == ImeAction.Go && !_busy)
            {
                await ConnectServerAsync();
            }
        };

        _changeServerButton.Click += (_, _) =>
        {
            if (_busy) return;
            ShowServerEntry();
        };

        _oidcSignInButton.Click += (_, _) => BeginOidcSignIn();

        _localSignInButton.Click += async (_, _) => await SignInLocalAsync();

        _passwordInput.EditorAction += async (_, args) =>
        {
            if (args.ActionId == ImeAction.Done && !_busy)
            {
                await SignInLocalAsync();
            }
        };

        _registerButton.Click += (_, _) =>
        {
            if (_busy) return;
            _registerMode = !_registerMode;
            _identityContainer.Hint = _registerMode ? "注册邮箱" : "邮箱或用户名";
            _localSignInButton.Text = _registerMode ? "注册并登录" : "登录";
            _registerButton.Text = _registerMode ? "已有账号？返回登录" : "创建新账号";
            SetStatus(null);
        };

        _toggleApiKeyButton.Click += (_, _) =>
        {
            if (_busy) return;
            _apiKeyExpanded = !_apiKeyExpanded;
            _apiKeySection.Visibility = _apiKeyExpanded ? ViewStates.Visible : ViewStates.Gone;
            _toggleApiKeyButton.Text = _apiKeyExpanded ? "收起个人 API Key 登录" : "改用个人 API Key (dmsu_)";
        };

        _apiKeySignInButton.Click += async (_, _) => await SignInApiKeyAsync();

        _apiKeyInput.EditorAction += async (_, args) =>
        {
            if (args.ActionId == ImeAction.Done && !_busy)
            {
                await SignInApiKeyAsync();
            }
        };
    }

    private void ShowServerEntry()
    {
        _connectedServer = null;
        _registerMode = false;
        _apiKeyExpanded = false;
        _serverContainer.Visibility = ViewStates.Visible;
        _connectButton.Visibility = ViewStates.Visible;
        _connectButton.Text = "连接服务器";
        _connectedServerRow.Visibility = ViewStates.Gone;
        _authPanel.Visibility = ViewStates.Gone;
        SetStatus(null);
    }

    private async Task ConnectServerAsync()
    {
        var server = LockboxClient.ParseServer(_serverInput.Text ?? string.Empty);
        if (server is null)
        {
            SetStatus("请输入不含路径的 HTTPS 地址（例如 https://dms.aiursoft.com）。", isError: true);
            return;
        }

        await RunBusyAsync("正在连接服务器…", async token =>
        {
            _connectButton.Text = "取消连接";
            var config = await Session.ConnectServerAsync(server, token);
            _connectedServer = server;

            _serverContainer.Visibility = ViewStates.Gone;
            _connectButton.Visibility = ViewStates.Gone;
            _connectedServerLabel.Text = server.AbsoluteUri.TrimEnd('/');
            _connectedServerRow.Visibility = ViewStates.Visible;
            _authPanel.Visibility = ViewStates.Visible;

            if (Session.IsOidcReady)
            {
                _oidcSignInButton.Visibility = ViewStates.Visible;
                _localAuthSection.Visibility = ViewStates.Gone;
                _apiKeyExpanded = false;
                _apiKeySection.Visibility = ViewStates.Gone;
                _toggleApiKeyButton.Visibility = ViewStates.Visible;
                _toggleApiKeyButton.Text = "改用个人 API Key (dmsu_)";
                SetStatus("已连接到 DMS，推荐使用单点登录。");
            }
            else if (config is not null &&
                     string.Equals(config.AuthenticationMode, "Local", StringComparison.OrdinalIgnoreCase))
            {
                _oidcSignInButton.Visibility = ViewStates.Gone;
                _localAuthSection.Visibility = ViewStates.Visible;
                _registerButton.Visibility = config.AllowRegistration ? ViewStates.Visible : ViewStates.Gone;
                _apiKeyExpanded = false;
                _apiKeySection.Visibility = ViewStates.Gone;
                _toggleApiKeyButton.Visibility = ViewStates.Visible;
                _toggleApiKeyButton.Text = "改用个人 API Key (dmsu_)";
                SetStatus("已连接到 DMS，请使用账号密码登录。");
            }
            else
            {
                // Older server without /api/v1/config, or OIDC mobile client not yet configured.
                _oidcSignInButton.Visibility = ViewStates.Gone;
                _localAuthSection.Visibility = ViewStates.Gone;
                _apiKeyExpanded = true;
                _apiKeySection.Visibility = ViewStates.Visible;
                _toggleApiKeyButton.Visibility = ViewStates.Gone;
                SetStatus("当前服务器暂未开启移动端 SSO，请使用 DMS 个人 API Key (dmsu_) 登录。");
            }
        });

        if (_connectedServer is null)
        {
            _connectButton.Text = "连接服务器";
        }
    }

    private void BeginOidcSignIn()
    {
        if (_busy) return;
        try
        {
            var authorizeUri = Session.CreateOidcAuthorizationUri();
            SetStatus("正在打开系统浏览器完成单点登录…");
            StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(authorizeUri.AbsoluteUri)));
        }
        catch (Exception ex)
        {
            SetStatus(LockboxFormatting.FriendlyError(ex), isError: true);
        }
    }

    private async Task HandleOidcCallbackAsync(global::Android.Net.Uri callback)
    {
        await RunBusyAsync("正在验证单点登录凭据…", async token =>
        {
            var uri = new Uri(callback.ToString()!);
            await Session.CompleteOidcSignInAsync(uri, token);
            OpenWorkspace();
        });
    }

    private async Task SignInLocalAsync()
    {
        if (_connectedServer is null) return;
        var identity = _identityInput.Text?.Trim() ?? string.Empty;
        var password = _passwordInput.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(identity) || string.IsNullOrEmpty(password))
        {
            SetStatus("请输入账号和密码。", isError: true);
            return;
        }

        await RunBusyAsync(_registerMode ? "正在创建账号…" : "正在验证登录…", async token =>
        {
            await Session.SignInLocalAsync(_connectedServer, identity, password, _registerMode, token);
            OpenWorkspace();
        });
    }

    private async Task SignInApiKeyAsync()
    {
        if (_connectedServer is null) return;
        var key = _apiKeyInput.Text?.Trim() ?? string.Empty;
        if (!LockboxClient.IsPersonalKey(key))
        {
            SetStatus("请输入有效的 69 位 DMS 个人 API Key（以 dmsu_ 开头）。", isError: true);
            return;
        }

        await RunBusyAsync("正在验证个人 API Key…", async token =>
        {
            await Session.SignInApiKeyAsync(_connectedServer, key, token);
            OpenWorkspace();
        });
    }

    private async Task RunBusyAsync(string status, Func<CancellationToken, Task> work)
    {
        if (_busy) return;
        _busy = true;
        _activeCts?.Cancel();
        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();
        SetInteractive(false);
        SetStatus(status);
        try
        {
            await work(_activeCts.Token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消操作。");
        }
        catch (Exception ex)
        {
            SetStatus(LockboxFormatting.FriendlyError(ex), isError: true);
        }
        finally
        {
            _busy = false;
            SetInteractive(true);
        }
    }

    private void SetInteractive(bool enabled)
    {
        _serverInput.Enabled = enabled;
        _changeServerButton.Enabled = enabled;
        _oidcSignInButton.Enabled = enabled;
        _identityInput.Enabled = enabled;
        _passwordInput.Enabled = enabled;
        _localSignInButton.Enabled = enabled;
        _registerButton.Enabled = enabled;
        _toggleApiKeyButton.Enabled = enabled;
        _apiKeyInput.Enabled = enabled;
        _apiKeySignInButton.Enabled = enabled;
    }

    private void SetStatus(string? message, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _statusText.Visibility = ViewStates.Gone;
            _statusText.Text = string.Empty;
            return;
        }

        _statusText.Text = message;
        _statusText.SetTextColor(Resources!.GetColor(
            isError ? Resource.Color.on_danger_container : Resource.Color.text_secondary, Theme));
        _statusText.Visibility = ViewStates.Visible;
    }

    private void OpenWorkspace()
    {
        var intent = new Intent(this, typeof(MainActivity));
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
        StartActivity(intent);
        Finish();
    }

    private static bool IsOidcCallback(global::Android.Net.Uri uri) =>
        string.Equals(uri.Scheme, "com.aiursoft.dms.lockbox", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Path, "/oauth2redirect", StringComparison.OrdinalIgnoreCase);
}
