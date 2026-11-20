using DataAccessLayer.Entities;
using WebApi.Enrollments;

namespace WebApi.Tests.Idempotency;

[TestClass]
public class IdempotentOperationsTests
{
    private static readonly Dictionary<IdempotentOperation, string> Expected = new()
    {
        [IdempotentOperation.Enroll] = "enroll",
        [IdempotentOperation.JoinWaitlist] = "join-waitlist"
    };

    [TestMethod]
    public void ToValue_EveryOperation_ReturnsExactStringAndParsesBack()
    {
        var operations = Enum.GetValues<IdempotentOperation>();

        CollectionAssert.AreEquivalent(Expected.Keys.ToArray(), operations);
        foreach (var operation in operations)
        {
            var value = IdempotentOperations.ToValue(operation);

            Assert.AreEqual(Expected[operation], value);
            Assert.AreEqual(operation, IdempotentOperations.Parse(value));
        }
    }

    [TestMethod]
    public void ToValue_Enroll_EqualsOperationTag()
    {
        Assert.AreEqual(Operations.Enroll, IdempotentOperations.ToValue(IdempotentOperation.Enroll));
    }

    [TestMethod]
    public void ToValue_JoinWaitlist_EqualsOperationTag()
    {
        Assert.AreEqual(Operations.JoinWaitlist, IdempotentOperations.ToValue(IdempotentOperation.JoinWaitlist));
    }

    [TestMethod]
    [DataRow("Enroll")]
    [DataRow("ENROLL")]
    [DataRow("")]
    [DataRow("waitlist")]
    [DataRow("JoinWaitlist")]
    public void Parse_UnknownValue_Throws(string value)
    {
        Assert.ThrowsException<InvalidOperationException>(() => IdempotentOperations.Parse(value));
    }

    [TestMethod]
    public void ToValue_UndefinedMember_Throws()
    {
        Assert.ThrowsException<InvalidOperationException>(() => IdempotentOperations.ToValue((IdempotentOperation)42));
    }
}
