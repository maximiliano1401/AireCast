// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Pairing;
using static AireCast.Core.Tests.PyatvVectors;

namespace AireCast.Core.Tests.Pairing;

/// <summary>Con las mismas claves, el emparejamiento produce los mismos bytes que pyatv y srptools.</summary>
public class PairSetupTests
{
    private static byte[] Message2() => Tlv8.Write((Tlv8.Salt, Bytes(SrpSalt)), (Tlv8.PublicKey, Bytes(HapServerPublic)));

    [Fact]
    public void HomeKitSrpMatches()
    {
        SrpClient srp = SrpClient.HomeKit("1234", Bytes(SrpSeed));

        var (proof, _) = srp.Process(Bytes(SrpSalt), Bytes(HapServerPublic));

        Assert.Equal(Bytes(HapPublic), srp.Public);
        Assert.Equal(Bytes(HapProof), proof);
    }

    [Fact]
    public void HomeKitMessage5Matches()
    {
        var setup = new HomeKitPairSetup(Bytes(SrpSeed), Bytes(SrpPairingId));
        setup.Message3(Message2(), "1234");

        byte[] message5 = setup.Message5([]);

        Assert.Equal(Bytes(HapMessage5), Tlv8.Read(message5)[Tlv8.EncryptedData]);
    }

    [Fact]
    public void HomeKitReportsWrongPin()
    {
        var setup = new HomeKitPairSetup(Bytes(SrpSeed), Bytes(SrpPairingId));
        setup.Message3(Message2(), "1234");

        var error = Assert.Throws<PairingException>(() => setup.Message5(Tlv8.Write((Tlv8.Error, [2]))));
        Assert.Equal("PIN incorrecto", error.Message);
    }

    [Fact]
    public void LegacySrpAndKeyMatch()
    {
        var legacy = new LegacyPairSetup(Bytes(SrpSeed), Enumerable.Repeat((byte)0xAA, 8).ToArray());

        var (publicKey, proof) = legacy.Respond(Bytes(SrpSalt), Bytes(LegacyServerPublic), "1234");
        var (epk, tag) = legacy.EncryptPublicKey();

        Assert.Equal(Bytes(LegacyPublic), publicKey);
        Assert.Equal(Bytes(LegacyProof), proof);
        Assert.Equal(Bytes(LegacyEpk), epk);
        Assert.Equal(Bytes(LegacyTag), tag);
        Assert.Equal(PairingKind.Legacy, legacy.Credentials.Kind);
    }

    [Fact]
    public void CredentialsRoundTripThroughTheFile()
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, """{"OTRA": "aa:bb:cc:dd"}""");
        try
        {
            var credentials = new Credentials([1], [2], [3], [4]);

            CredentialStore.Save("TV", credentials, path);

            Assert.Equal("01:02:03:04", CredentialStore.Find(["TV"], path)!.ToString());
            Assert.NotNull(CredentialStore.Find(["OTRA"], path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
