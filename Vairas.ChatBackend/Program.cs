using Vairas.ChatBackend;
using Vairas.ChatBackend.Startup;

if (await StartupCommands.TryRunAsync(args)) return;
await ChatApplication.Build(args).RunAsync();
public partial class Program;
