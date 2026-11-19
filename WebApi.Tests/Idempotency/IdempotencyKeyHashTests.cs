using WebApi.Idempotency;

namespace WebApi.Tests.Idempotency;

[TestClass]
public class IdempotencyKeyHashTests
{
    [TestMethod]
    [DataRow("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    [DataRow("ключ", "1de36a32af798da0c1ac9297603a320ed8fe567cf21c9177112a4ce914ebb8be")]
    public void Compute_KnownKey_ReturnsSha256OfUtf8Bytes(string key, string expected)
    {
        var hash = IdempotencyKeyHash.Compute(key);

        Assert.AreEqual(32, hash.Length);
        Assert.AreEqual(expected, Convert.ToHexStringLower(hash));
    }

    [TestMethod]
    public void Compute_DifferentCase_ReturnsDifferentHash()
    {
        CollectionAssert.AreNotEqual(IdempotencyKeyHash.Compute("Key"), IdempotencyKeyHash.Compute("key"));
    }
}
