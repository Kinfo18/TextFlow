using System.Buffers.Binary;
using System.Text;
using TextFlow.Contracts.Voice;

namespace TextFlow.Core.Tests.Voice;

public class VoiceFrameTests
{
    public static TheoryData<VoiceMessage> Messages() =>
    [
        new HelloMessage(VoiceProtocol.Version),
        new ReadyMessage(VoiceProtocol.Version, 4242),
        new PingMessage(long.MaxValue),
        new PongMessage(7),
        new ShutdownMessage(),
    ];

    [Theory]
    [MemberData(nameof(Messages))]
    public async Task EveryMessage_RoundTrips(VoiceMessage message)
    {
        using var stream = new MemoryStream();

        await VoiceFrame.WriteAsync(stream, message, CancellationToken.None);
        stream.Position = 0;
        var read = await VoiceFrame.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(message, read);
    }

    [Fact]
    public async Task SeveralFrames_ReadInOrder_ThenNullAtEndOfStream()
    {
        using var stream = new MemoryStream();
        await VoiceFrame.WriteAsync(stream, new PingMessage(1), CancellationToken.None);
        await VoiceFrame.WriteAsync(stream, new PingMessage(2), CancellationToken.None);
        stream.Position = 0;

        Assert.Equal(new PingMessage(1), await VoiceFrame.ReadAsync(stream, CancellationToken.None));
        Assert.Equal(new PingMessage(2), await VoiceFrame.ReadAsync(stream, CancellationToken.None));
        Assert.Null(await VoiceFrame.ReadAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task StreamCutInsideAFrame_IsInvalidData()
    {
        using var whole = new MemoryStream();
        await VoiceFrame.WriteAsync(whole, new PingMessage(1), CancellationToken.None);
        using var cut = new MemoryStream(whole.ToArray()[..^2]);

        await Assert.ThrowsAsync<InvalidDataException>(() => VoiceFrame.ReadAsync(cut, CancellationToken.None));
    }

    [Fact]
    public async Task StreamCutInsideTheHeader_IsInvalidData()
    {
        using var cut = new MemoryStream([1, 0]);

        await Assert.ThrowsAsync<InvalidDataException>(() => VoiceFrame.ReadAsync(cut, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(VoiceFrame.MaxPayloadBytes + 1)]
    public async Task ImpossibleLength_IsInvalidData_WithoutAllocatingIt(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<InvalidDataException>(() => VoiceFrame.ReadAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"type":"launch-missiles"}""")]
    [InlineData("""{"id":1}""")]
    [InlineData("not json")]
    public async Task UnknownOrBrokenJson_IsInvalidData(string json)
    {
        using var stream = Frame(json);

        await Assert.ThrowsAsync<InvalidDataException>(() => VoiceFrame.ReadAsync(stream, CancellationToken.None));
    }

    private static MemoryStream Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return new MemoryStream(frame);
    }
}
