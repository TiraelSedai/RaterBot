using Shouldly;
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
