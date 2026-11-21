using Aiursoft.Canon.BackgroundJobs;
using Aiursoft.DMS.Services.Dms;

namespace Aiursoft.DMS.Services.BackgroundJobs;

public class SshKeyVerificationCleanupJob(PersonalSshKeys keys) : IBackgroundJob
{
    public string Name => "Expired SSH Key Verification Cleanup";
    public string Description => "Removes unfinished SSH key registrations after their verification time expires.";

    public async Task ExecuteAsync() => await keys.RemoveExpiredVerificationsAsync();
}
