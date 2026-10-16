namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class RecordedFrameProvenanceTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("replaced")]
    [InlineData("cropped-and-rehashed")]
    [InlineData("run")]
    [InlineData("session")]
    [InlineData("sequence")]
    [InlineData("metadata-replaced")]
    [InlineData("original-missing")]
    [InlineData("duplicate")]
    public void ReplayRequiresOriginalContentAndTheExpectedRunIdentity(string mutation)
    {
        var root = Path.Combine(Path.GetTempPath(), "bgi-frame-provenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string file = "saurian-1610-blocked.png";
            var image = Path.Combine(root, "evidence-1582.png");
            var metadataPath = Path.ChangeExtension(image, ".json");
            var fixture = Path.Combine(root, file);
            File.WriteAllBytes(image, [1, 2, 3, 4]);
            File.Copy(image, fixture);
            var metadata = Newtonsoft.Json.Linq.JObject.Parse("""
                {"RunId":"33cad267-aecb-4655-a488-8859eef297c0","Source":{"SessionId":"5afdff11-ae74-4e93-a99c-20309fef1571","Sequence":201217,"IsKnown":true},"ImageFile":"evidence-1582.png"}
                """);
            File.WriteAllText(metadataPath, metadata.ToString());
            string Hash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            var entry = new Newtonsoft.Json.Linq.JObject
            {
                ["Fixture"] = file, ["RunId"] = (string?)metadata["RunId"],
                ["CaptureSession"] = (string?)metadata["Source"]!["SessionId"], ["SourceSequence"] = 201217,
                ["ImagePath"] = image, ["MetadataPath"] = metadataPath,
                ["MetadataSha256"] = Hash(metadataPath), ["ImageSha256"] = Hash(image), ["FixtureSha256"] = Hash(fixture)
            };
            switch (mutation)
            {
                case "replaced": File.WriteAllBytes(fixture, [8, 9]); break;
                case "cropped-and-rehashed": File.WriteAllBytes(fixture, [1, 2]); entry["FixtureSha256"] = Hash(fixture); break;
                case "run": entry["RunId"] = Guid.NewGuid().ToString(); break;
                case "session": entry["CaptureSession"] = Guid.NewGuid().ToString(); break;
                case "sequence": entry["SourceSequence"] = 201218; break;
                case "metadata-replaced": metadata["Source"]!["Sequence"] = 201218; File.WriteAllText(metadataPath, metadata.ToString()); entry["MetadataSha256"] = Hash(metadataPath); break;
                case "original-missing": File.Delete(image); break;
            }
            var frames = new Newtonsoft.Json.Linq.JArray(entry);
            if (mutation == "duplicate") frames.Add(entry.DeepClone());
            File.WriteAllText(Path.Combine(root, "recorded-20261016.manifest.json"),
                new Newtonsoft.Json.Linq.JObject { ["Version"] = 1, ["Frames"] = frames }.ToString());
            var ok = RecordedFrameProvenance.TryRead(fixture, out var bytes, out var reason);
            Assert.Equal(mutation == "valid", ok);
            if (ok) { Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes); Assert.Null(reason); }
            else { Assert.Null(bytes); Assert.Contains("evidence-unavailable", reason); }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SameNamedFrameWithoutManifestCannotQualifyAsRecordedEvidence()
    {
        // This test owns only its unique temporary fixture directory.
        var root = Path.Combine(Path.GetTempPath(), "bgi-frame-provenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "saurian-1610-blocked.png"), "substituted pixels");
            var reason = UiRecoveryFixtures.MissingReason(["saurian-1610-blocked.png"], file => Path.Combine(root, file));
            Assert.NotNull(reason);
            Assert.Contains("evidence-unavailable", reason);
        }
        finally { Directory.Delete(root, true); }
    }
}
