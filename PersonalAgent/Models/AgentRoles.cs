namespace PersonalAgent.Models;

internal static class AgentRoles
{
    public const string Owner = "Owner";
    public const string Coach = "Coach";

    public static readonly string[] Defined = [Owner, Coach];

    public static bool IsDefined(string role) => Defined.Contains(role, StringComparer.Ordinal);
}
