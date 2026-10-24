namespace BrightnessTray.Windows;

internal sealed record DisplayEndpoint(string Name, string InterfaceName);

internal static class DisplayEndpointMatcher
{
    internal static DisplayEndpoint?[] Match(IReadOnlyList<string> descriptions,
        IReadOnlyList<DisplayEndpoint> endpoints)
    {
        var matches = new DisplayEndpoint?[descriptions.Count];
        var claimed = new HashSet<int>();
        for (var index = 0; index < descriptions.Count; index++)
        {
            var description = descriptions[index].Trim();
            if (description.Length == 0 || descriptions.Count(value =>
                    string.Equals(value.Trim(), description, StringComparison.OrdinalIgnoreCase)) != 1) continue;
            var candidates = Enumerable.Range(0, endpoints.Count).Where(endpoint =>
                string.Equals(endpoints[endpoint].Name.Trim(), description, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 1 && claimed.Add(candidates[0]))
                matches[index] = endpoints[candidates[0]];
        }

        // Drivers commonly keep physical handles in EnumDisplayDevices order, including clones.
        // This is a best-effort fallback, never a positional join to QueryDisplayConfig paths.
        // A count mismatch or an already claimed endpoint leaves the handle unidentified.
        if (descriptions.Count == endpoints.Count)
        {
            for (var index = 0; index < descriptions.Count; index++)
                if (matches[index] is null && claimed.Add(index)) matches[index] = endpoints[index];
        }
        return matches;
    }
}
