using Instella.Server.Data.Entities;
using Instella.Server.Services;
using Instella.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using OtpNet;

namespace Instella.Server.Tests.Services;

[TestFixture]
public class AuthServiceTests
{
    private DatabaseFixture _dbFixture = null!;
    private AuthService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _dbFixture = new DatabaseFixture();
        _service = new AuthService(_dbFixture.Context);
    }

    [TearDown]
    public void TearDown()
    {
        _dbFixture.Dispose();
    }

    #region Setup Tests

    [Test]
    public async Task IsSetupCompleteAsync_ReturnsFalse_WhenNoAdminUsers()
    {
        // Act
        var result = await _service.IsSetupCompleteAsync();

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task IsSetupCompleteAsync_ReturnsTrue_WhenAdminUserExists()
    {
        // Arrange
        await _service.CreateAdminUserAsync("admin", "password123");

        // Act
        var result = await _service.IsSetupCompleteAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    #endregion

    #region Admin User Tests

    [Test]
    public async Task CreateAdminUserAsync_CreatesUser()
    {
        // Act
        var user = await _service.CreateAdminUserAsync("testadmin", "SecurePass123!");

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(user.Id, Is.GreaterThan(0));
            Assert.That(user.Username, Is.EqualTo("testadmin"));
            Assert.That(user.PasswordHash, Is.Not.Empty);
            Assert.That(user.PasswordHash, Is.Not.EqualTo("SecurePass123!"));
        });
    }

    [Test]
    public async Task ValidateCredentialsAsync_ReturnsUser_WithValidCredentials()
    {
        // Arrange
        await _service.CreateAdminUserAsync("admin", "password123");

        // Act
        var result = await _service.ValidateCredentialsAsync("admin", "password123");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Username, Is.EqualTo("admin"));
    }

    [Test]
    public async Task ValidateCredentialsAsync_ReturnsNull_WithInvalidPassword()
    {
        // Arrange
        await _service.CreateAdminUserAsync("admin", "password123");

        // Act
        var result = await _service.ValidateCredentialsAsync("admin", "wrongpassword");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ValidateCredentialsAsync_ReturnsNull_WithNonExistentUser()
    {
        // Act
        var result = await _service.ValidateCredentialsAsync("nonexistent", "password");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task GetAdminUserAsync_ReturnsUser_WhenExists()
    {
        // Arrange
        var created = await _service.CreateAdminUserAsync("admin", "password123");

        // Act
        var result = await _service.GetAdminUserAsync(created.Id);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Username, Is.EqualTo("admin"));
    }

    [Test]
    public async Task GetAdminUserAsync_ReturnsNull_WhenNotExists()
    {
        // Act
        var result = await _service.GetAdminUserAsync(999);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ChangePasswordAsync_ReturnsTrueAndUpdates_WithValidCurrentPassword()
    {
        // Arrange
        var user = await _service.CreateAdminUserAsync("admin", "oldpassword");

        // Act
        var result = await _service.ChangePasswordAsync(user.Id, "oldpassword", "newpassword");

        // Assert
        Assert.That(result, Is.True);

        // Verify new password works
        var validated = await _service.ValidateCredentialsAsync("admin", "newpassword");
        Assert.That(validated, Is.Not.Null);
    }

    [Test]
    public async Task ChangePasswordAsync_ReturnsFalse_WithInvalidCurrentPassword()
    {
        // Arrange
        var user = await _service.CreateAdminUserAsync("admin", "oldpassword");

        // Act
        var result = await _service.ChangePasswordAsync(user.Id, "wrongpassword", "newpassword");

        // Assert
        Assert.That(result, Is.False);

        // Verify old password still works
        var validated = await _service.ValidateCredentialsAsync("admin", "oldpassword");
        Assert.That(validated, Is.Not.Null);
    }

    [Test]
    public async Task ChangePasswordAsync_ReturnsFalse_WhenUserNotFound()
    {
        // Act
        var result = await _service.ChangePasswordAsync(999, "old", "new");

        // Assert
        Assert.That(result, Is.False);
    }

    #endregion

    #region TOTP Tests

    [Test]
    public async Task SetupTotpAsync_GeneratesSecret()
    {
        // Arrange
        var user = await _service.CreateAdminUserAsync("admin", "password");

        // Act
        var secret = await _service.SetupTotpAsync(user.Id);

        // Assert
        Assert.That(secret, Is.Not.Empty);
        Assert.That(secret.Length, Is.GreaterThan(10));

        // Verify user has secret but TOTP not enabled
        var updated = await _service.GetAdminUserAsync(user.Id);
        Assert.That(updated!.TotpSecret, Is.EqualTo(secret));
        Assert.That(updated.TotpEnabled, Is.False);
    }

    [Test]
    public async Task SetupTotpAsync_ThrowsException_WhenUserNotFound()
    {
        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _service.SetupTotpAsync(999));

        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    [Test]
    public async Task VerifyAndEnableTotpAsync_EnablesWithValidCode()
    {
        // Arrange
        var user = await _service.CreateAdminUserAsync("admin", "password");
        var secret = await _service.SetupTotpAsync(user.Id);

        // Generate valid TOTP code
        var secretBytes = Base32Encoding.ToBytes(secret);
        var totp = new Totp(secretBytes);
        var code = totp.ComputeTotp();

        // Act
        var result = await _service.VerifyAndEnableTotpAsync(user.Id, code);

        // Assert
        Assert.That(result, Is.True);

        var updated = await _service.GetAdminUserAsync(user.Id);
        Assert.That(updated!.TotpEnabled, Is.True);
    }

    [Test]
    public async Task VerifyAndEnableTotpAsync_ReturnsFalse_WithInvalidCode()
    {
        // Arrange
        var user = await _service.CreateAdminUserAsync("admin", "password");
        await _service.SetupTotpAsync(user.Id);

        // Act
        var result = await _service.VerifyAndEnableTotpAsync(user.Id, "000000");

        // Assert
        Assert.That(result, Is.False);

        var updated = await _service.GetAdminUserAsync(user.Id);
        Assert.That(updated!.TotpEnabled, Is.False);
    }

    [Test]
    public async Task ValidateTotpAndAdvanceStep_AcceptsAFreshCode_ThenRefusesItsReplay()
    {
        var user = await _service.CreateAdminUserAsync("admin", "password");
        var secret = await _service.SetupTotpAsync(user.Id);
        var totp = new Totp(Base32Encoding.ToBytes(secret));

        // Enabling consumed the current time step, so replaying that same code must fail.
        var enableCode = totp.ComputeTotp();
        Assert.That(await _service.VerifyAndEnableTotpAsync(user.Id, enableCode), Is.True);
        var enabled = (await _service.GetAdminUserAsync(user.Id))!;
        Assert.That(await _service.ValidateTotpAndAdvanceStepAsync(enabled, enableCode), Is.False, "a code is never accepted twice");

        // A code from the next time step is fresh.
        var next = totp.ComputeTotp(DateTime.UtcNow.AddSeconds(30));
        Assert.That(await _service.ValidateTotpAndAdvanceStepAsync(enabled, next), Is.True);
        Assert.That(await _service.ValidateTotpAndAdvanceStepAsync(enabled, next), Is.False);
    }

    [Test]
    public async Task ValidateTotpAndAdvanceStep_ReturnsFalse_WhenNotEnabled()
    {
        var user = await _service.CreateAdminUserAsync("admin", "password");
        await _service.SetupTotpAsync(user.Id);

        Assert.That(await _service.ValidateTotpAndAdvanceStepAsync(user, "123456"), Is.False);
    }

    [Test]
    public async Task TotpSecret_IsStoredProtected()
    {
        var protectedService = new AuthService(_dbFixture.Context,
            new SecretProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        var user = await protectedService.CreateAdminUserAsync("admin2", "password");

        var secret = await protectedService.SetupTotpAsync(user.Id);

        var stored = (await _service.GetAdminUserAsync(user.Id))!.TotpSecret;
        Assert.That(stored, Is.Not.EqualTo(secret), "the database never holds the plaintext secret (10.10)");
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        Assert.That(await protectedService.VerifyAndEnableTotpAsync(user.Id, totp.ComputeTotp()), Is.True);
    }

    [Test]
    public async Task DisableTotpAsync_DisablesTotp()
    {
        // Arrange
        var user = await _service.CreateAdminUserAsync("admin", "password");
        var secret = await _service.SetupTotpAsync(user.Id);
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        await _service.VerifyAndEnableTotpAsync(user.Id, totp.ComputeTotp());

        // Act
        var result = await _service.DisableTotpAsync(user.Id);

        // Assert
        Assert.That(result, Is.True);

        var updated = await _service.GetAdminUserAsync(user.Id);
        Assert.That(updated!.TotpEnabled, Is.False);
        Assert.That(updated.TotpSecret, Is.Null);
    }

    [Test]
    public void GenerateTotpUri_CreatesValidUri()
    {
        // Arrange
        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

        // Act
        var uri = _service.GenerateTotpUri(secret, "testuser", "TestApp");

        // Assert
        Assert.That(uri, Does.StartWith("otpauth://totp/"));
        Assert.That(uri, Does.Contain("TestApp"));
        Assert.That(uri, Does.Contain("testuser"));
    }

    #endregion

    #region API Key Tests

    [Test]
    public async Task CreateApiKeyAsync_CreatesKeyWithHash()
    {
        // Act
        var (key, plainKey) = await _service.CreateApiKeyAsync("Test Key", ApiKeyScope.Admin);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(key.Id, Is.GreaterThan(0));
            Assert.That(key.Name, Is.EqualTo("Test Key"));
            Assert.That(key.KeyHash, Is.Not.Empty);
            Assert.That(key.KeyHash, Is.Not.EqualTo(plainKey));
            Assert.That(plainKey, Is.Not.Empty);
        });
    }

    [Test]
    public async Task CreateApiKeyAsync_SetsCorrectPermissions()
    {
        // Act
        var (key, _) = await _service.CreateApiKeyAsync(
            "Test Key", ApiKeyScope.Package, canUpload: true, canDownload: false);

        // Assert
        Assert.Multiple(() =>
        {
            Assert.That(key.Scope, Is.EqualTo(ApiKeyScope.Package));
            Assert.That(key.CanUpload, Is.True);
            Assert.That(key.CanDownload, Is.False);
        });
    }

    [Test]
    public async Task CreateApiKeyAsync_LinksToPackage()
    {
        // Arrange
        var package = await _dbFixture.SeedPackageAsync();

        // Act
        var (key, _) = await _service.CreateApiKeyAsync(
            "Package Key", ApiKeyScope.Package, packageId: package.Id);

        // Assert
        Assert.That(key.PackageId, Is.EqualTo(package.Id));
    }

    [Test]
    public async Task ValidateApiKeyAsync_ReturnsKey_WhenValid()
    {
        // Arrange
        var (_, plainKey) = await _service.CreateApiKeyAsync("Test Key", ApiKeyScope.Admin);

        // Act
        var result = await _service.ValidateApiKeyAsync(plainKey);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Name, Is.EqualTo("Test Key"));
    }

    [Test]
    public async Task ValidateApiKeyAsync_ReturnsNull_WhenKeyNotFound()
    {
        // Act
        var result = await _service.ValidateApiKeyAsync("invalid-key");

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ValidateApiKeyAsync_ReturnsNull_WhenKeyRevoked()
    {
        // Arrange
        var (key, plainKey) = await _service.CreateApiKeyAsync("Test Key", ApiKeyScope.Admin);
        await _service.RevokeApiKeyAsync(key.Id);

        // Act
        var result = await _service.ValidateApiKeyAsync(plainKey);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ValidateApiKeyAsync_UpdatesLastUsedAt()
    {
        // Arrange
        var (key, plainKey) = await _service.CreateApiKeyAsync("Test Key", ApiKeyScope.Admin);
        var createdAt = key.CreatedAt;

        // Wait a bit to ensure timestamp difference
        await Task.Delay(10);

        // Act
        var result = await _service.ValidateApiKeyAsync(plainKey);

        // Assert
        Assert.That(result!.LastUsedAt, Is.Not.Null);
        Assert.That(result.LastUsedAt, Is.GreaterThanOrEqualTo(createdAt));
    }

    [Test]
    public async Task RevokeApiKeyAsync_RevokesKey()
    {
        // Arrange
        var (key, _) = await _service.CreateApiKeyAsync("Test Key", ApiKeyScope.Admin);

        // Act
        var result = await _service.RevokeApiKeyAsync(key.Id);

        // Assert
        Assert.That(result, Is.True);

        using var db = _dbFixture.CreateNewContext();
        var revoked = db.ApiKeys.Find(key.Id);
        Assert.That(revoked!.IsRevoked, Is.True);
    }

    [Test]
    public async Task RevokeApiKeyAsync_ReturnsFalse_WhenKeyNotFound()
    {
        // Act
        var result = await _service.RevokeApiKeyAsync(999);

        // Assert
        Assert.That(result, Is.False);
    }

    [Test]
    public async Task DeleteApiKeyAsync_DeletesKey()
    {
        // Arrange
        var (key, _) = await _service.CreateApiKeyAsync("Test Key", ApiKeyScope.Admin);

        // Act
        var result = await _service.DeleteApiKeyAsync(key.Id);

        // Assert
        Assert.That(result, Is.True);

        using var db = _dbFixture.CreateNewContext();
        var deleted = db.ApiKeys.Find(key.Id);
        Assert.That(deleted, Is.Null);
    }

    [Test]
    public async Task GetApiKeysAsync_ReturnsAllKeys()
    {
        // Arrange
        await _service.CreateApiKeyAsync("Key 1", ApiKeyScope.Admin);
        await _service.CreateApiKeyAsync("Key 2", ApiKeyScope.Admin);
        await _service.CreateApiKeyAsync("Key 3", ApiKeyScope.Admin);

        // Act
        var keys = await _service.GetApiKeysAsync();

        // Assert
        Assert.That(keys, Has.Count.EqualTo(3));
    }

    #endregion

    #region Permission Tests

    [TestCase(ApiKeyScope.Admin, true, false, "Upload", true)]
    [TestCase(ApiKeyScope.Admin, false, true, "Upload", false)]
    [TestCase(ApiKeyScope.Admin, false, true, "ManageVersions", false)]
    [TestCase(ApiKeyScope.Admin, true, false, "ManageVersions", false)]
    [TestCase(ApiKeyScope.Admin, false, true, "Download", true)]
    [TestCase(ApiKeyScope.Admin, true, false, "Download", false)]
    [TestCase(ApiKeyScope.Package, true, true, "Upload", true)]
    public async Task PermissionMatrix(ApiKeyScope scope, bool canUpload, bool canDownload, string permission, bool expected)
    {
        var package = await _dbFixture.SeedPackageAsync("com.test.app");
        var (key, _) = await _service.CreateApiKeyAsync("k", scope, canUpload, canDownload,
            packageId: scope == ApiKeyScope.Package ? package.Id : null);

        Assert.That(Instella.Server.Auth.ApiPermissions.Allows(key, package, Enum.Parse<Instella.Server.Auth.ApiPermission>(permission)),
            Is.EqualTo(expected));
    }

    [TestCase(false, false)]
    [TestCase(true, true)]
    public async Task PermissionMatrix_ManageVersions_FollowsItsOwnFlag(bool canManageVersions, bool expected)
    {
        // Managing versions follows its own flag, whatever the upload permission.
        var package = await _dbFixture.SeedPackageAsync("com.test.app");
        var (key, _) = await _service.CreateApiKeyAsync("k", ApiKeyScope.Admin, canUpload: true, canDownload: true,
            canManageVersions: canManageVersions);

        Assert.That(key.CanManageVersions, Is.EqualTo(canManageVersions));
        Assert.That(Instella.Server.Auth.ApiPermissions.Allows(key, package, Instella.Server.Auth.ApiPermission.ManageVersions),
            Is.EqualTo(expected));
    }

    [Test]
    public async Task PermissionMatrix_PackageKey_IsLimitedToItsPackage_AndRevokedKeysNothing()
    {
        var mine = await _dbFixture.SeedPackageAsync("com.test.mine");
        var other = await _dbFixture.SeedPackageAsync("com.test.other");
        var (key, _) = await _service.CreateApiKeyAsync("k", ApiKeyScope.Package, canUpload: true, canDownload: true, packageId: mine.Id);

        Assert.That(Instella.Server.Auth.ApiPermissions.Allows(key, mine, Instella.Server.Auth.ApiPermission.Upload), Is.True);
        Assert.That(Instella.Server.Auth.ApiPermissions.Allows(key, other, Instella.Server.Auth.ApiPermission.Upload), Is.False);

        key.IsRevoked = true;
        Assert.That(Instella.Server.Auth.ApiPermissions.Allows(key, mine, Instella.Server.Auth.ApiPermission.Download), Is.False);
    }

    #endregion
}
