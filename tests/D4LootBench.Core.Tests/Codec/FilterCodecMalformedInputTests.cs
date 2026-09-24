using D4LootBench.Core.Codec;
using D4LootBench.Core.Data;
using D4LootBench.Core.Models;
using Shouldly;

namespace D4LootBench.Core.Tests.Codec;

/// <summary>
/// Share codes are user-pasted: any garbage must fail with <see cref="FormatException"/>,
/// never IndexOutOfRange, OverflowException, or a runaway allocation.
/// </summary>
public sealed class FilterCodecMalformedInputTests
{
    private static string Code(params byte[] bytes) => Convert.ToBase64String(bytes);

    [Fact]
    public void Decode_OverlongVarint_ThrowsFormatException()
    {
        // Field 3 (varint) followed by 11 continuation bytes — shift would pass 63.
        var bytes = new byte[] { 0x18 }.Concat(Enumerable.Repeat((byte)0xFF, 11)).Append((byte)0x01).ToArray();
        Should.Throw<FormatException>(() => FilterCodec.Decode(Code(bytes)));
    }

    [Fact]
    public void Decode_TruncatedVarint_ThrowsFormatException()
    {
        Should.Throw<FormatException>(() => FilterCodec.Decode(Code(0x18, 0x80)));
    }

    [Fact]
    public void Decode_HugeLengthPrefix_ThrowsFormatExceptionWithoutAllocating()
    {
        // Field 1 (len) claiming ~4 GB.
        Should.Throw<FormatException>(() => FilterCodec.Decode(Code(0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F)));
    }

    [Fact]
    public void Decode_NegativeAsIntLengthPrefix_ThrowsFormatException()
    {
        // 0xFFFFFFFF would become -1 under an unchecked (int) cast.
        Should.Throw<FormatException>(() => FilterCodec.Decode(Code(0x12, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x41)));
    }

    [Fact]
    public void Decode_LengthPastEnd_ThrowsFormatException()
    {
        Should.Throw<FormatException>(() => FilterCodec.Decode(Code(0x12, 0x05, 0x41, 0x42)));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    public void Decode_UnsupportedWireType_ThrowsFormatException(int wireType)
    {
        var tag = (byte)((9 << 3) | wireType);
        Should.Throw<FormatException>(() => FilterCodec.Decode(Code(tag, 0x00)));
    }

    [Fact]
    public void Decode_TruncatedFixed32InRule_ThrowsFormatException()
    {
        // Rule (field 1, len 3) containing field 3 fixed32 with only 2 bytes of payload.
        Should.Throw<FormatException>(() => FilterCodec.Decode(Code(0x0A, 0x03, 0x1D, 0x01, 0x02)));
    }

    [Fact]
    public void Decode_TruncatedFixed64Skip_ThrowsFormatException()
    {
        // Unknown field 9, wire type 1 (fixed64) with only 3 bytes.
        Should.Throw<FormatException>(() => FilterCodec.Decode(Code(0x49, 0x01, 0x02, 0x03)));
    }

    [Fact]
    public void Decode_UnknownFixed64Field_IsSkipped()
    {
        var name = FilterCodec.Encode(new FilterRuleset("Named", []));
        var bytes = new byte[] { 0x49, 1, 2, 3, 4, 5, 6, 7, 8 }.Concat(Convert.FromBase64String(name)).ToArray();
        FilterCodec.Decode(Code(bytes)).Name.ShouldBe("Named");
    }

    [Fact]
    public void Decode_NotBase64_ThrowsFormatException()
    {
        Should.Throw<FormatException>(() => FilterCodec.Decode("this is not a share code!"));
    }

    [Fact]
    public void Decode_EveryTruncationOfAValidCode_SucceedsOrThrowsFormatException()
    {
        var ruleset = new FilterRuleset("Truncate Me",
        [
            new FilterRule("Affixes", Visibility.Show, FilterColors.Blue,
                [new AffixCondition([0x001BEAC2u, 0x001BEAC6u], 2)
                {
                    GreaterEntries = [new GreaterAffixEntry(0x001BEAC2u, 0x001BEAC2u)],
                }]),
            new FilterRule("Rarity", Visibility.HideAll, 0,
                [new RarityCondition(RarityFlags.Common), new ItemPowerCondition(800, 0)]),
        ]);
        var bytes = Convert.FromBase64String(FilterCodec.Encode(ruleset));

        for (var len = 0; len < bytes.Length; len++)
            AssertOnlyFormatException(bytes[..len]);
    }

    [Fact]
    public void Decode_RandomGarbage_OnlyEverThrowsFormatException()
    {
        var rng = new Random(20240923);
        for (var i = 0; i < 5000; i++)
        {
            var bytes = new byte[rng.Next(1, 64)];
            rng.NextBytes(bytes);
            AssertOnlyFormatException(bytes);
        }
    }

    [Fact]
    public void Decode_BitFlippedValidCode_OnlyEverThrowsFormatException()
    {
        var bytes = Convert.FromBase64String(FilterCodec.Encode(new FilterRuleset("Flip",
        [
            new FilterRule("Uniques", Visibility.Recolor, FilterColors.Cyan,
                [new SpecificUniqueCondition([0x0027D5F1u]), new GreaterAffixCondition(1)]),
        ])));
        for (var i = 0; i < bytes.Length; i++)
        for (var bit = 0; bit < 8; bit++)
        {
            var copy = (byte[])bytes.Clone();
            copy[i] ^= (byte)(1 << bit);
            AssertOnlyFormatException(copy);
        }
    }

    private static void AssertOnlyFormatException(byte[] bytes)
    {
        try
        {
            FilterCodec.Decode(Code(bytes));
        }
        catch (FormatException)
        {
        }
        catch (Exception ex)
        {
            throw new ShouldAssertException(
                $"Decode of [{Convert.ToHexString(bytes)}] threw {ex.GetType().Name}: {ex.Message}", ex);
        }
    }
}
