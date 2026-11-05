// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Pairing;

namespace AireCast.Core.Tests.Pairing;

public class CredentialsTests
{
    [Fact]
    public void ParsesHomeKit()
    {
        Credentials credentials = Credentials.Parse("aa:bb:cc:dd");

        Assert.Equal(PairingKind.HomeKit, credentials.Kind);
        Assert.Equal([0xAA], credentials.Ltpk);
        Assert.Equal([0xDD], credentials.ClientId);
    }

    [Fact]
    public void ParsesLegacyWithEmptyFields()
    {
        Credentials credentials = Credentials.Parse(":bb::dd");

        Assert.Equal(PairingKind.Legacy, credentials.Kind);
        Assert.Equal([0xBB], credentials.Ltsk);
    }

    [Theory]
    [InlineData("aa")]
    [InlineData("aa:bb:cc")]
    [InlineData("xx:bb:cc:dd")]
    public void RejectsInvalidText(string text) => Assert.Throws<FormatException>(() => Credentials.Parse(text));

    [Fact]
    public void FindsByAnyIdentifier()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"AA:BB": "aa:bb:cc:dd"}""");

            Assert.NotNull(CredentialStore.Find(["DMAP", "AA:BB"], path));
            Assert.Null(CredentialStore.Find(["otro"], path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFileMeansNoCredentials() => Assert.Null(CredentialStore.Find(["AA"], "/no/existe/credentials.json"));
}
