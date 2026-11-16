namespace Kombin.Api.Common.Hosting;

/// <summary>The Hangfire configuration every feature's job registration reads.</summary>
internal static class HangfireSettings
{
    /// <summary>Nothing about Hangfire happens unless this is true (compose sets it; tests set it to false; Development defaults to true).</summary>
    public const string EnabledKey = "Hangfire:Enabled";
}
