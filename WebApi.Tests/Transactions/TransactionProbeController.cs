using DataAccessLayer.Entities;
using DataAccessLayer.Transactions;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WebApi.Controllers;

namespace WebApi.Tests.Transactions;

[ApiController]
[Route("probe/transactions")]
public class TransactionProbeController : ClubApiController
{
    public const string Operation = "probe-transaction";

    private readonly TransactionRunner _runner;
    private readonly IUnitOfWork _unitOfWork;

    public TransactionProbeController(TransactionRunner runner, IUnitOfWork unitOfWork)
    {
        _runner = runner;
        _unitOfWork = unitOfWork;
    }

    [HttpPost("spend-and-lock/{classId}")]
    public async Task<ActionResult<int>> SpendAndLockClass(long classId, CancellationToken ct)
    {
        return await _runner.RunAsync(Operation, async token =>
        {
            var pass = await _unitOfWork.Pass.GetByUserAsync(CurrentUserId, token);
            pass!.RemainingVisits -= 1;
            await _unitOfWork.SaveChangesAsync(token);
            await LockClassAsync(classId, token);
            return TransactionResult.Commit(pass.RemainingVisits);
        }, ct);
    }

    [HttpPost("lock-both/{first}/{second}")]
    public async Task<ActionResult<int>> LockBothClasses(long first, long second, CancellationToken ct)
    {
        return await _runner.RunAsync(Operation, async token =>
        {
            await LockClassAsync(first, token);
            await LockClassAsync(second, token);
            return TransactionResult.Commit(2);
        }, ct);
    }

    [HttpPost("duplicate-pass")]
    public async Task<ActionResult<int>> AddDuplicatePass(CancellationToken ct)
    {
        return await _runner.RunAsync(Operation, async token =>
        {
            var pass = await _unitOfWork.Pass.GetByUserAsync(CurrentUserId, token);
            await _unitOfWork.Pass.CreateAsync(
                new Pass { UserId = CurrentUserId, RemainingVisits = 1, ValidUntil = pass!.ValidUntil },
                token);
            await _unitOfWork.SaveChangesAsync(token);
            return TransactionResult.Commit(1);
        }, ct);
    }

    private async Task LockClassAsync(long classId, CancellationToken ct)
    {
        await _unitOfWork.DbContext.Set<Class>()
            .FromSql($"SELECT * FROM classes WHERE id = {classId} FOR NO KEY UPDATE")
            .IgnoreQueryFilters()
            .ToListAsync(ct);
    }
}
