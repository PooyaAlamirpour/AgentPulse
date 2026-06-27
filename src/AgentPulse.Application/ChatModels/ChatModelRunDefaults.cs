namespace AgentPulse.Application.ChatModels;

public sealed class ChatModelRunDefaults
{
    public ChatModelRunDefaults(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        Model = model.Trim();
    }

    public string Model { get; }
}
