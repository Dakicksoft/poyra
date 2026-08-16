namespace Poyra.Modules.Disputes.Contracts;

public static class KnownDisputeEvents
{
    public const string Opened = "dispute.opened";
    public const string EvidenceDueSoon = "dispute.evidence_due_soon";
    public const string Submitted = "dispute.evidence_submitted";
    public const string Won = "dispute.won";
    public const string Lost = "dispute.lost";
    public const string Expired = "dispute.expired";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Opened, EvidenceDueSoon, Submitted, Won, Lost, Expired,
    };
}
