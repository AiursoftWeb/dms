using Aiursoft.DMS.Agent;

if (args.Length != 0) return await AgentCommandApp.Create().RunAsync(args);
await AgentRuntime.RunEnvironmentAsync();
return 0;
