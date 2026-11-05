// SPDX-License-Identifier: GPL-3.0-or-later
using AireCast.Core.Pairing;

namespace AireCast.Core.Tests.Pairing;

public class AesCtrTests
{
    // NIST SP 800-38A, F.5.1 (CTR-AES128): dos bloques.
    private static readonly byte[] Key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
    private static readonly byte[] Iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
    private static readonly byte[] Plain = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51");
    private static readonly byte[] Cipher = Convert.FromHexString("874d6191b620e3261bef6864990db6ce9806f66b7970fdff8617187bb9fffdff");

    [Fact]
    public void MatchesNistVector()
    {
        using var aes = new AesCtr(Key, Iv);

        Assert.Equal(Cipher, aes.Transform(Plain));
    }

    [Fact]
    public void ContinuesInTheMiddleOfABlock()
    {
        using var aes = new AesCtr(Key, Iv);

        byte[] split = [.. aes.Transform(Plain.AsSpan(0, 5)), .. aes.Transform(Plain.AsSpan(5, 20)), .. aes.Transform(Plain.AsSpan(25))];

        Assert.Equal(Cipher, split);
    }
}
