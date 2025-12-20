using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Authentication;

/// <summary>
/// User service implementation for managing users in SSO mode.
/// Handles auto-provisioning of users on first login and profile updates.
/// </summary>
public class UserService(
    IRepository<User> userRepository,
    ILogger<UserService> logger) : IUserService
{
    /// <inheritdoc />
    public async Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await userRepository.GetByIdAsync(id, ct);
    }
    
    /// <inheritdoc />
    public async Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        var users = await userRepository.FindAsync(
            u => u.Username.ToLower() == username.ToLower(), 
            ct);
        return users.FirstOrDefault();
    }
    
    /// <inheritdoc />
    public async Task<User> GetOrCreateFromSsoAsync(
        string username,
        string? email,
        string? displayName,
        IReadOnlyList<string> groups,
        CancellationToken ct = default)
    {
        var existingUser = await GetByUsernameAsync(username, ct);
        
        if (existingUser is not null)
        {
            // Update profile data and last seen time
            var needsUpdate = false;
            
            if (existingUser.Email != email)
            {
                existingUser.Email = email;
                needsUpdate = true;
            }
            
            if (existingUser.DisplayName != displayName)
            {
                existingUser.DisplayName = displayName;
                needsUpdate = true;
            }
            
            if (!existingUser.Groups.SequenceEqual(groups))
            {
                existingUser.Groups = groups.ToList();
                needsUpdate = true;
            }
            
            existingUser.LastSeen = DateTime.UtcNow;
            
            if (needsUpdate)
            {
                logger.LogDebug("Updating profile for user {Username}", username);
            }
            
            await userRepository.UpdateAsync(existingUser, ct);
            return existingUser;
        }
        
        // Create new user
        var newUser = new User
        {
            Username = username,
            Email = email,
            DisplayName = displayName,
            Groups = groups.ToList(),
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            IsActive = true
        };
        
        await userRepository.InsertAsync(newUser, ct);
        logger.LogInformation("Created new user {Username} with ID {UserId}", username, newUser.Id);
        
        return newUser;
    }
    
    /// <inheritdoc />
    public async Task<IEnumerable<User>> GetAllAsync(CancellationToken ct = default)
    {
        return await userRepository.GetAllAsync(ct);
    }
    
    /// <inheritdoc />
    public async Task UpdateAsync(User user, CancellationToken ct = default)
    {
        await userRepository.UpdateAsync(user, ct);
    }
    
    /// <inheritdoc />
    public async Task DeactivateAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
        {
            logger.LogWarning("Attempted to deactivate non-existent user {UserId}", userId);
            return;
        }
        
        user.IsActive = false;
        await userRepository.UpdateAsync(user, ct);
        logger.LogInformation("Deactivated user {Username} ({UserId})", user.Username, userId);
    }
    
    /// <inheritdoc />
    public async Task ReactivateAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await userRepository.GetByIdAsync(userId, ct);
        if (user is null)
        {
            logger.LogWarning("Attempted to reactivate non-existent user {UserId}", userId);
            return;
        }
        
        user.IsActive = true;
        await userRepository.UpdateAsync(user, ct);
        logger.LogInformation("Reactivated user {Username} ({UserId})", user.Username, userId);
    }
}
