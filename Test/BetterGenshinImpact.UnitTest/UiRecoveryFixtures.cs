namespace BetterGenshinImpact.UnitTest;

// Recorded frames contain account identifiers and stay outside the public repository.
internal static class UiRecoveryFixtures
{
    internal static string PathFor(string file)
    {
        var configured = Environment.GetEnvironmentVariable("BGI_UI_RECOVERY_FIXTURE_ROOT");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.Combine(configured, file);
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BetterGenshinImpact.sln")))
                return Path.Combine(directory.FullName, ".cyaness", "fixtures", "ui-recovery-20261003", file);
        }
        return Path.Combine(AppContext.BaseDirectory, ".private-ui-fixtures", file);
    }

    internal static string? MissingReason(string[] files) => files.All(file => File.Exists(PathFor(file)))
        ? null : "Private recorded UI frames are unavailable; set BGI_UI_RECOVERY_FIXTURE_ROOT to run this replay.";
}

public sealed class UiRecoveryFactAttribute : FactAttribute
{
    public UiRecoveryFactAttribute(params string[] files) => Skip = UiRecoveryFixtures.MissingReason(files);
}

public sealed class UiRecoveryTheoryAttribute : TheoryAttribute
{
    public UiRecoveryTheoryAttribute(params string[] files) => Skip = UiRecoveryFixtures.MissingReason(files);
}
