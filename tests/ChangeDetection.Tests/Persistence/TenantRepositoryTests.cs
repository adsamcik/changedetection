using System.Linq.Expressions;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Persistence;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Persistence;

/// <summary>
/// Simple test entity implementing IOwnedEntity for tenant isolation tests.
/// </summary>
public class TestOwnedEntity : IOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid OwnerId { get; set; }
}

[Category("Unit")]
public class TenantRepositoryTests
{
    private static readonly Guid UserAId = Guid.NewGuid();
    private static readonly Guid UserBId = Guid.NewGuid();
    private static readonly Guid AdminId = Guid.NewGuid();

    private IRepository<TestOwnedEntity> _innerRepo = null!;
    private IUserContext _userContext = null!;

    [Before(Test)]
    public async Task SetUp()
    {
        _innerRepo = Substitute.For<IRepository<TestOwnedEntity>>();
        _userContext = Substitute.For<IUserContext>();
        await Task.CompletedTask;
    }

    private TenantRepository<TestOwnedEntity> CreateSut() => new(_innerRepo, _userContext);

    private void SetCurrentUser(Guid userId, bool isAdmin = false)
    {
        _userContext.CurrentUserId.Returns(userId);
        _userContext.IsAdmin.Returns(isAdmin);
    }

    // --- GetAllAsync ---

    [Test]
    public async Task GetAllAsync_RegularUser_ReturnsOnlyOwnedEntities()
    {
        SetCurrentUser(UserAId);
        var ownedEntity = new TestOwnedEntity { OwnerId = UserAId, Name = "Mine" };
        var otherEntity = new TestOwnedEntity { OwnerId = UserBId, Name = "Theirs" };

        _innerRepo.FindAsync(Arg.Any<Expression<Func<TestOwnedEntity, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var predicate = callInfo.ArgAt<Expression<Func<TestOwnedEntity, bool>>>(0).Compile();
                return new[] { ownedEntity, otherEntity }.Where(predicate).ToList();
            });

        var sut = CreateSut();
        var results = (await sut.GetAllAsync()).ToList();

        results.ShouldContain(e => e.Name == "Mine");
        results.ShouldNotContain(e => e.Name == "Theirs");
        results.Count.ShouldBe(1);
    }

    [Test]
    public async Task GetAllAsync_AdminUser_ReturnsAllEntities()
    {
        SetCurrentUser(AdminId, isAdmin: true);
        var entityA = new TestOwnedEntity { OwnerId = UserAId, Name = "A's" };
        var entityB = new TestOwnedEntity { OwnerId = UserBId, Name = "B's" };

        _innerRepo.FindAsync(Arg.Any<Expression<Func<TestOwnedEntity, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var predicate = callInfo.ArgAt<Expression<Func<TestOwnedEntity, bool>>>(0).Compile();
                return new[] { entityA, entityB }.Where(predicate).ToList();
            });

        var sut = CreateSut();
        var results = (await sut.GetAllAsync()).ToList();

        results.Count.ShouldBe(2);
    }

    // --- GetByIdAsync ---

    [Test]
    public async Task GetByIdAsync_RegularUser_OwnEntity_ReturnsEntity()
    {
        SetCurrentUser(UserAId);
        var entity = new TestOwnedEntity { OwnerId = UserAId, Name = "Mine" };
        _innerRepo.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>()).Returns(entity);

        var sut = CreateSut();
        var result = await sut.GetByIdAsync(entity.Id);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Mine");
    }

    [Test]
    public async Task GetByIdAsync_RegularUser_OtherUsersEntity_ReturnsNull()
    {
        SetCurrentUser(UserAId);
        var entity = new TestOwnedEntity { OwnerId = UserBId, Name = "Not mine" };
        _innerRepo.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>()).Returns(entity);

        var sut = CreateSut();
        var result = await sut.GetByIdAsync(entity.Id);

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetByIdAsync_AdminUser_AnyEntity_ReturnsEntity()
    {
        SetCurrentUser(AdminId, isAdmin: true);
        var entityA = new TestOwnedEntity { OwnerId = UserAId };
        var entityB = new TestOwnedEntity { OwnerId = UserBId };
        _innerRepo.GetByIdAsync(entityA.Id, Arg.Any<CancellationToken>()).Returns(entityA);
        _innerRepo.GetByIdAsync(entityB.Id, Arg.Any<CancellationToken>()).Returns(entityB);

        var sut = CreateSut();

        (await sut.GetByIdAsync(entityA.Id)).ShouldNotBeNull();
        (await sut.GetByIdAsync(entityB.Id)).ShouldNotBeNull();
    }

    // --- InsertAsync ---

    [Test]
    public async Task InsertAsync_SetsOwnerIdFromCurrentUser()
    {
        SetCurrentUser(UserAId);
        var entity = new TestOwnedEntity { Name = "New" };

        var sut = CreateSut();
        await sut.InsertAsync(entity);

        entity.OwnerId.ShouldBe(UserAId);
        await _innerRepo.Received(1).InsertAsync(entity, Arg.Any<CancellationToken>());
    }

    // --- UpdateAsync ---

    [Test]
    public async Task UpdateAsync_RegularUser_OwnEntity_Succeeds()
    {
        SetCurrentUser(UserAId);
        var entity = new TestOwnedEntity { OwnerId = UserAId, Name = "Original" };
        _innerRepo.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>()).Returns(entity);

        var updated = new TestOwnedEntity { Id = entity.Id, OwnerId = UserAId, Name = "Updated" };

        var sut = CreateSut();
        await sut.UpdateAsync(updated);

        await _innerRepo.Received(1).UpdateAsync(updated, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_RegularUser_OtherUsersEntity_Fails()
    {
        SetCurrentUser(UserAId);
        var entity = new TestOwnedEntity { OwnerId = UserBId, Name = "Not mine" };
        _innerRepo.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>()).Returns(entity);

        var updated = new TestOwnedEntity { Id = entity.Id, OwnerId = UserBId, Name = "Hacked" };

        var sut = CreateSut();

        await Should.ThrowAsync<UnauthorizedAccessException>(async () => await sut.UpdateAsync(updated));
        await _innerRepo.DidNotReceive().UpdateAsync(Arg.Any<TestOwnedEntity>(), Arg.Any<CancellationToken>());
    }

    // --- DeleteAsync ---

    [Test]
    public async Task DeleteAsync_RegularUser_OwnEntity_Succeeds()
    {
        SetCurrentUser(UserAId);
        var entity = new TestOwnedEntity { OwnerId = UserAId };
        _innerRepo.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>()).Returns(entity);

        var sut = CreateSut();
        await sut.DeleteAsync(entity.Id);

        await _innerRepo.Received(1).DeleteAsync(entity.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteAsync_RegularUser_OtherUsersEntity_Fails()
    {
        SetCurrentUser(UserAId);
        var entity = new TestOwnedEntity { OwnerId = UserBId };
        _innerRepo.GetByIdAsync(entity.Id, Arg.Any<CancellationToken>()).Returns(entity);

        var sut = CreateSut();

        await Should.ThrowAsync<UnauthorizedAccessException>(async () => await sut.DeleteAsync(entity.Id));
        await _innerRepo.DidNotReceive().DeleteAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // --- FindAsync ---

    [Test]
    public async Task FindAsync_RegularUser_FiltersToOwnedOnly()
    {
        SetCurrentUser(UserAId);
        var ownedMatch = new TestOwnedEntity { OwnerId = UserAId, Name = "Match" };
        var ownedNoMatch = new TestOwnedEntity { OwnerId = UserAId, Name = "NoMatch" };
        var otherMatch = new TestOwnedEntity { OwnerId = UserBId, Name = "Match" };

        _innerRepo.FindAsync(Arg.Any<Expression<Func<TestOwnedEntity, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var predicate = callInfo.ArgAt<Expression<Func<TestOwnedEntity, bool>>>(0).Compile();
                return new[] { ownedMatch, ownedNoMatch, otherMatch }.Where(predicate).ToList();
            });

        var sut = CreateSut();
        var results = (await sut.FindAsync(e => e.Name == "Match")).ToList();

        results.ShouldContain(e => e.OwnerId == UserAId && e.Name == "Match");
        results.ShouldNotContain(e => e.OwnerId == UserBId);
        results.ShouldNotContain(e => e.Name == "NoMatch");
        results.Count.ShouldBe(1);
    }

    // --- CountAsync ---

    [Test]
    public async Task CountAsync_RegularUser_CountsOnlyOwnedEntities()
    {
        SetCurrentUser(UserAId);

        _innerRepo.CountAsync(Arg.Any<Expression<Func<TestOwnedEntity, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var predicate = callInfo.ArgAt<Expression<Func<TestOwnedEntity, bool>>>(0).Compile();
                var allEntities = new[]
                {
                    new TestOwnedEntity { OwnerId = UserAId },
                    new TestOwnedEntity { OwnerId = UserAId },
                    new TestOwnedEntity { OwnerId = UserBId }
                };
                return allEntities.Count(predicate);
            });

        var sut = CreateSut();
        var count = await sut.CountAsync();

        count.ShouldBe(2);
    }
}
