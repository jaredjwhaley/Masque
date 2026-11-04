namespace Masque.Vault.Tests;

public class DpapiKeyProtectorTests
{
    [Fact]
    public void ProtectThenUnprotect_ReturnsTheOriginalKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DPAPI exists only on Windows.");
            return;
        }

        var protector = new DpapiKeyProtector();
        byte[] key = [1, 2, 3, 4, 5, 6, 7, 8];

        var protectedKey = protector.Protect(key);

        protectedKey.ShouldNotBe(key);
        protector.Unprotect(protectedKey).ShouldBe(key);
    }

    [Fact]
    public void ForCurrentPlatform_OnWindows_ReturnsDpapi()
    {
        if (!OperatingSystem.IsWindows())
        {
            Should.Throw<PlatformNotSupportedException>(KeyProtectors.ForCurrentPlatform);
            return;
        }

        KeyProtectors.ForCurrentPlatform().ShouldBeOfType<DpapiKeyProtector>();
    }
}
