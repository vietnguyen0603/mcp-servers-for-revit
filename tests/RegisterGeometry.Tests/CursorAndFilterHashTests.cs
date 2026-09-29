using System.Collections.Generic;
using RegisterGeometry;
using TUnit.Core;
using TUnit.Assertions;

namespace RegisterGeometry.Tests;

public class CursorAndFilterHashTests
{
    [Test]
    public async Task FilterHasher_SameParts_ProduceSameHash()
    {
        var hash1 = FilterHasher.Compute(new[] { "a", "b", "c" });
        var hash2 = FilterHasher.Compute(new[] { "a", "b", "c" });
        await Assert.That(hash1).IsEqualTo(hash2);
    }

    [Test]
    public async Task FilterHasher_DifferentOrder_ProducesDifferentHash()
    {
        var hash1 = FilterHasher.Compute(new[] { "a", "b", "c" });
        var hash2 = FilterHasher.Compute(new[] { "c", "b", "a" });
        await Assert.That(hash1).IsNotEqualTo(hash2);
    }

    [Test]
    public async Task FilterHasher_DictionaryOrderIsCanonical()
    {
        var a = new Dictionary<string, string> { ["k1"] = "v1", ["k2"] = "v2" };
        var b = new Dictionary<string, string> { ["k2"] = "v2", ["k1"] = "v1" };
        await Assert.That(FilterHasher.Compute(a)).IsEqualTo(FilterHasher.Compute(b));
    }

    [Test]
    public async Task FilterHasher_DoubleFormattingIsInvariant()
    {
        // We never want "1.5" vs "1,5" to change the hash.
        var hash = FilterHasher.Compute(new[] { FilterHasher.FormatDouble(1.5) });
        await Assert.That(hash).IsEqualTo(FilterHasher.Compute(new[] { "1.5" }));
    }

    [Test]
    public async Task CursorCodec_RoundTrip_PreservesAllFields()
    {
        var original = new Cursor(
            schemaVersion: "1.0",
            documentKey: "doc-abc",
            filterHash: "deadbeef",
            lastRecordKey: "uid-1234",
            pageNumber: 7);
        var encoded = CursorCodec.Encode(original);
        var decoded = CursorCodec.Decode(encoded);
        await Assert.That(decoded).IsEqualTo(original);
    }

    [Test]
    public async Task CursorCodec_Encoded_IsUrlSafe()
    {
        var original = new Cursor("1.0", "doc", "abc", "last", 0);
        var encoded = CursorCodec.Encode(original);
        foreach (var c in encoded)
        {
            var ok = (c >= 'A' && c <= 'Z') ||
                     (c >= 'a' && c <= 'z') ||
                     (c >= '0' && c <= '9') ||
                     c == '-' || c == '_';
            await Assert.That(ok).IsTrue();
        }
    }

    [Test]
    public async Task CursorCodec_DecodeEmpty_Throws()
    {
        await Assert.That(() => CursorCodec.Decode("")).Throws<CursorFormatException>();
    }

    [Test]
    public async Task CursorCodec_DecodeInvalidBase64_Throws()
    {
        await Assert.That(() => CursorCodec.Decode("@@@not-base64@@@")).Throws<CursorFormatException>();
    }

    [Test]
    public async Task CursorCodec_DecodeWrongFieldCount_Throws()
    {
        // Build a base64url payload that decodes to a 4-field string instead of 5.
        var raw = "1.0|doc|hash|last";
        var encoded = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        await Assert.That(() => CursorCodec.Decode(encoded)).Throws<CursorFormatException>();
    }

    [Test]
    public async Task CursorCodec_DecodeNegativePage_Throws()
    {
        var raw = "1.0|doc|hash|last|-1";
        var encoded = System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        await Assert.That(() => CursorCodec.Decode(encoded)).Throws<CursorFormatException>();
    }

    [Test]
    public async Task CursorCodec_DecodeTooLong_Throws()
    {
        var huge = new string('a', CursorCodec.MaxWireLength + 1);
        await Assert.That(() => CursorCodec.Decode(huge)).Throws<CursorFormatException>();
    }

    [Test]
    public async Task CursorCodec_Validate_FlagsSchemaMismatch()
    {
        var cursor = new Cursor("1.0", "doc", "hash", "last", 0);
        var reason = CursorCodec.Validate(cursor, expectedSchemaVersion: "2.0", expectedFilterHash: "hash", expectedDocumentKey: "doc");
        await Assert.That(reason).IsEqualTo("stale_schema_version");
    }

    [Test]
    public async Task CursorCodec_Validate_FlagsFilterMismatch()
    {
        var cursor = new Cursor("1.0", "doc", "hash-A", "last", 0);
        var reason = CursorCodec.Validate(cursor, "1.0", "hash-B", "doc");
        await Assert.That(reason).IsEqualTo("filter_mismatch");
    }

    [Test]
    public async Task CursorCodec_Validate_DocumentMismatch()
    {
        var cursor = new Cursor("1.0", "doc-A", "hash", "last", 0);
        var reason = CursorCodec.Validate(cursor, "1.0", "hash", "doc-B");
        await Assert.That(reason).IsEqualTo("document_mismatch");
    }

    [Test]
    public async Task CursorCodec_Validate_Match_ReturnsNull()
    {
        var cursor = new Cursor("1.0", "doc", "hash", "last", 0);
        var reason = CursorCodec.Validate(cursor, "1.0", "hash", "doc");
        await Assert.That(reason).IsNull();
    }
}
