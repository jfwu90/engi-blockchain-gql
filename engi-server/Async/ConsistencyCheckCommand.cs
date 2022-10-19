namespace Engi.Substrate.Server.Async;

public class ConsistencyCheckCommand
{
    public string Id { get; set; } = null!;

    public DateTime? LastExecutedOn { get; set; }

    public long? LastRecovered { get; set; }
}