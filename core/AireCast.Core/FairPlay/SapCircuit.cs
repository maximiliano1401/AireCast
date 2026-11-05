// SPDX-License-Identifier: LGPL-3.0-or-later
namespace AireCast.Core.FairPlay;

/// <summary>
/// Circuito no lineal del hash SAP: traducción línea a línea de doubletake (LGPL-3.0-or-later), generada
/// desde fairplay.py. Todos los valores son bytes guardados en int; B() reduce a byte como hacía Go.
/// </summary>
internal static class SapCircuit
{
    private static readonly byte[] Seed =
    [
        0xed, 0x25, 0xd1, 0xbb, 0xbc, 0x27, 0x9f, 0x02, 0xa2, 0xa9, 0x11,
        0x00, 0x0c, 0xb3, 0x52, 0xc0, 0xbd, 0xe3, 0x1b, 0x49, 0xc7,
    ];

    private static int B(int x) => x & 0xFF;

    internal static int Rotl8(int x, int k)
    {
        x = B(x);
        k %= 8;
        return B(x << k | x >> (8 - k));
    }

    private static int RotateOrZero(int x, int count)
    {
        count = B(count);
        return count == 0 ? 0 : Rotl8(x, count);
    }

    private static int WideSeed(int x, int count)
    {
        x = B(x);
        count = B(count);
        if (count == 0)
            return Seed[0];
        int right = count < 8 ? x >> (8 - count) : 0;
        return Seed[(x << count | right) % Seed.Length];
    }

    private static int Maj(int a, int b, int c)
    {
        a = B(a);
        b = B(b);
        c = B(c);
        return a ^ ((a ^ b) & (a ^ c));
    }

    private static int Sel(int mask, int ifSet, int ifClear) => B(ifClear ^ ((ifSet ^ ifClear) & mask));

    private static int Sq(int v) => B(B(v) * B(v));

    private static int Cube(int v)
    {
        v = B(v);
        return B(v * v * v);
    }

    internal static void Run(int[] hash, int[] matrix, int[] aux, int[] work)
    {
        int Hi(int i) => hash[B(i) % 20];
        int Si(int i) => Seed[B(i) % 21];
        int H(int i) => Hi(work[i]);
        int M(int i) => matrix[work[i] % 35];
        int S(int i) => Si(work[i]);
        int Ma(int i) => matrix[aux[i] % 35];

        matrix[12] = B(0x14 + (Sel(92, work[64], work[99] / 3) & WideSeed(S(206), 4)));
        work[4] = B(2 * Sq(work[99] / 5));
        work[153] = B(work[153] ^ (Sq(M(203)) * work[190]));
        hash[3] = B(0x13 ^ ((S(205) >> 1) & 0x10));
        work[33] = B(work[33] - (S(36) & ~9));
        aux[5] = B(((M(67) & ~2) | 1 | ((H(181) >> 6) & 2) | (hash[3] & 0x10)) - 15);
        matrix[12] = 0x07;
        work[2] = B(work[2] - 64);
        hash[19] = S(58);
        aux[4] = B(92 - M(32));
        aux[9] = B(M(15) + 0x9e);
        work[34] = B(work[34] + Si(aux[9]) / 5);
        hash[19] = B(hash[19] + (0xe6 ^ ((Hi(aux[9]) >> 1) & 0x66)));
        work[15] = B(work[15] ^ (3 * RotateOrZero(work[72], -S(190) & 7) - 9 * S(126)));
        hash[15] = B(hash[15] ^ Cube(M(181)));
        matrix[4] = B(matrix[4] ^ (work[202] / 3));
        matrix[1] = B(matrix[1] + Cube(Maj(92 - Hi(aux[4]), ~work[105], 0xc6)));
        hash[19] = B(hash[19] ^ ((224 | (S(92) & 27)) * M(41) / 3));
        work[140] = B(work[140] + RotateOrZero(92, -work[5] & 7));
        matrix[12] = B(matrix[12] + Maj(~work[4] ^ M(12), work[182], 192));
        work[36] = B(work[36] + 125);
        work[124] = Rotl8(Maj(Maj(work[138], hash[15], 74), H(43), 95), 4);
        int auxHash = Hi(aux[9]);
        aux[1] = B(0x4c & ~(auxHash & (S(68) << 1)));
        aux[2] = B(222 - Maj((work[177] + S(79)) >> 1, 3 * work[148] / 5, matrix[1]));
        matrix[16] = B(matrix[16] + (((Ma(4) & ~0x60) | auxHash | 8) - (Rotl8(work[33], 2) | 128)));
        hash[14] = B(hash[14] ^ Ma(2));
        work[19] = B(work[19] + Maj(RotateOrZero(Si(H(201)), (M(112) << 1) & 6), ((H(208) & ~0x7c) | (H(164) & 0x7c)) / 5, 37));
        matrix[8] = B(RotateOrZero(140, -Sq(S(45)) & 7) ^ aux[4]);
        work[190] = 56;
        work[53] = B(~((H(83) | 204) / 5));
        hash[13] = B(hash[13] + H(41));
        hash[10] = Maj(Ma(4), work[2], aux[2]) / 15;
        aux[3] = B(92 - Sq(0x28 | (Ma(1) & (0x12 | (S(2) & 4)))));
        int seedBits = Si(aux[4]);
        matrix[13] = B(matrix[13] ^ seedBits);
        aux[6] = B(92 + Sq(Maj(M(179) - 38, aux[2], 177)));
        int expansionBits = Maj(aux[3] + (aux[4] & 74), ~seedBits, 121);
        work[47] = B(work[47] ^ (M(89) + Maj(expansionBits ^ 0xa6, aux[4], 4)));
        aux[7] = B(seedBits / 3 - Ma(9) - (0x14 | (work[151] & ((aux[4] & 0x88) | 0x62)) | (aux[4] & 0x22)));
        int expandedSelector = B(expansionBits ^ ((aux[4] & 0xca) >> 1) ^ 75);
        aux[9] = B(aux[9] + (0x80 | (Maj(aux[7], work[151], 0x20) & 0x64) | (seedBits & 0x44) | (Ma(9) & 0x1b)));
        matrix[33] = B(matrix[33] ^ work[26]);
        matrix[30] = B((aux[9] / 3 - ((aux[4] & ~8) | 0x13)) ^ H(122));
        work[22] = B((M(90) & 0x1b) | 0x44);
        int wide = Sel(71, matrix[expandedSelector % 35], Si(aux[5]));
        matrix[18] = B(matrix[18] + ((wide * wide * wide) >> 1));
        matrix[5] = B(matrix[5] - S(92));
        matrix[18] = B(matrix[18] ^ (Sel(aux[3], Ma(3), Sel(16, M(183), work[41])) * Sel(expandedSelector, H(59), work[17])));
        matrix[22] = B(Maj(Sel(hash[14] | 28, (work[7] & 28) | 0x82, H(93)), RotateOrZero(Ma(4), RotateOrZero(work[11], -M(28) & 7) & 7), matrix[33]) + 74);
        hash[15] = B(hash[15] - Maj(Maj(aux[3], aux[4], 214), Si(H(39) ^ 217), aux[6]));
        int hash9 = Hi(aux[9]);
        int indexedHash = Hi(((aux[4] / 3) - (aux[9] | work[22])) ^ aux[6] ^ (((M(57) | hash9) & (0x52 | (aux[9] & 0x0d))) | (((M(57) & hash9) | aux[9]) & 0x20)));
        aux[6] = B(Sq(Sq(H(99))) | Ma(9));
        aux[1] = B(aux[1] + RotateOrZero(H(151) | S(202), H(50) & 7) + Maj(H(4), (Sel(matrix[16], indexedHash, M(138)) + Sel(17, work[33], S(39))) / 5, 147));
        aux[0] = Sel(hash[10] & 7, Ma(6) & H(209), Sel(0x47, RotateOrZero(S(127), Ma(6) & 7), Si(Ma(5)) << 1));
        int selectedSquare = Sel(198, Sq(M(14)), H(145) ^ aux[0]);
        int seed9 = Si(aux[9]);
        int hash3 = Hi(aux[3]);
        matrix[2] = B(matrix[2] + (((hash3 << 1) & ((work[25] & 0x96) | (seed9 & 8))) | (seed9 & 0x40)));
        matrix[14] = B(matrix[14] - Sel(34, work[97], Ma(3) & (aux[0] ^ M(100))));
        work[23] = B(work[23] ^ (Maj(Maj(S(17), hash3, aux[0]), work[50] / 3, 0x76) << 1));
        hash[17] = 115;
        hash[13] = B(((Maj(Hi(aux[7]), work[10], 82) >> 1) & 0x68) | (H(39) & 0x17));
        matrix[33] = B(matrix[33] - (work[113] & 9));
        matrix[28] = B(matrix[28] - ((aux[3] & ~0x20) | ((work[110] >> 1) & 0x20)));
        work[95] = Si(aux[3]);
        hash[15] = B(Maj(work[95] - 48, ~work[184], 189) & Cube(Maj(aux[7], Si(aux[1]), 0xaa)));
        matrix[22] = B(matrix[22] + work[183]);
        aux[4] = B(aux[4] ^ (3 * S(1)));
        aux[5] = B(aux[5] + 198 * Maj(S(178), Ma(1), 209) * H(13) * (S(26) >> 1));
        aux[8] = Sel(10, Ma(3), Ma(9));
        matrix[18] = B(matrix[18] - Sel(hash[15], aux[5] / 15, Cube(Hi(aux[6]) | 81)));
        aux[1] = B(aux[1] + Si(Hi(aux[1])) / 3 - H(160));
        hash[16] = B(147 - Maj(aux[0], Maj(S(69), work[172], aux[2] - selectedSquare + 77), 0xc2 | (aux[0] & 5)));
        hash[3] = B(hash[3] - WideSeed(Maj(S(155), work[105], 141), Maj(S(168), H(29), 6) & 7));
        work[5] = B(RotateOrZero(0x38, -(H(61) / 5) & 7) ^ (B(~Ma(8)) / 5));
        work[198] = B(work[198] + work[3]);
        wide = 162 | Ma(9);
        work[164] = B(work[164] + wide * wide / 5);
        aux[2] = B(Maj(RotateOrZero(139, -aux[5] & 6), Hi(aux[3]), 12) | Sel(95, Cube(seed9), Hi(aux[7])));
        matrix[12] = B(matrix[12] + (16 | ((work[103] | 60) & (aux[2] | (work[103] & 32)))) / 3);
        work[143] = B(work[143] - (0x12 | (Sel(aux[9], Sel(matrix[8], work[35], aux[7]), aux[8] / 3) & (0x4d | ((work[172] >> 1) & 0x20)))));
        matrix[29] = 162;
        hash[15] = B(hash[15] + Maj(M(149) ^ Sq(work[43]), Sel(95, H(125), Si(aux[1])) >> 1, 115));
        aux[9] = B(aux[9] - Hi(aux[7]));
        hash[7] = B(hash[7] - Sq(RotateOrZero(Ma(5), -M(17) * (M(17) & 1))));
        matrix[8] = B(matrix[8] + Cube(S(202)) - work[184]);
        hash[16] = B((M(102) << 1) & 0x84);
        aux[6] = B(aux[6] ^ (Si(aux[7]) >> 1));
        hash[7] = B(hash[7] - (H(191) - Sel(177, Si(Si(aux[1])), S(80) << 1)));
        hash[6] = H(119);
        hash[12] = B((Hi(aux[8]) ^ (M(71) + M(15))) & Maj((work[118] & ~0x2c) | 2, Sq(Hi(aux[9])), 27));
        int digestIndex = Sel(0xa9, S(57) * 231, Maj(work[32], Ma(1), 23)) / 5;
        int seedSample = Si(aux[6]);
        aux[5] = B(Maj((seedSample & 0x1c) | (H(82) & 0xa2) | (Si(digestIndex) & 0x41), Maj(Cube(Hi(aux[7])), work[82], 92), 192) ^ digestIndex);
        matrix[25] = B(matrix[25] ^ (2 * Hi(aux[9]) * work[5] - (RotateOrZero(aux[4], seedSample & 7) & (aux[3] + 110))));
    }
}
