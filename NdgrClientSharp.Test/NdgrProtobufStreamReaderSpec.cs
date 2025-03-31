using NdgrClientSharp.Utilities;

namespace NdgrClientSharp.Test;

public class NdgrProtobufStreamReaderSpec
{
    private byte[] EncodeVarint(ulong value)
    {
        using var ms = new MemoryStream();
        while (value > 0x7F)
        {
            ms.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        ms.WriteByte((byte)value);
        return ms.ToArray();
    }

    [TestCase(0u)]
    [TestCase(1u)]
    [TestCase(127u)]
    [TestCase(128u)]
    [TestCase(300u)]
    [TestCase(uint.MaxValue)]
    public void ReadVarint_uintの範囲内を正しく読める(uint expected)
    {
        var reader = new NdgrProtobufStreamReader();

        // encodeして戻す
        var encoded = EncodeVarint(expected);

        reader.AddNewChunk(encoded);
        var result = reader.ReadVarint();

        Assert.IsNotNull(result);
        Assert.That(result.Value.result, Is.EqualTo(expected));
    }

    [TestCase(((ulong)123456 << 32) + 0u, 0u)]
    [TestCase(((ulong)123456 << 32) + 1u, 1u)]
    [TestCase(((ulong)123456 << 32) + 127u, 127u)]
    [TestCase(((ulong)123456 << 32) + 128u, 128u)]
    [TestCase(((ulong)123456 << 32) + 300u, 300u)]
    [TestCase(((ulong)123456 << 32) + uint.MaxValue, uint.MaxValue)]
    public void ReadVarint_ulongの場合はuintの範囲のみ読み取られる(ulong input, uint expected)
    {
        var reader = new NdgrProtobufStreamReader();

        // encodeして戻す
        var encoded = EncodeVarint(input);

        reader.AddNewChunk(encoded);
        var result = reader.ReadVarint();

        Assert.IsNotNull(result);
        Assert.That(result.Value.result, Is.EqualTo(expected));
    }

    [Test]
    public void ReadVarint_不十分なVarintがある場合はnullを返す()
    {
        var reader = new NdgrProtobufStreamReader();
        reader.AddNewChunk([0x80]); // 0x80は不完全なVarint

        var result = reader.ReadVarint();

        Assert.IsNull(result);
    }

    [Test]
    public void ReadVarint_64bitを超えるVarintがある場合は例外()
    {
        var reader = new NdgrProtobufStreamReader();
        reader.AddNewChunk([
            0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80 // over 64bit
        ]);

        Assert.Throws<NdgrProtobufStreamReaderException>(() => reader.ReadVarint());
    }

    [Test]
    public void UnshiftChunk_Varintが不十分なうちはfalseを返す()
    {
        var reader = new NdgrProtobufStreamReader();
        reader.AddNewChunk([0x80]); // 0x80は不完全なVarint

        var (isValid, _) = reader.UnshiftChunk(new byte[1]);

        Assert.IsFalse(isValid);
    }

    [Test]
    public void UnshiftChunk_データが揃っていない場合はfalse()
    {
        var reader = new NdgrProtobufStreamReader();
        reader.AddNewChunk(EncodeVarint(123)); // varint分しかない

        var (isValid, _) = reader.UnshiftChunk(new byte[1024]);

        Assert.IsFalse(isValid);
    }

    [Test]
    public void UnshiftChunk_データが揃っている場合は正常に読み取る()
    {
        var reader = new NdgrProtobufStreamReader();
        reader.AddNewChunk(EncodeVarint(3));

        var expected = new byte[] { 1, 2, 3 };
        reader.AddNewChunk(expected);

        var resultBuffer = new byte[1024];
        var (isValid, size) = reader.UnshiftChunk(resultBuffer);

        Assert.IsTrue(isValid);
        Assert.That(size, Is.EqualTo(expected.Length));
        Assert.That(resultBuffer[..size], Is.EqualTo(expected));
    }

    [Test]
    public void UnshiftChunk_データが大きすぎる場合は例外()
    {
        var reader = new NdgrProtobufStreamReader();
        reader.AddNewChunk(EncodeVarint(1024));

        var expected = new byte[1024];
        reader.AddNewChunk(expected);

        var resultBuffer = new byte[100]; // 100バイトしか収容できない
        Assert.Throws<NdgrProtobufStreamReaderException>(() => reader.UnshiftChunk(resultBuffer));
    }
}