namespace Re0Agent.Core.Services.Agent;

public sealed class GameProgressService
{
    public static bool AutoRetryGlobal { get; set; } = true;
    public static bool StreamingGlobal { get; set; }

    public bool IsBusy { get; private set; }
    public event Action? OnStateChanged;

    public void SetBusy(bool value)
    {
        IsBusy = value;
        OnStateChanged?.Invoke();
    }
}
