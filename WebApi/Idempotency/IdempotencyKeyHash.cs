using System.Security.Cryptography;
using System.Text;

namespace WebApi.Idempotency;

public static class IdempotencyKeyHash
{
    public static byte[] Compute(string key)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }
}
