using Aiursoft.DMS.Workflows;
using Aiursoft.CSTools.Tools;
using Aiursoft.Canon.TaskQueue;
using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.Canon.ScheduledTasks;
using Aiursoft.DbTools.Switchable;
using Aiursoft.Scanner;
using Aiursoft.DMS.Configuration;
using Aiursoft.WebTools.Abstractions.Models;
using Aiursoft.DMS.InMemory;
using Aiursoft.DMS.MySql;
using Aiursoft.DMS.Services.Authentication;
using Aiursoft.DMS.Services.BackgroundJobs;
using Aiursoft.DMS.Sqlite;
using Aiursoft.UiStack.Layout;
using Aiursoft.UiStack.Navigation;
using Microsoft.AspNetCore.Mvc.Razor;
using Aiursoft.ClickhouseLoggerProvider;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Aiursoft.DMS;

[ExcludeFromCodeCoverage]
public class Startup : IWebStartup
{
    public void ConfigureServices(IConfiguration configuration, IWebHostEnvironment environment, IServiceCollection services)
    {
        // AppSettings.
        services.Configure<AppSettings>(configuration.GetSection("AppSettings"));

        // Relational database
        var (connectionString, dbType, allowCache) = configuration.GetDbSettings();
        services.AddSwitchableRelationalDatabase(
            dbType: EntryExtends.IsInUnitTests() ? "InMemory" : dbType,
            connectionString: connectionString,
            supportedDbs:
            [
                new MySqlSupportedDb(allowCache: allowCache, splitQuery: false),
                new SqliteSupportedDb(allowCache: allowCache, splitQuery: true),
                new InMemorySupportedDb()
            ]);

        services.AddLogging(builder =>
        {
            builder.AddClickhouse(options => configuration.GetSection("Logging:Clickhouse").Bind(options));
        });

        // Authentication and Authorization
        services.AddTemplateAuth(configuration);

        services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
            DmsKeyAuthenticationHandler>(DmsKeyAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(options =>
        {
            options.AddPolicy("DmsHuman", policy => policy.RequireClaim(System.Security.Claims.ClaimTypes.NameIdentifier));
            options.AddPolicy("DmsAgent", policy => policy.RequireClaim("DmsMachine"));
            options.AddPolicy("DmsJoin", policy => policy.RequireClaim("DmsJoinKey"));
            options.AddPolicy("DmsEnrollment", policy => policy.RequireClaim("DmsEnrollment"));
        });
        services.AddSingleton<Services.Dms.ControllerGate>();
        services.AddSingleton<Services.Dms.LockboxReviewSignal>();
        services.AddSingleton<Services.Dms.AgentSessions>();
        services.AddSingleton<Services.Dms.TraceStore>();
        services.AddSingleton<Services.Dms.DmsArtifactStore>();
        services.AddScoped<Services.Dms.DmsArtifactAccess>();
        services.AddScoped<Services.Dms.PersonalSshKeys>();
        services.AddScoped<Services.Dms.AccessService>();
        services.AddScoped<Services.Dms.MachineSshOverview>();
        services.AddScoped<Services.Dms.LockboxPages>();
        services.AddScoped<Services.Dms.DmsManagementService>();
        services.AddScoped<Services.Dms.EnrollmentService>();
        services.AddScoped<Services.Dms.WorkflowService>();
        services.AddSingleton<Services.Dms.AgentReleaseStore>();
        services.AddSingleton<Services.Dms.BoxSecretTransfers>();
        services.AddSingleton<Services.Dms.DatabaseSecretTransfers>();
        services.AddSingleton<Services.Dms.WorkflowCatalog>();
        services.AddScoped<Services.Dms.AgentEvents>();
        services.AddScoped<Services.Dms.TraceDatabase>();
        services.AddScoped<Services.Dms.WorkflowAuditArchive>();
        services.AddScoped<Services.Dms.WorkflowInvestigation>();
        services.AddScoped<Services.Dms.FilesystemListResults>();
        services.AddSingleton<MachineWorkflowRegistry>();
        services.AddSingleton<Services.Dms.ClusterWorkflowRegistry>();
        services.AddSingleton<Services.Dms.ClusterWorkflowRuntime>();
        services.AddHostedService<Services.Dms.WorkflowScheduler>();
        services.AddHostedService<Services.Dms.LockboxExpiryService>();
        services.AddHostedService<Services.Dms.TraceFlushService>();
        services.AddOptions<Services.Dms.LockboxEmailOptions>().BindConfiguration("Dms:LockboxEmail")
            .Validate(options => options.IsValid(), "Configure a valid SMTP host, port, sender, and HTTPS Controller origin for Lockbox mail.")
            .ValidateOnStart();
        services.AddScoped<Services.Dms.ILockboxReminderEmail, Services.Dms.SmtpLockboxReminderEmail>();
        services.AddScoped<Services.Dms.LockboxEmailReminders>();
        services.AddHostedService<Services.Dms.LockboxEmailReminderService>();

        // Services
        services.AddMemoryCache();
        services.AddHttpClient();
        services.AddAssemblyDependencies(typeof(Startup).Assembly);
        services.AddSingleton<NavigationState<Startup>>();

        // Background job infrastructure
        services.AddTaskQueueEngine();
        services.AddScheduledTaskEngine();

        // Background jobs
        services.RegisterBackgroundJob<DummyJob>();
        var orphanAvatarCleanupJob = services.RegisterBackgroundJob<OrphanAvatarCleanupJob>();
        var sshKeyVerificationCleanupJob = services.RegisterBackgroundJob<SshKeyVerificationCleanupJob>();

        // Scheduled tasks (attach a schedule to any registered background job)
        services.RegisterScheduledTask(
            registration: orphanAvatarCleanupJob,
            period:     TimeSpan.FromHours(6),
            startDelay: TimeSpan.FromMinutes(5));
        services.RegisterScheduledTask(
            registration: sshKeyVerificationCleanupJob,
            period:     TimeSpan.FromMinutes(1),
            startDelay: TimeSpan.FromMinutes(1));

        // Controllers and localization
        services.AddControllersWithViews()
            .AddNewtonsoftJson(options =>
            {
                options.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
                options.SerializerSettings.ContractResolver = new DefaultContractResolver();
            })
            .AddApplicationPart(typeof(Startup).Assembly)
            .AddApplicationPart(typeof(UiStackLayoutViewModel).Assembly)
            .AddViewLocalization(LanguageViewLocationExpanderFormat.Suffix)
            .AddDataAnnotationsLocalization();
    }

    public void Configure(WebApplication app)
    {
        app.UseExceptionHandler("/Error/Code500");
        app.UseStatusCodePagesWithReExecute("/Error/Code{0}");
        app.UseStaticFiles();
        app.UseRouting();
        app.UseWebSockets();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDefaultControllerRoute();
    }
}
