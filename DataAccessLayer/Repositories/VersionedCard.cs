namespace DataAccessLayer.Repositories;

public sealed record VersionedCard(EnrollmentCard Card, long Version);
