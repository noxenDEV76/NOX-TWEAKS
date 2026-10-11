namespace NoxTweak.Core.Model;

public enum RiskLevel { Low, Medium, High }

public enum Tier { Safe, Advanced, Experimental }

/// <summary>How trustworthy a detected property is. Never show a value without one of these.</summary>
public enum FactStatus { Verified, Estimated, Unsupported, Unavailable, PermissionDenied }

public enum OptState { Applied, NotApplied, Partial, Unsupported }

public enum OpOutcome
{
    Success,
    Failed,
    RolledBack,
    Blocked,
}

public enum RestartNeed { None, SignOut, Recommended, Required }

public enum RegHive { CurrentUser, LocalMachine }

public enum RegKind { DWord, String, Binary }

public enum OptTab { Core, Privacy, Qol, Apps, Powerplan }

public enum OptPage { General, Advanced }

public enum Confidence { Low, Medium, High }

public enum PlanGroup { HighPriority, Recommended, Optional, Advanced, NotRecommended }

/// <summary>A detected value together with how reliable it is.</summary>
public sealed record Fact(string Value, FactStatus Status, string? Note = null)
{
    public static Fact Verified(string v, string? note = null) => new(v, FactStatus.Verified, note);
    public static Fact Estimated(string v, string? note = null) => new(v, FactStatus.Estimated, note);
    public static Fact Unavailable(string note) => new("Unavailable", FactStatus.Unavailable, note);
    public static Fact Unsupported(string note) => new("Unsupported", FactStatus.Unsupported, note);
    public static Fact Denied(string note) => new("Permission denied", FactStatus.PermissionDenied, note);

    public bool HasValue => Status is FactStatus.Verified or FactStatus.Estimated;

    public string StatusLabel => Status switch
    {
        FactStatus.Verified => "Verified",
        FactStatus.Estimated => "Estimated",
        FactStatus.Unsupported => "Unsupported",
        FactStatus.Unavailable => "Unavailable",
        FactStatus.PermissionDenied => "Permission denied",
        _ => ""
    };
}
