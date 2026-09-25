using System.Text.Json;

namespace PersonalAgent.Contracts.Automations;

public static class AutomationPackageLock
{
    public static string[] ResolvedPackages(string PackageLock)
    {
        if (PackageLock.Length > 20000) throw new ArgumentException("Package lock exceeds its size limit.");
        try
        {
            using var Document = JsonDocument.Parse(PackageLock, new() { MaxDepth = 16 });
            var Dependencies = Document.RootElement.GetProperty("dependencies");
            if (Dependencies.ValueKind != JsonValueKind.Object || Dependencies.EnumerateObject().Count() != 1
                || !Dependencies.TryGetProperty("net11.0", out var Framework)) throw new ArgumentException("The package lock must target net11.0 only.");
            var Packages = new List<string>();
            foreach (var Package in Framework.EnumerateObject())
            {
                var Item = Package.Value;
                if (Item.GetProperty("type").GetString() is not ("Direct" or "Transitive") || Item.GetProperty("contentHash").GetString() is not { Length: > 0 })
                    throw new ArgumentException("Every dependency needs a NuGet content hash.");
                Packages.Add(Package.Name + "@" + Item.GetProperty("resolved").GetString());
            }
            if (Packages.Count is < 1 or > 50) throw new ArgumentException("A lock requires 1–50 dependencies.");
            return Packages.ToArray();
        }
        catch (Exception E) when (E is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new ArgumentException("Invalid NuGet package lock."); }
    }
}
