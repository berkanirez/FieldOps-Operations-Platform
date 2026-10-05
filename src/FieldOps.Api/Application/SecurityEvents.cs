namespace FieldOps.Api.Application;

// Day 123 (SECURITY_REVIEW.md F12): security-relevant events are logged under
// one category with a structured SecurityEvent field, so they can be counted
// and alerted on with a single query (e.g. in Log Analytics, Day 109:
// ContainerAppConsoleLogs_CL | where Log_s has "FieldOps.Security"), instead
// of being indistinguishable from ordinary request logs.
public static class SecurityEvents
{
    public const string Category = "FieldOps.Security";

    public const string LoginFailed = "LoginFailed";
    public const string LoginSucceeded = "LoginSucceeded";
    public const string RateLimitExceeded = "RateLimitExceeded";
}
