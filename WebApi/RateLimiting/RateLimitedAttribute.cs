namespace WebApi.RateLimiting;

[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class RateLimitedAttribute : Attribute;
