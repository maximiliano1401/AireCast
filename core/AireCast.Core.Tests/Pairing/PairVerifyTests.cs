// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Pairing;
using static AireCast.Core.Tests.PyatvVectors;

namespace AireCast.Core.Tests.Pairing;

public class PairVerifyTests
{
    private static Credentials HomeKitCredentials(byte[]? accessoryId = null) =>
        new(Bytes(TvLtpk), Bytes(ClientSeed), accessoryId ?? Bytes(TvId), Bytes(ClientId));

    private static HomeKitPairVerify Verify(Credentials credentials) => new(credentials, Bytes(OurEphemeral));

    [Fact]
    public void HomeKitMessage3MatchesPyatv() =>
        Assert.Equal(Bytes(Message3), Verify(HomeKitCredentials()).Message3(Bytes(Message2)));

    [Fact]
    public void HomeKitChannelKeysMatchPyatv()
    {
        var verify = Verify(HomeKitCredentials());
        verify.Message3(Bytes(Message2));

        Assert.Equal(Bytes(OutputKey), verify.DeriveKey("Control-Salt", "Control-Write-Encryption-Key"));
        Assert.Equal(Bytes(InputKey), verify.DeriveKey("Control-Salt", "Control-Read-Encryption-Key"));
    }

    [Fact]
    public void HomeKitRejectsAnotherDevice()
    {
        var verify = Verify(HomeKitCredentials(accessoryId: "otro"u8.ToArray()));

        Assert.Throws<PairingException>(() => verify.Message3(Bytes(Message2)));
    }

    [Fact]
    public void HomeKitRejectsForgedSignature()
    {
        byte[] otherKey = PairCrypto.Ed25519PublicKey(Bytes(ClientSeed));
        var verify = Verify(new Credentials(otherKey, Bytes(ClientSeed), Bytes(TvId), Bytes(ClientId)));

        Assert.Throws<PairingException>(() => verify.Message3(Bytes(Message2)));
    }

    [Fact]
    public void HomeKitReportsTvError()
    {
        var verify = Verify(HomeKitCredentials());

        var error = Assert.Throws<PairingException>(() => verify.CheckMessage4(Tlv8.Write((Tlv8.Error, [2]))));
        Assert.Contains("emparejar", error.Message);
    }

    [Fact]
    public void LegacyMessagesMatchPyatv()
    {
        var legacy = new LegacyPairVerify(new Credentials([], Bytes(ClientSeed), [], new byte[8]));

        Assert.Equal(Bytes(LegacyMessage1), legacy.Message1());
        Assert.Equal(Bytes(LegacyMessage2), legacy.Message2([.. Bytes(TvPublic), .. Bytes(LegacyData)]));
    }
}
