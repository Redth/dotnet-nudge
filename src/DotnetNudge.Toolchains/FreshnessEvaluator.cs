namespace DotnetNudge.Toolchains;

public static class FreshnessEvaluator
{
    public static AnalysisResult<T> Apply<T>(AnalysisResult<T> result, FreshnessCriteria criteria)
    {
        if (criteria.MaximumAge < TimeSpan.Zero)
        {
            return Unknown("freshness.policy-invalid", "Maximum evidence age cannot be negative.");
        }

        if (result.Evidence.Count == 0)
        {
            return Unknown("evidence.missing", "No source evidence was supplied for this analysis.");
        }

        var stale = result.Evidence
            .Where(x => x.RetrievedAt > criteria.AsOf
                || criteria.AsOf - x.RetrievedAt > criteria.MaximumAge)
            .ToArray();
        if (stale.Length == 0)
        {
            return result;
        }

        var diagnostics = stale.Select(x =>
            x.RetrievedAt > criteria.AsOf
                ? new Diagnostic(
                    "evidence.from-future",
                    $"Evidence '{x.Source}' was retrieved after the analysis time {criteria.AsOf:O}.",
                    Assessment.Unknown)
                : new Diagnostic(
                    "evidence.expired",
                    $"Evidence '{x.Source}' is older than the caller-supplied maximum age of {criteria.MaximumAge}.",
                    Assessment.Unknown));
        return result with
        {
            Assessment = Assessment.Unknown,
            Value = default,
            Diagnostics = result.Diagnostics.Concat(diagnostics).ToArray()
        };

        AnalysisResult<T> Unknown(string code, string message) =>
            result with
            {
                Assessment = Assessment.Unknown,
                Value = default,
                Diagnostics = result.Diagnostics.Append(new(code, message, Assessment.Unknown)).ToArray()
            };
    }
}
