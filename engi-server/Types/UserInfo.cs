namespace Engi.Substrate.Server.Types;

public class UserInfo
{
    public string Address { get; set; } = null!;

    public string? Display { get; set; } = null!;

    public string? ProfileImageUrl { get; set; }

    public DateTime? CreatedOn { get; set; }

    public int CreatedJobsCount { get; set; }

    public int SolvedJobsCount { get; set; }
}
