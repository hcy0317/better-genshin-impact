using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest;

// Only the incident's seven private source frames, not a general evidence framework.
internal static class RecordedFrameProvenance
{
    private const string AfternoonRun = "33cad267-aecb-4655-a488-8859eef297c0";
    private const string AfternoonSession = "5afdff11-ae74-4e93-a99c-20309fef1571";
    private const string NoonRun = "bdabd798-6166-4d49-9ee0-9e24f7e4652a";
    private const string NoonSession = "c25bec69-9b89-49d0-8f49-03294f4250f0";
    private sealed record Source(string Run, string Session, long Sequence, string Image);
    private static readonly IReadOnlyDictionary<string, Source> Sources = new Dictionary<string, Source>
    {
        ["saurian-1610-blocked.png"] = new(AfternoonRun, AfternoonSession, 201217, "evidence-1582.png"),
        ["saurian-1610-recovered.png"] = new(AfternoonRun, AfternoonSession, 219254, "evidence-1744.png"),
        ["saurian-1610-approach.png"] = new(AfternoonRun, AfternoonSession, 205497, "evidence-1598.png"),
        ["saurian-1610-before.png"] = new(AfternoonRun, AfternoonSession, 201195, "evidence-1592.png"),
        ["saurian-1610-after.png"] = new(AfternoonRun, AfternoonSession, 201383, "evidence-1595.png"),
        ["saurian-entry-20261016.png"] = new(NoonRun, NoonSession, 615647, "evidence-4692.png"),
        ["saurian-post-20261016.png"] = new(NoonRun, NoonSession, 616449, "evidence-4695.png")
    };

    internal static bool IsBound(string file) => Sources.ContainsKey(file);

    internal static bool TryRead(string fixturePath, out byte[]? pixels, out string? reason)
    {
        pixels = null;
        reason = null;
        try
        {
            var file = Path.GetFileName(fixturePath);
            if (!Sources.TryGetValue(file, out var expected)) throw new InvalidDataException("unbound frame");
            var manifest = JObject.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(fixturePath)!, "recorded-20261016.manifest.json")));
            if ((int?)manifest["Version"] != 1) throw new InvalidDataException("manifest version");
            var entries = ((JArray?)manifest["Frames"] ?? throw new InvalidDataException("frames missing"))
                .OfType<JObject>().Where(entry => (string?)entry["Fixture"] == file).ToArray();
            if (entries.Length != 1) throw new InvalidDataException("missing or duplicate binding");
            var entry = entries[0];
            if ((string?)entry["RunId"] != expected.Run || (string?)entry["CaptureSession"] != expected.Session ||
                (long?)entry["SourceSequence"] != expected.Sequence)
                throw new InvalidDataException("wrong run/session/sequence");
            var imagePath = (string?)entry["ImagePath"] ?? throw new InvalidDataException("image path missing");
            var metadataPath = (string?)entry["MetadataPath"] ?? throw new InvalidDataException("metadata path missing");
            if (!Path.IsPathFullyQualified(imagePath) || !Path.IsPathFullyQualified(metadataPath) ||
                Path.GetFileName(imagePath) != expected.Image ||
                !string.Equals(Path.ChangeExtension(imagePath, ".json"), metadataPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("source file mismatch");
            var metadataBytes = File.ReadAllBytes(metadataPath);
            Verify(metadataBytes, (string?)entry["MetadataSha256"]);
            var metadata = JObject.Parse(System.Text.Encoding.UTF8.GetString(metadataBytes).TrimStart('\uFEFF'));
            if ((string?)metadata["RunId"] != expected.Run || (string?)metadata["Source"]?["SessionId"] != expected.Session ||
                (long?)metadata["Source"]?["Sequence"] != expected.Sequence ||
                (bool?)metadata["Source"]?["IsKnown"] != true || (string?)metadata["ImageFile"] != expected.Image)
                throw new InvalidDataException("source metadata identity mismatch");
            var original = File.ReadAllBytes(imagePath);
            Verify(original, (string?)entry["ImageSha256"]);
            var candidate = File.ReadAllBytes(fixturePath);
            Verify(candidate, (string?)entry["FixtureSha256"]);
            if (!candidate.AsSpan().SequenceEqual(original)) throw new InvalidDataException("fixture is not the original frame");
            pixels = candidate;
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Newtonsoft.Json.JsonException or
                                     FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            reason = "evidence-unavailable: " + Path.GetFileName(fixturePath) + ": " + error.Message;
            return false;
        }
    }

    private static void Verify(byte[] bytes, string? expected)
    {
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("source content hash mismatch");
    }
}
