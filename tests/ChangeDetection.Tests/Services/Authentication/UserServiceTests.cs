using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Authentication;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.Authentication;

[Category("Unit")]
public class UserServiceTests : TestBase
{
    private readonly IRepository<User> _userRepo;
    private readonly UserService _sut;

    public UserServiceTests()
    {
        _userRepo = Substitute.For<IRepository<User>>();
        var logger = CreateLogger<UserService>();
        _sut = new UserService(_userRepo, logger);
    }

    // --- GetByIdAsync ---

    [Test]
    public async Task GetByIdAsync_ExistingUser_ReturnsUser()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Username = "alice" };
        _userRepo.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        var result = await _sut.GetByIdAsync(userId);

        result.ShouldNotBeNull();
        result.Id.ShouldBe(userId);
        result.Username.ShouldBe("alice");
    }

    [Test]
    public async Task GetByIdAsync_NonExistingUser_ReturnsNull()
    {
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((User?)null);

        var result = await _sut.GetByIdAsync(Guid.NewGuid());

        result.ShouldBeNull();
    }

    // --- GetByUsernameAsync ---

    [Test]
    public async Task GetByUsernameAsync_ExistingUser_ReturnsUser()
    {
        var user = new User { Username = "alice" };
        _userRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { user });

        var result = await _sut.GetByUsernameAsync("alice");

        result.ShouldNotBeNull();
        result.Username.ShouldBe("alice");
    }

    [Test]
    public async Task GetByUsernameAsync_NonExistingUser_ReturnsNull()
    {
        _userRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<User>());

        var result = await _sut.GetByUsernameAsync("unknown");

        result.ShouldBeNull();
    }

    // --- GetOrCreateFromSsoAsync ---

    [Test]
    public async Task GetOrCreateFromSsoAsync_NewUser_CreatesUser()
    {
        _userRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<User>());

        var result = await _sut.GetOrCreateFromSsoAsync(
            "newuser", "new@example.com", "New User", ["group1"]);

        result.ShouldNotBeNull();
        result.Username.ShouldBe("newuser");
        result.Email.ShouldBe("new@example.com");
        result.DisplayName.ShouldBe("New User");
        result.Groups.ShouldContain("group1");
        result.IsActive.ShouldBeTrue();

        await _userRepo.Received(1).InsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetOrCreateFromSsoAsync_ExistingUser_UpdatesProfile()
    {
        var existing = new User
        {
            Username = "alice",
            Email = "old@example.com",
            DisplayName = "Old Name",
            Groups = ["old-group"]
        };
        _userRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { existing });

        var result = await _sut.GetOrCreateFromSsoAsync(
            "alice", "new@example.com", "New Name", ["new-group"]);

        result.Email.ShouldBe("new@example.com");
        result.DisplayName.ShouldBe("New Name");
        result.Groups.ShouldContain("new-group");

        await _userRepo.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
        await _userRepo.DidNotReceive().InsertAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetOrCreateFromSsoAsync_ExistingUserNoChanges_StillUpdatesLastSeen()
    {
        var existing = new User
        {
            Username = "alice",
            Email = "same@example.com",
            DisplayName = "Same Name",
            Groups = ["same-group"],
            LastSeen = DateTime.UtcNow.AddHours(-1)
        };
        _userRepo.FindAsync(Arg.Any<System.Linq.Expressions.Expression<Func<User, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new[] { existing });

        await _sut.GetOrCreateFromSsoAsync(
            "alice", "same@example.com", "Same Name", ["same-group"]);

        // UpdateAsync is always called to persist LastSeen update
        await _userRepo.Received(1).UpdateAsync(existing, Arg.Any<CancellationToken>());
    }

    // --- GetAllAsync ---

    [Test]
    public async Task GetAllAsync_ReturnsAllUsers()
    {
        var users = new List<User>
        {
            new() { Username = "alice" },
            new() { Username = "bob" }
        };
        _userRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(users);

        var result = await _sut.GetAllAsync();

        result.Count().ShouldBe(2);
    }

    // --- UpdateAsync ---

    [Test]
    public async Task UpdateAsync_DelegatesToRepository()
    {
        var user = new User { Username = "alice" };

        await _sut.UpdateAsync(user);

        await _userRepo.Received(1).UpdateAsync(user, Arg.Any<CancellationToken>());
    }

    // --- DeactivateAsync ---

    [Test]
    public async Task DeactivateAsync_ExistingUser_SetsInactive()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Username = "alice", IsActive = true };
        _userRepo.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        await _sut.DeactivateAsync(userId);

        user.IsActive.ShouldBeFalse();
        await _userRepo.Received(1).UpdateAsync(user, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeactivateAsync_NonExistingUser_DoesNotThrow()
    {
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((User?)null);

        await _sut.DeactivateAsync(Guid.NewGuid());

        await _userRepo.DidNotReceive().UpdateAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    // --- ReactivateAsync ---

    [Test]
    public async Task ReactivateAsync_ExistingUser_SetsActive()
    {
        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Username = "alice", IsActive = false };
        _userRepo.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);

        await _sut.ReactivateAsync(userId);

        user.IsActive.ShouldBeTrue();
        await _userRepo.Received(1).UpdateAsync(user, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReactivateAsync_NonExistingUser_DoesNotThrow()
    {
        _userRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((User?)null);

        await _sut.ReactivateAsync(Guid.NewGuid());

        await _userRepo.DidNotReceive().UpdateAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    // --- SingleUserContext ---

    [Test]
    public async Task SingleUserContext_UsesGuidEmpty()
    {
        var ctx = new SingleUserContext();

        ctx.CurrentUserId.ShouldBe(Guid.Empty);
        ctx.IsAuthenticated.ShouldBeTrue();
        ctx.IsAdmin.ShouldBeTrue();

        var user = ctx.GetCurrentUser();
        user.ShouldNotBeNull();
        user.Id.ShouldBe(Guid.Empty);
        user.Username.ShouldBe("default");

        ctx.GetRequiredCurrentUser().ShouldNotBeNull();
        await Task.CompletedTask;
    }
}
