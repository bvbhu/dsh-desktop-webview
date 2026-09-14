namespace DshDesktop.Domain;

public enum ServiceStrategy
{
    NeverStart,
    ProbeThenStart,
    AlwaysStart,
}

public enum SessionPhase
{
    Idle,
    Probing,
    Launching,
    NeedsToken,
    Ready,
    Failed,
}

public enum ProbeOutcome
{
    Ok,
    Unreachable,
    Other,
}

public enum ServiceAction
{
    Navigate,
    Launch,
    OpenConfigForToken,
}

public enum ChromeButtonVisibility
{
    Hidden,
    Shown,
}
