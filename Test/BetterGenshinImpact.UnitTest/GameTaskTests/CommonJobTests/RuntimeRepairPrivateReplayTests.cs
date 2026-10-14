using System.Security.Cryptography;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Newtonsoft.Json.Linq;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class RuntimeRepairPrivateReplayTests(ITestOutputHelper output)
{
    [RuntimeRepairReplayFact]
    public async Task ManifestBoundPrivatePixelsExerciseTheCandidateClassifierWithoutInventingMissingAnchors()
    {
        var manifestPath = Environment.GetEnvironmentVariable("BGI_RUNTIME_REPAIR_MANIFEST");
        Assert.True(File.Exists(manifestPath), "evidence-unavailable: set BGI_RUNTIME_REPAIR_MANIFEST; no skipped replay can count as success");
        Assert.Equal("C8F1B50694D8A3AF7D9FAC59278C8911787BA1DE6A7ECFDE67BE84D6523C8EEB",
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath!))));
        var manifest = JObject.Parse(File.ReadAllText(manifestPath!));
        var entries = (JArray)manifest["entries"]!;
        Assert.Equal(8, entries.Count);
        Assert.Equal(8, entries.Select(entry => (string)entry["id"]!).Distinct().Count());
        // Verify the private snapshot, including its logs, before executing any replay.
        foreach (var item in (JArray)manifest["support"]!)
            VerifyFile((string)item["path"]!, (string)item["sha256"]!);
        foreach (var entry in entries)
        foreach (var field in new[] { "image", "metadata" })
        {
            if (entry[field] == null) continue;
            var path = (string)entry[field]!;
            VerifyFile(path, (string)entry[field == "image" ? "sha256" : "metadata_sha256"]!);
            if (field == "metadata")
                Assert.Equal(entry["source"]!.ToObject<CaptureFrameStamp>(),
                    JObject.Parse(File.ReadAllText(path))["Source"]!.ToObject<CaptureFrameStamp>());
        }
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var loaded = 0;
        var replayed = 0;
        foreach (var entry in entries)
        {
            using var image = new ImageRegion(Cv2.ImRead((string)entry["image"]!), 0, 0)
            { FrameStamp = entry["source"]?.ToObject<CaptureFrameStamp>() ?? new CaptureFrameSource().Next() };
            Assert.False(image.SrcMat.Empty(), "evidence-unavailable: image decode failed");
            loaded++;
            var kind = (string)entry["kind"]!;
            if (kind == "context-only")
            {
                output.WriteLine($"entry={entry["id"]} loaded as context only, not replayed or substituted for missing decision pixels");
                continue;
            }
            var emptyOcr = new ReplayOcr("");
            var result = WorldFrameAvailability.ReadConnectionWait(image, emptyOcr);
            output.WriteLine($"entry={entry["id"]} source={(entry["source"] != null ? "incident-neighbor" : "historical-unknown-identity")} geometry={result ?? "no-candidate"} ocrCalls={emptyOcr.Calls}; OCR is an explicit stub, not real inference");
            if (kind is "reconnect" or "map-reconnect")
            {
                Assert.Equal("connection-panel-unconfirmed", result);
                Assert.Equal("reconnect-panel", WorldFrameAvailability.ReadConnectionWait(image, new ReplayOcr("重新连接服务器")));
            }
            if (kind == "server-wait") Assert.Equal("server-wait", result);
            if (kind is "dark-world" or "mail-neighbor") Assert.Null(result);
            replayed++;
        }
        await scope.DisposeAsync();
        Assert.Equal(8, loaded);
        Assert.Equal(5, replayed);
        Assert.Contains(saved, item => item.Phase == "connection-candidate" && item.Fields!["classification"] == "connection-panel-unconfirmed");
        output.WriteLine("Loaded=8; geometryReplay=5; contextOnly=3; missing anchors108/949/121/2623 not substituted; recorded dialogue replay=evidence-unavailable (accepted residual risk, not passed); real OCR and runtime roots remain unclosed.");
    }

    // Spec 1.3.0: retain this replay for restored originals, but never count it as passed
    // when those pixels are unavailable. Required synthetic dialogue safety tests remain separate.
    [RecordedDialogueReplayFact]
    [Trait("Evidence", "OptionalRecordedDialogue")]
    public void RestoredRecordedDialoguePixelsMustStillMeetTheOriginalSafetyAssertions()
    {
        var manifestPath = Environment.GetEnvironmentVariable("BGI_RUNTIME_REPAIR_MANIFEST")!;
        var legacyPath = Path.Combine(Path.GetDirectoryName(manifestPath)!, "private-replay-manifest.json");
        VerifyFile(legacyPath, "6DA497AC27040589AAB0A7F305FE2322FFBC9435E92B246FD49A6EFC409CEB61");
        var entries = (JArray)JObject.Parse(File.ReadAllText(legacyPath))["entries"]!;
        foreach (var entry in entries.Skip(2).Take(2))
        {
            VerifyFile((string)entry["image"]!, (string)entry["sha256"]!);
            VerifyFile((string)entry["metadata"]!, (string)entry["metadata_sha256"]!);
            using var image = new ImageRegion(Cv2.ImRead((string)entry["image"]!), 0, 0)
            { FrameStamp = entry["source"]!.ToObject<CaptureFrameStamp>() };
            Assert.False(image.SrcMat.Empty(), "evidence-unavailable: recorded dialogue decode");
            var dialogue = DialogueUiReader.Read(image, new DialogueTextStub());
            Assert.True(dialogue.Confirmed, "Recorded dialogue could not be read with explicit OCR boundary substitute");
            Assert.True(dialogue.CanAdvance || dialogue.ExitBounds != null, "Recorded dialogue has no safe action evidence");
        }
        output.WriteLine("Recorded dialogue replay=2; body OCR substituted, not real inference.");
    }

    private static void VerifyFile(string path, string hash)
    {
        Assert.True(File.Exists(path), "evidence-unavailable: " + path);
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private sealed class ReplayOcr(string text) : IOcrService
    {
        internal int Calls;
        public string Ocr(Mat mat) { Calls++; return text; }
        public string OcrWithoutDetector(Mat mat) { Calls++; return text; }
        public OcrResult OcrResult(Mat mat) => new([]);
    }
    private sealed class DialogueTextStub : IOcrService
    {
        public string Ocr(Mat mat) => mat.Height < 150 ? "已确认对话正文" : "";
        public string OcrWithoutDetector(Mat mat) => Ocr(mat);
        public OcrResult OcrResult(Mat mat) => new([]);
    }
}

public sealed class RecordedDialogueReplayFactAttribute : FactAttribute
{
    public RecordedDialogueReplayFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BGI_RUNTIME_REPAIR_RECORDED_DIALOGUE") != "1")
            Skip = "evidence-unavailable: recorded dialogue originals not replayed; Spec 1.3.0 accepted residual risk, not a pass; set BGI_RUNTIME_REPAIR_RECORDED_DIALOGUE=1 to verify restored originals";
    }
}

public sealed class RuntimeRepairReplayFactAttribute : FactAttribute
{
    public RuntimeRepairReplayFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("BGI_RUNTIME_REPAIR_MANIFEST")))
            Skip = "evidence-unavailable: private manifest not supplied; this skip is not replay success or not-reproduced";
    }
}
