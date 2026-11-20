namespace WebApi.Tests.Invariants;

public enum OpKind
{
    Enroll,
    ChangeSeats,
    Cancel,
    Join,
    Leave
}

public sealed record MixedOp(int Wave, OpKind Kind, int User, long Target, int Seats, string? Key)
{
    public override string ToString()
    {
        return $"w{Wave} {Kind} u{User} target={Target} seats={Seats} key={Key}";
    }
}

public sealed record Placement(int Class, int User, int Seats);

public sealed record Placed(int Class, int User, int Seats, long Id);

public sealed record Outcome(MixedOp Op, int Status, string? Reason, long? Id, bool RetryAfter, string Body)
{
    public override string ToString()
    {
        return $"{Op} -> {Status} {Reason}{Id}";
    }
}

public sealed class MixedScenario
{
    public const int Users = 40;
    public const int MaxRequestsPerUser = 10;
    public const int ShortClass = 2;
    public const int OpenClass = 3;

    public static readonly int[] Capacities = [1, 2, 3, 3];

    private readonly Random _random;
    private readonly int[] _requests = new int[Users];
    private int _keys;

    public MixedScenario(int seed)
    {
        _random = new Random(seed);
        Passes = Enumerable.Range(0, Users)
            .Select(x => x < 2 ? (Remaining: 10, Expired: true) : x < 12 ? (_random.Next(1, 3), false) : (10, false))
            .ToArray();
    }

    public (int Remaining, bool Expired)[] Passes { get; }

    public (List<Placement> Enrollments, List<Placement> Entries) Setup()
    {
        var enrollments = new List<Placement>();
        var entries = new List<Placement>();

        for (var c = 0; c < Capacities.Length; c++)
        {
            for (var left = Capacities[c]; left > 0;)
            {
                var seats = _random.Next(1, left + 1);
                enrollments.Add(new Placement(c, _random.Next(Users), seats));
                left -= seats;
            }
        }

        // решение: первый в очереди каждого занятия, кроме открытого, — один и тот же годный абонемент на 1 место, он не
        // уходит и не владеет бронями: отмены первой волны продвигают его на нескольких занятиях одновременно.
        var owners = enrollments.Select(x => x.User).ToHashSet();
        var anchor = Enumerable.Range(12, Users - 12).Where(x => !owners.Contains(x)).OrderBy(_ => _random.Next()).First();

        for (var c = 0; c < Capacities.Length; c++)
        {
            // решение: очередь открытого занятия — только истёкшие абонементы: продвижение их пропускает, иначе очередь
            // забирает каждое освободившееся место и новых броней через §1 в прогоне нет.
            if (c == OpenClass)
            {
                entries.AddRange([new Placement(c, 0, _random.Next(1, 3)), new Placement(c, 1, _random.Next(1, 3))]);
                continue;
            }

            entries.Add(new Placement(c, anchor, 1));

            foreach (var user in Enumerable.Range(0, Users).Where(x => x != anchor).OrderBy(_ => _random.Next()).Take(2))
            {
                entries.Add(new Placement(c, user, _random.Next(1, 3)));
            }
        }

        return (enrollments, entries);
    }

    public List<MixedOp> FirstWave(IReadOnlyList<Placed> enrollments, IReadOnlyList<Placed> entries, IReadOnlyList<long> classes)
    {
        var ops = new List<MixedOp>();

        var shrinking = enrollments.First(x => x.Class == ShortClass);

        foreach (var enrollment in enrollments)
        {
            Add(ops, enrollment == shrinking
                ? new MixedOp(1, OpKind.ChangeSeats, enrollment.User, enrollment.Id, _random.Next(1, enrollment.Seats + 1), null)
                : new MixedOp(1, OpKind.Cancel, enrollment.User, enrollment.Id, 0, null));
        }

        var anchors = entries.Where(x => x.Class != OpenClass).GroupBy(x => x.Class).Select(g => g.First()).ToHashSet();

        foreach (var entry in entries.Where(x => !anchors.Contains(x)))
        {
            var roll = _random.NextDouble();

            if (roll < 0.5)
            {
                Add(ops, new MixedOp(1, OpKind.Leave, entry.User, entry.Id, 0, null));
            }
            else if (roll < 0.8)
            {
                Add(ops, new MixedOp(1, OpKind.Join, entry.User, classes[entry.Class], _random.Next(1, 3), NextKey()));
            }
        }

        AddNew(ops, 1, classes, enrolls: 35, joins: 25);

        return ops;
    }

    public List<MixedOp> SecondWave(IReadOnlyList<Outcome> first, IReadOnlyList<long> classes)
    {
        var ops = new List<MixedOp>();

        foreach (var created in first.Where(x => x.Status == 201))
        {
            var op = created.Op.Kind == OpKind.Join
                ? _random.NextDouble() < 0.5 ? new MixedOp(2, OpKind.Leave, created.Op.User, created.Id!.Value, 0, null) : null
                : _random.NextDouble() < 0.5
                    ? new MixedOp(2, OpKind.Cancel, created.Op.User, created.Id!.Value, 0, null)
                    : new MixedOp(2, OpKind.ChangeSeats, created.Op.User, created.Id!.Value, _random.Next(1, 4), null);

            if (op is not null)
            {
                Add(ops, op);
            }
        }

        foreach (var keyed in first.Select(x => x.Op).Where(x => x.Key is not null && _random.NextDouble() < 0.5))
        {
            Add(ops, keyed with { Wave = 2 });
        }

        AddNew(ops, 2, classes, enrolls: 20, joins: 10);

        return ops;
    }

    private void AddNew(List<MixedOp> ops, int wave, IReadOnlyList<long> classes, int enrolls, int joins)
    {
        for (var i = 0; i < enrolls + joins; i++)
        {
            var user = Enumerable.Range(0, 5).Select(_ => _random.Next(Users)).FirstOrDefault(x => _requests[x] < MaxRequestsPerUser, -1);
            var kind = i < enrolls ? OpKind.Enroll : OpKind.Join;

            if (user >= 0)
            {
                var target = kind == OpKind.Enroll && _random.NextDouble() < 0.5 ? OpenClass : _random.Next(classes.Count);
                target = kind == OpKind.Join && wave == 1 && target == OpenClass ? ShortClass : target;
                Add(ops, new MixedOp(wave, kind, user, classes[target], _random.Next(1, 3), NextKey()));
            }
        }
    }

    private void Add(List<MixedOp> ops, MixedOp op)
    {
        if (_requests[op.User] < MaxRequestsPerUser)
        {
            _requests[op.User]++;
            ops.Add(op);
        }
    }

    private string NextKey()
    {
        return $"mixed-{_keys++}";
    }
}
