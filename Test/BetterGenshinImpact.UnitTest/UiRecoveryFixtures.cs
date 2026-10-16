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

    internal static string? MissingReason(string[] files, Func<string, string>? pathFor = null)
    {
        pathFor ??= PathFor;
        foreach (var file in files)
        {
            if (!File.Exists(pathFor(file))) return "evidence-unavailable: private UI frame missing: " + file;
            if (RecordedFrameProvenance.IsBound(file) &&
                RecordedFrameProvenance.TryRead(pathFor(file), out _, out var reason) == false)
                return reason;
        }
        return null;
    }

    // Recheck at use, not only xUnit discovery; decode the exact bytes that were verified.
    internal static OpenCvSharp.Mat ReadRecordedFrame(string file)
    {
        if (!RecordedFrameProvenance.TryRead(PathFor(file), out var bytes, out var reason))
            throw new InvalidDataException(reason);
        return OpenCvSharp.Cv2.ImDecode(bytes!, OpenCvSharp.ImreadModes.Color);
    }
}

public sealed class UiRecoveryFactAttribute : FactAttribute
{
    public UiRecoveryFactAttribute(params string[] files) => Skip = UiRecoveryFixtures.MissingReason(files);
}

public sealed class UiRecoveryTheoryAttribute : TheoryAttribute
{
    public UiRecoveryTheoryAttribute(params string[] files) => Skip = UiRecoveryFixtures.MissingReason(files);
}
