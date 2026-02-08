using System.Linq.Expressions;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

[Category("Unit")]
public class CategoryServiceTests : TestBase
{
    private readonly IRepository<Category> _categoryRepo;
    private readonly IRepository<WatchedSite> _watchRepo;
    private readonly ServerCategoryService _sut;

    public CategoryServiceTests()
    {
        _categoryRepo = Substitute.For<IRepository<Category>>();
        _watchRepo = Substitute.For<IRepository<WatchedSite>>();
        var logger = CreateLogger<ServerCategoryService>();
        _sut = new ServerCategoryService(_categoryRepo, _watchRepo, logger);
    }

    [Test]
    public async Task CreateAsync_WithValidData_SavesSuccessfully()
    {
        // Arrange
        _categoryRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<Category>());
        var request = new CreateCategoryRequest { Name = "Test Category", Description = "A description", Color = "#FF0000" };

        // Act
        var result = await _sut.CreateAsync(request);

        // Assert
        result.ShouldNotBeNull();
        result.Name.ShouldBe("Test Category");
        result.Description.ShouldBe("A description");
        result.Color.ShouldBe("#FF0000");
        result.IsDefault.ShouldBeFalse();
        result.SortOrder.ShouldBe(1);
        await _categoryRepo.Received(1).InsertAsync(Arg.Any<Category>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAsync_TrimsWhitespace_FromNameAndDescription()
    {
        // Arrange
        _categoryRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<Category>());
        var request = new CreateCategoryRequest { Name = "  Padded Name  ", Description = "  Padded Desc  " };

        // Act
        var result = await _sut.CreateAsync(request);

        // Assert
        result.Name.ShouldBe("Padded Name");
        result.Description.ShouldBe("Padded Desc");
    }

    [Test]
    public async Task CreateAsync_WithExistingCategories_AssignsNextSortOrder()
    {
        // Arrange
        var existing = new List<Category>
        {
            new() { Name = "First", SortOrder = 1 },
            new() { Name = "Second", SortOrder = 3 }
        };
        _categoryRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(existing);
        var request = new CreateCategoryRequest { Name = "Third" };

        // Act
        var result = await _sut.CreateAsync(request);

        // Assert
        result.SortOrder.ShouldBe(4);
    }

    [Test]
    public async Task GetByIdAsync_ExistingCategory_ReturnsCategory()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var category = new Category { Id = categoryId, Name = "Test" };
        _categoryRepo.GetByIdAsync(categoryId, Arg.Any<CancellationToken>()).Returns(category);

        // Act
        var result = await _sut.GetByIdAsync(categoryId);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(categoryId);
        result.Name.ShouldBe("Test");
    }

    [Test]
    public async Task GetByIdAsync_NonExistingCategory_ReturnsNull()
    {
        // Arrange
        _categoryRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Category?)null);

        // Act
        var result = await _sut.GetByIdAsync(Guid.NewGuid());

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public async Task GetAllAsync_ReturnsCategoriesOrderedBySortOrder()
    {
        // Arrange
        var categories = new List<Category>
        {
            new() { Name = "C", SortOrder = 3 },
            new() { Name = "A", SortOrder = 1 },
            new() { Name = "B", SortOrder = 2 }
        };
        _categoryRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(categories);

        // Act
        var result = (await _sut.GetAllAsync()).ToList();

        // Assert
        result.Count.ShouldBe(3);
        result[0].Name.ShouldBe("A");
        result[1].Name.ShouldBe("B");
        result[2].Name.ShouldBe("C");
    }

    [Test]
    public async Task GetAllAsync_SameSortOrder_OrdersByName()
    {
        // Arrange
        var categories = new List<Category>
        {
            new() { Name = "Zebra", SortOrder = 1 },
            new() { Name = "Apple", SortOrder = 1 }
        };
        _categoryRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(categories);

        // Act
        var result = (await _sut.GetAllAsync()).ToList();

        // Assert
        result[0].Name.ShouldBe("Apple");
        result[1].Name.ShouldBe("Zebra");
    }

    [Test]
    public async Task UpdateAsync_ExistingCategory_UpdatesFields()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var category = new Category { Id = categoryId, Name = "Old Name", Description = "Old Desc", Color = "#000000" };
        _categoryRepo.GetByIdAsync(categoryId, Arg.Any<CancellationToken>()).Returns(category);
        var request = new UpdateCategoryRequest { Name = "New Name", Description = "New Desc", Color = "#FF0000" };

        // Act
        var result = await _sut.UpdateAsync(categoryId, request);

        // Assert
        result.Name.ShouldBe("New Name");
        result.Description.ShouldBe("New Desc");
        result.Color.ShouldBe("#FF0000");
        await _categoryRepo.Received(1).UpdateAsync(category, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateAsync_PartialUpdate_OnlyChangesProvidedFields()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var category = new Category { Id = categoryId, Name = "Original", Description = "Original Desc", Color = "#000000" };
        _categoryRepo.GetByIdAsync(categoryId, Arg.Any<CancellationToken>()).Returns(category);
        var request = new UpdateCategoryRequest { Name = "Updated" };

        // Act
        var result = await _sut.UpdateAsync(categoryId, request);

        // Assert
        result.Name.ShouldBe("Updated");
        result.Description.ShouldBe("Original Desc");
        result.Color.ShouldBe("#000000");
    }

    [Test]
    public async Task UpdateAsync_NonExistingCategory_ThrowsInvalidOperationException()
    {
        // Arrange
        _categoryRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Category?)null);

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.UpdateAsync(Guid.NewGuid(), new UpdateCategoryRequest { Name = "Test" }));
    }

    [Test]
    public async Task DeleteAsync_ExistingCategory_DeletesAndReassignsWatches()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var defaultCategoryId = Guid.NewGuid();
        var category = new Category { Id = categoryId, Name = "ToDelete", IsDefault = false };
        var defaultCategory = new Category { Id = defaultCategoryId, Name = "Uncategorized", IsDefault = true };
        var watches = new List<WatchedSite>
        {
            new() { Url = "https://example.com", CategoryId = categoryId }
        };

        _categoryRepo.GetByIdAsync(categoryId, Arg.Any<CancellationToken>()).Returns(category);
        _categoryRepo.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<Category, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(defaultCategory);
        _watchRepo.FindAsync(
            Arg.Any<Expression<Func<WatchedSite, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(watches);

        // Act
        await _sut.DeleteAsync(categoryId);

        // Assert
        await _categoryRepo.Received(1).DeleteAsync(categoryId, Arg.Any<CancellationToken>());
        watches[0].CategoryId.ShouldBe(defaultCategoryId);
        await _watchRepo.Received(1).UpdateAsync(watches[0], Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteAsync_DefaultCategory_ThrowsInvalidOperationException()
    {
        // Arrange
        var categoryId = Guid.NewGuid();
        var category = new Category { Id = categoryId, Name = "Uncategorized", IsDefault = true };
        _categoryRepo.GetByIdAsync(categoryId, Arg.Any<CancellationToken>()).Returns(category);

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.DeleteAsync(categoryId));
        ex.Message.ShouldContain("default category");
    }

    [Test]
    public async Task DeleteAsync_NonExistingCategory_ThrowsInvalidOperationException()
    {
        // Arrange
        _categoryRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Category?)null);

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => _sut.DeleteAsync(Guid.NewGuid()));
    }

    [Test]
    public async Task GetDefaultCategoryAsync_Exists_ReturnsExisting()
    {
        // Arrange
        var defaultCategory = new Category { Name = "Uncategorized", IsDefault = true };
        _categoryRepo.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<Category, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(defaultCategory);

        // Act
        var result = await _sut.GetDefaultCategoryAsync();

        // Assert
        result.ShouldBe(defaultCategory);
        await _categoryRepo.DidNotReceive().InsertAsync(Arg.Any<Category>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetDefaultCategoryAsync_NotExists_CreatesDefault()
    {
        // Arrange
        _categoryRepo.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<Category, bool>>>(), Arg.Any<CancellationToken>())
            .Returns((Category?)null);

        // Act
        var result = await _sut.GetDefaultCategoryAsync();

        // Assert
        result.ShouldNotBeNull();
        result.Name.ShouldBe("Uncategorized");
        result.IsDefault.ShouldBeTrue();
        result.SortOrder.ShouldBe(int.MaxValue);
        await _categoryRepo.Received(1).InsertAsync(Arg.Any<Category>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetWatchCountsAsync_GroupsByCategory()
    {
        // Arrange
        var cat1Id = Guid.NewGuid();
        var cat2Id = Guid.NewGuid();
        var defaultCategory = new Category { Id = Guid.NewGuid(), Name = "Uncategorized", IsDefault = true };
        var watches = new List<WatchedSite>
        {
            new() { Url = "https://a.com", CategoryId = cat1Id },
            new() { Url = "https://b.com", CategoryId = cat1Id },
            new() { Url = "https://c.com", CategoryId = cat2Id }
        };

        _watchRepo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(watches);
        _categoryRepo.FirstOrDefaultAsync(
            Arg.Any<Expression<Func<Category, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(defaultCategory);

        // Act
        var result = await _sut.GetWatchCountsAsync();

        // Assert
        result[cat1Id].ShouldBe(2);
        result[cat2Id].ShouldBe(1);
    }

    [Test]
    public async Task ReorderAsync_UpdatesSortOrder()
    {
        // Arrange
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var cat1 = new Category { Id = id1, Name = "Cat1", SortOrder = 5 };
        var cat2 = new Category { Id = id2, Name = "Cat2", SortOrder = 10 };

        _categoryRepo.GetByIdAsync(id1, Arg.Any<CancellationToken>()).Returns(cat1);
        _categoryRepo.GetByIdAsync(id2, Arg.Any<CancellationToken>()).Returns(cat2);

        // Act
        await _sut.ReorderAsync([id2, id1]);

        // Assert
        cat2.SortOrder.ShouldBe(0);
        cat1.SortOrder.ShouldBe(1);
        await _categoryRepo.Received(2).UpdateAsync(Arg.Any<Category>(), Arg.Any<CancellationToken>());
    }
}
