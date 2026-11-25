namespace Vairas.ChatBackend.Startup;

/// <summary>Operator commands that run instead of the web host.</summary>
public static class StartupCommands
{
    public static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.Contains("--probe-model", StringComparer.Ordinal)) await SyntheticModelProbe.RunAsync();
        else if (args.Contains("--initialize", StringComparer.Ordinal)) await ChatProvisioning.InitializeAsync(args);
        else return false;
        return true;
    }
}
