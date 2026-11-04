using Masque.Core.Vault;

namespace Masque.Core.Tests.Vault;

public class InMemoryTokenVaultTests : TokenVaultContractTests
{
    protected override ITokenVault Vault { get; } = new InMemoryTokenVault();
}
