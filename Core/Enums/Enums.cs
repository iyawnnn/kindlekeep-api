namespace KindleKeep.Api.Core.Enums;

public enum AuthProvider
{
    GitHub,
    Google,
    GitLab
}

public enum UptimeStatus
{
    Healthy,
    Down,
    Degraded,
    Quarantined
}

public enum MonitorType
{
    Http,
    Journey
}