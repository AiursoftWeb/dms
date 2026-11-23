using Aiursoft.DMS.Agent;

if (args is [AgentAptTransaction.VerifyArgument]) return AgentAptTransaction.Verify();
if (args.Length != 0) return await AgentCommandApp.Create().RunAsync(args);
await AgentRuntime.RunEnvironmentAsync();
return 0;
