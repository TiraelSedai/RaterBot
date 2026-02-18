using Shouldly;
using OpenCvSharp;
using RaterBot;

namespace RaterBot.Tests.Unit;

public class VectorSearchServiceTests
{
    [Fact]
    public void FloatsToInt8Bytes_And_BytesToFloats_RoundTrip()
    {
        var original = new float[] { 0.5f, -0.5f, 0.0f, 0.99f, -0.99f, 0.123f, -0.456f };
        var bytes = FloatsToInt8Bytes(original);
        var restored = BytesToFloats(bytes, new DateTime(2026, 2, 1));

        for (var i = 0; i < original.Length; i++)
        {
            Math.Abs(restored[i] - original[i]).ShouldBeLessThan(0.01f);
        }
    }

    [Fact]
    public void FloatsToInt8Bytes_ClampsValues()
    {
        var floats = new float[] { 1.5f, -1.5f, 2.0f, -2.0f };
        var bytes = FloatsToInt8Bytes(floats);
        var restored = BytesToFloats(bytes, new DateTime(2026, 2, 1));

        Math.Abs(restored[0] - 1.0f).ShouldBeLessThan(0.01f);
        Math.Abs(restored[1] - (-1.0f)).ShouldBeLessThan(0.01f);
        Math.Abs(restored[2] - 1.0f).ShouldBeLessThan(0.01f);
        Math.Abs(restored[3] - (-1.0f)).ShouldBeLessThan(0.01f);
    }

    [Fact]
    public void BytesToFloats_BeforeCutoff_InterpretsAsFloat32()
    {
        var original = new float[] { 0.123f, -0.456f };
        var bytes = new byte[original.Length * sizeof(float)];
        Buffer.BlockCopy(original, 0, bytes, 0, bytes.Length);

        var beforeCutoff = new DateTime(2026, 1, 28, 9, 59, 59, DateTimeKind.Utc);
        var restored = BytesToFloats(bytes, beforeCutoff);

        restored[0].ShouldBe(original[0]);
        restored[1].ShouldBe(original[1]);
    }

    [Fact]
    public void BytesToFloats_AfterCutoff_InterpretsAsInt8()
    {
        var bytes = new byte[] { 64, 192, 0, 127, 129 };
        var afterCutoff = new DateTime(2026, 1, 29, 10, 1, 0, DateTimeKind.Utc);
        var restored = BytesToFloats(bytes, afterCutoff);

        Math.Abs(restored[0] - 64 / 127f).ShouldBeLessThan(0.001f);
        Math.Abs(restored[1] - (-64 / 127f)).ShouldBeLessThan(0.001f);
        Math.Abs(restored[2] - 0f).ShouldBeLessThan(0.001f);
        Math.Abs(restored[3] - 127 / 127f).ShouldBeLessThan(0.001f);
        Math.Abs(restored[4] - (-127 / 127f)).ShouldBeLessThan(0.001f);
    }

    [Fact]
    public void FloatsToInt8Bytes_ReturnsCorrectLength()
    {
        var floats = new float[512];
        var random = new Random(42);
        for (var i = 0; i < floats.Length; i++)
            floats[i] = (float)(random.NextDouble() * 2 - 1);

        var bytes = FloatsToInt8Bytes(floats);
        bytes.Length.ShouldBe(512);
    }

    [Fact]
    public void NormalizeOcrText_RemovesPunctuationAndCollapsesWhitespace()
    {
        var normalized = VectorSearchService.NormalizeOcrText("Hello,\nWORLD!!!   #42");
        normalized.ShouldBe("hello world 42");
    }

    [Fact]
    public void TokenDiceSimilarity_HighForCloseText()
    {
        var similarity = VectorSearchService.TokenDiceSimilarity(
            "Breaking news from twitter screenshot",
            "breaking news from Twitter screenshot dark theme"
        );

        similarity.ShouldBeGreaterThanOrEqualTo(0.80f);
    }

    [Fact]
    public void TokenDiceSimilarity_LowForDifferentText()
    {
        var similarity = VectorSearchService.TokenDiceSimilarity(
            "dogs and cats",
            "sql migrations and telegram bots"
        );

        similarity.ShouldBeLessThan(0.80f);
    }

    [Fact]
    public void ShouldUseOcrRoute_TrueForReliableTextHeavyInput()
    {
        var shouldUse = VectorSearchService.ShouldUseOcrRoute(
            textCoverageRatio: 0.30f,
            normalizedText: "this is enough normalized text for ocr mode",
            ocrAvgConfidence: 72f
        );

        shouldUse.ShouldBeTrue();
    }

    [Fact]
    public void ShouldUseOcrRoute_FalseForLowConfidence()
    {
        var shouldUse = VectorSearchService.ShouldUseOcrRoute(
            textCoverageRatio: 0.30f,
            normalizedText: "this is enough normalized text for ocr mode",
            ocrAvgConfidence: 40f
        );

        shouldUse.ShouldBeFalse();
    }

    [Fact]
    public void DecodeEastCandidates_DecodesSingleExpectedBox()
    {
        const int mapHeight = 2;
        const int mapWidth = 2;
        var scores = new float[mapHeight * mapWidth];
        var geometry = new float[scores.Length * 5];

        var offset = 3; // y = 1, x = 1
        scores[offset] = 0.9f;
        geometry[offset] = 1f; // top
        geometry[scores.Length + offset] = 2f; // right
        geometry[scores.Length * 2 + offset] = 1f; // bottom
        geometry[scores.Length * 3 + offset] = 2f; // left
        geometry[scores.Length * 4 + offset] = 0f; // angle

        var candidates = VectorSearchService.DecodeEastCandidates(
            scores,
            geometry,
            mapHeight,
            mapWidth,
            imageWidth: 320,
            imageHeight: 320,
            scoreThreshold: 0.35f
        );

        candidates.Count.ShouldBe(1);
        var rect = candidates[0].Box;
        Math.Abs(rect.X - 2f).ShouldBeLessThan(0.001f);
        Math.Abs(rect.Y - 3f).ShouldBeLessThan(0.001f);
        Math.Abs(rect.Width - 4f).ShouldBeLessThan(0.001f);
        Math.Abs(rect.Height - 2f).ShouldBeLessThan(0.001f);
    }

    [Fact]
    public void ApplyNms_RemovesHighlyOverlappingLowerConfidenceBox()
    {
        var candidates = new List<VectorSearchService.EastCandidate>
        {
            new(new Rect2f(0, 0, 10, 10), 0.95f),
            new(new Rect2f(1, 1, 10, 10), 0.85f),
            new(new Rect2f(20, 20, 5, 5), 0.70f),
        };

        var boxes = VectorSearchService.ApplyNms(candidates, iouThreshold: 0.30f);

        boxes.Count.ShouldBe(2);
        boxes.ShouldContain(x => Math.Abs(x.X) < 0.001f && Math.Abs(x.Y) < 0.001f);
        boxes.ShouldContain(x => Math.Abs(x.X - 20f) < 0.001f && Math.Abs(x.Y - 20f) < 0.001f);
    }

    [Fact]
    public void ComputeUnionCoverageRatio_HandlesOverlapWithoutDoubleCounting()
    {
        var boxes = new List<Rect2f>
        {
            new(0, 0, 10, 10),
            new(5, 0, 10, 10),
        };

        var ratio = VectorSearchService.ComputeUnionCoverageRatio(boxes, imageWidth: 20, imageHeight: 10);

        Math.Abs(ratio - 0.75f).ShouldBeLessThan(0.001f);
    }

    [Fact]
    public void ComputeUnionCoverageRatio_ClampsOutOfBoundsToImageArea()
    {
        var boxes = new List<Rect2f>
        {
            new(-5, -5, 10, 10),
        };

        var ratio = VectorSearchService.ComputeUnionCoverageRatio(boxes, imageWidth: 10, imageHeight: 10);

        Math.Abs(ratio - 0.25f).ShouldBeLessThan(0.001f);
    }

    [Fact]
    public void HasTwoDistinctFrameMatches_TrueWhenTwoPairsMatch()
    {
        var incoming = new List<float[]>
        {
            new float[] { 1f, 0f },
            new float[] { 0f, 1f },
            new float[] { 0.7f, 0.7f },
        };
        var candidate = new List<float[]>
        {
            new float[] { 1f, 0f },
            new float[] { 0f, 1f },
        };

        var result = VectorSearchService.HasTwoDistinctFrameMatches(incoming, candidate, threshold: 0.96f);

        result.ShouldBeTrue();
    }

    [Fact]
    public void HasTwoDistinctFrameMatches_FalseWhenOnlyOnePairMatches()
    {
        var incoming = new List<float[]>
        {
            new float[] { 1f, 0f },
            new float[] { 1f, 0f },
        };
        var candidate = new List<float[]>
        {
            new float[] { 1f, 0f },
            new float[] { 0f, 1f },
        };

        var result = VectorSearchService.HasTwoDistinctFrameMatches(incoming, candidate, threshold: 0.96f);

        result.ShouldBeFalse();
    }

    [Fact]
    public void HasTwoDistinctFrameMatches_FalseWhenCandidateHasSingleFrame()
    {
        var incoming = new List<float[]>
        {
            new float[] { 1f, 0f },
            new float[] { 0f, 1f },
        };
        var candidate = new List<float[]>
        {
            new float[] { 1f, 0f },
        };

        var result = VectorSearchService.HasTwoDistinctFrameMatches(incoming, candidate, threshold: 0.96f);

        result.ShouldBeFalse();
    }

    private static byte[] FloatsToInt8Bytes(float[] floats)
    {
        var bytes = new byte[floats.Length];
        for (var i = 0; i < floats.Length; i++)
            bytes[i] = (byte)(sbyte)Math.Clamp((int)MathF.Round(floats[i] * 127f), -127, 127);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes, DateTime timestamp)
    {
        var quantCutoff = new DateTime(2026, 1, 29, 10, 0, 0, DateTimeKind.Utc);
        if (timestamp < quantCutoff)
        {
            var floats = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }
        else
        {
            var floats = new float[bytes.Length];
            for (var i = 0; i < bytes.Length; i++)
                floats[i] = (sbyte)bytes[i] / 127f;
            return floats;
        }
    }
}
