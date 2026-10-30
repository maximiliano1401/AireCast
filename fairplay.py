# SPDX-License-Identifier: LGPL-3.0-or-later
"""FairPlay SAP del lado emisor: intercambio /fp-setup y envoltura de la clave del flujo.

Traducción a Python de internal/airplay/{fpsap,fairplay_crypto,fairplay_md5,fairplay_message,
fairplay_sap}.go de doubletake (https://github.com/omarroth/doubletake), LGPL-3.0-or-later.
Este archivo y fairplay_tables.py se distribuyen bajo esa misma licencia.

Uso:
    fp = FairPlaySession()
    m2 = POST /fp-setup con fp.message1()
    m4 = POST /fp-setup con fp.message3(m2)
    fp.confirm(m4)
    ekey = fp.wrap_key(clave_aes_16_bytes)   # va en el SETUP como "ekey"

Todo opera sobre bytes (0..255). Las operaciones que en Go desbordan un byte se reducen con
_b(); los bytearray lanzan ValueError si alguna se escapa, así no hay errores silenciosos.
"""

import hashlib
import hmac
import os
import struct

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

from fairplay_tables import (
    FIRST_INPUT_MASK,
    FIRST_TABLES,
    MIX_BASES,
    SECOND_OUTPUT_MASK,
    SECOND_TABLES,
    SUBSTITUTION_BASES,
)

M32 = 0xFFFFFFFF


def _b(x):
    return x & 0xFF


def _rotl8(x, k):
    x, k = _b(x), k % 8
    return _b((x << k) | (x >> (8 - k)))


def _rotl32(x, k):
    x &= M32
    return ((x << k) | (x >> (32 - k))) & M32


# --- MD5 modificado (palabras big-endian, mensaje mutado tras la ronda 31) ---------------------

_MD5_SHIFT = [7, 12, 17, 22] * 4 + [5, 9, 14, 20] * 4 + [4, 11, 16, 23] * 4 + [6, 10, 15, 21] * 4
_MD5_K = [
    0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
    0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
    0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
    0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
    0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
    0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
    0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
    0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
]

SWAP, CYCLE, KDF = 0, 1, 2


def _mutate(msg, a, b, c, d, mutation):
    def swap(i, j):
        msg[i], msg[j] = msg[j], msg[i]

    if mutation in (SWAP, CYCLE):
        idx = [a & 15, b & 15, c & 15, d & 15, (a >> 4) & 15, (b >> 4) & 15, (c >> 4) & 15, (d >> 4) & 15]
        if mutation == SWAP:
            for i, j in enumerate(idx):
                swap(i, j)
        else:
            first = msg[idx[0]]
            for i in range(len(idx) - 1):
                msg[idx[i]] = msg[idx[i + 1]]
            msg[idx[-1]] = first
    else:
        swap(a & 15, b & 15)
        swap(c & 15, d & 15)
        for shift in (4, 8, 12):
            swap((a >> shift) & 15, (b >> shift) & 15)


def md5_compress(state, block, mutation):
    msg = list(struct.unpack(">16I", bytes(block)))
    a, b, c, d = state
    for rnd in range(64):
        if rnd < 16:
            f, word = (b & c) | (~b & d), rnd
        elif rnd < 32:
            f, word = (d & b) | (~d & c), (5 * rnd + 1) & 15
        elif rnd < 48:
            f, word = b ^ c ^ d, (3 * rnd + 5) & 15
        else:
            f, word = c ^ (b | (~d & M32)), (7 * rnd) & 15
        a, b, c, d = d, (b + _rotl32(a + f + _MD5_K[rnd] + msg[word], _MD5_SHIFT[rnd])) & M32, b, c
        if rnd == 31:
            _mutate(msg, a, b, c, d, mutation)
    return [(state[0] + a) & M32, (state[1] + b) & M32, (state[2] + c) & M32, (state[3] + d) & M32]


def _words_le(data):
    return list(struct.unpack("<4I", bytes(data)))


def _bytes_be(words):
    return struct.pack(">4I", *words)


# --- Hash SAP propietario ---------------------------------------------------------------------

_SAP_HASH0 = bytes([
    0x96, 0x5f, 0xc6, 0x53, 0xf8, 0x46, 0xcc, 0x18, 0xdf, 0xbe,
    0xb2, 0xf8, 0x38, 0x62, 0xec, 0x22, 0x93, 0xd1, 0x20, 0x8f,
])
_SAP_MATRIX0 = bytes([
    0x43, 0x54, 0x62, 0x7a, 0x18, 0xc3, 0xd6, 0xb3, 0x9a, 0x56,
    0xf6, 0x1c, 0x14, 0x3f, 0x0c, 0x1d, 0x3b, 0x36, 0x83, 0xb1,
    0x39, 0x51, 0x4a, 0xaa, 0x09, 0x3e, 0xfe, 0x44, 0xaf, 0xde,
    0xc3, 0x20, 0x9d, 0x42, 0xb8,
])
_SAP_SEED = bytes([
    0xed, 0x25, 0xd1, 0xbb, 0xbc, 0x27, 0x9f, 0x02, 0xa2, 0xa9, 0x11,
    0x00, 0x0c, 0xb3, 0x52, 0xc0, 0xbd, 0xe3, 0x1b, 0x49, 0xc7,
])


def _rotate_or_zero(x, count):
    count = _b(count)
    return 0 if count == 0 else _rotl8(x, count)


def _wide_seed(x, count):
    x, count = _b(x), _b(count)
    if count == 0:
        return _SAP_SEED[0]
    right = x >> (8 - count) if count < 8 else 0
    return _SAP_SEED[((x << count) | right) % len(_SAP_SEED)]


def _maj(a, b, c):
    a, b, c = _b(a), _b(b), _b(c)
    return a ^ ((a ^ b) & (a ^ c))


def _sel(mask, if_set, if_clear):
    return _b(if_clear ^ ((if_set ^ if_clear) & mask))


def _sq(v):
    return _b(_b(v) * _b(v))


def _cube(v):
    v = _b(v)
    return _b(v * v * v)


def _nonlinear_circuit(hash_, matrix, aux, work):
    """Traducción línea a línea de sapState.nonlinearCircuit (Go).

    En Go, * / % << >> & &^ tienen más precedencia que + - | ^; aquí todo va con paréntesis.
    Antes de / y >> se reduce a byte con _b() porque en Go el operando ya es un byte.
    """
    def hi(i): return hash_[_b(i) % 20]
    def si(i): return _SAP_SEED[_b(i) % 21]
    def h(i): return hi(work[i])
    def m(i): return matrix[work[i] % 35]
    def s(i): return si(work[i])
    def ma(i): return matrix[aux[i] % 35]

    matrix[12] = _b(0x14 + (_sel(92, work[64], work[99] // 3) & _wide_seed(s(206), 4)))
    work[4] = _b(2 * _sq(work[99] // 5))
    work[153] = _b(work[153] ^ (_sq(m(203)) * work[190]))
    hash_[3] = _b(0x13 ^ ((s(205) >> 1) & 0x10))
    work[33] = _b(work[33] - (s(36) & ~9))
    aux[5] = _b(((m(67) & ~2) | 1 | ((h(181) >> 6) & 2) | (hash_[3] & 0x10)) - 15)
    matrix[12] = 0x07
    work[2] = _b(work[2] - 64)
    hash_[19] = s(58)
    aux[4] = _b(92 - m(32))
    aux[9] = _b(m(15) + 0x9e)
    work[34] = _b(work[34] + si(aux[9]) // 5)
    hash_[19] = _b(hash_[19] + (0xe6 ^ ((hi(aux[9]) >> 1) & 0x66)))
    work[15] = _b(work[15] ^ (3 * _rotate_or_zero(work[72], -s(190) & 7) - 9 * s(126)))
    hash_[15] = _b(hash_[15] ^ _cube(m(181)))
    matrix[4] = _b(matrix[4] ^ (work[202] // 3))
    matrix[1] = _b(matrix[1] + _cube(_maj(92 - hi(aux[4]), ~work[105], 0xc6)))
    hash_[19] = _b(hash_[19] ^ ((224 | (s(92) & 27)) * m(41) // 3))
    work[140] = _b(work[140] + _rotate_or_zero(92, -work[5] & 7))
    matrix[12] = _b(matrix[12] + _maj(~work[4] ^ m(12), work[182], 192))
    work[36] = _b(work[36] + 125)
    work[124] = _rotl8(_maj(_maj(work[138], hash_[15], 74), h(43), 95), 4)
    aux_hash = hi(aux[9])
    aux[1] = _b(0x4c & ~(aux_hash & (s(68) << 1)))
    aux[2] = _b(222 - _maj((work[177] + s(79)) >> 1, 3 * work[148] // 5, matrix[1]))
    matrix[16] = _b(matrix[16] + (((ma(4) & ~0x60) | aux_hash | 8) - (_rotl8(work[33], 2) | 128)))
    hash_[14] = _b(hash_[14] ^ ma(2))
    work[19] = _b(work[19] + _maj(_rotate_or_zero(si(h(201)), (m(112) << 1) & 6),
                                  ((h(208) & ~0x7c) | (h(164) & 0x7c)) // 5, 37))
    matrix[8] = _b(_rotate_or_zero(140, -_sq(s(45)) & 7) ^ aux[4])
    work[190] = 56
    work[53] = _b(~((h(83) | 204) // 5))
    hash_[13] = _b(hash_[13] + h(41))
    hash_[10] = _maj(ma(4), work[2], aux[2]) // 15
    aux[3] = _b(92 - _sq(0x28 | (ma(1) & (0x12 | (s(2) & 4)))))
    seed_bits = si(aux[4])
    matrix[13] = _b(matrix[13] ^ seed_bits)
    aux[6] = _b(92 + _sq(_maj(m(179) - 38, aux[2], 177)))
    expansion_bits = _maj(aux[3] + (aux[4] & 74), ~seed_bits, 121)
    work[47] = _b(work[47] ^ (m(89) + _maj(expansion_bits ^ 0xa6, aux[4], 4)))
    aux[7] = _b(seed_bits // 3 - ma(9) - (0x14 | (work[151] & ((aux[4] & 0x88) | 0x62)) | (aux[4] & 0x22)))
    expanded_selector = _b(expansion_bits ^ ((aux[4] & 0xca) >> 1) ^ 75)
    aux[9] = _b(aux[9] + (0x80 | (_maj(aux[7], work[151], 0x20) & 0x64) | (seed_bits & 0x44) | (ma(9) & 0x1b)))
    matrix[33] = _b(matrix[33] ^ work[26])
    matrix[30] = _b((aux[9] // 3 - ((aux[4] & ~8) | 0x13)) ^ h(122))
    work[22] = _b((m(90) & 0x1b) | 0x44)
    wide = _sel(71, matrix[expanded_selector % 35], si(aux[5]))
    matrix[18] = _b(matrix[18] + ((wide * wide * wide) >> 1))
    matrix[5] = _b(matrix[5] - s(92))
    matrix[18] = _b(matrix[18] ^ (_sel(aux[3], ma(3), _sel(16, m(183), work[41]))
                                  * _sel(expanded_selector, h(59), work[17])))
    matrix[22] = _b(_maj(_sel(hash_[14] | 28, (work[7] & 28) | 0x82, h(93)),
                         _rotate_or_zero(ma(4), _rotate_or_zero(work[11], -m(28) & 7) & 7),
                         matrix[33]) + 74)
    hash_[15] = _b(hash_[15] - _maj(_maj(aux[3], aux[4], 214), si(h(39) ^ 217), aux[6]))

    hash9 = hi(aux[9])
    indexed_hash = hi(((aux[4] // 3) - (aux[9] | work[22])) ^ aux[6]
                      ^ (((m(57) | hash9) & (0x52 | (aux[9] & 0x0d))) | (((m(57) & hash9) | aux[9]) & 0x20)))
    aux[6] = _b(_sq(_sq(h(99))) | ma(9))
    aux[1] = _b(aux[1] + _rotate_or_zero(h(151) | s(202), h(50) & 7)
                + _maj(h(4), (_sel(matrix[16], indexed_hash, m(138)) + _sel(17, work[33], s(39))) // 5, 147))
    aux[0] = _sel(hash_[10] & 7, ma(6) & h(209),
                  _sel(0x47, _rotate_or_zero(s(127), ma(6) & 7), si(ma(5)) << 1))
    selected_square = _sel(198, _sq(m(14)), h(145) ^ aux[0])
    seed9 = si(aux[9])
    hash3 = hi(aux[3])
    matrix[2] = _b(matrix[2] + (((hash3 << 1) & ((work[25] & 0x96) | (seed9 & 8))) | (seed9 & 0x40)))
    matrix[14] = _b(matrix[14] - _sel(34, work[97], ma(3) & (aux[0] ^ m(100))))
    work[23] = _b(work[23] ^ (_maj(_maj(s(17), hash3, aux[0]), work[50] // 3, 0x76) << 1))
    hash_[17] = 115
    hash_[13] = _b(((_maj(hi(aux[7]), work[10], 82) >> 1) & 0x68) | (h(39) & 0x17))
    matrix[33] = _b(matrix[33] - (work[113] & 9))
    matrix[28] = _b(matrix[28] - ((aux[3] & ~0x20) | ((work[110] >> 1) & 0x20)))
    work[95] = si(aux[3])
    hash_[15] = _b(_maj(work[95] - 48, ~work[184], 189) & _cube(_maj(aux[7], si(aux[1]), 0xaa)))
    matrix[22] = _b(matrix[22] + work[183])
    aux[4] = _b(aux[4] ^ (3 * s(1)))
    aux[5] = _b(aux[5] + 198 * _maj(s(178), ma(1), 209) * h(13) * (s(26) >> 1))
    aux[8] = _sel(10, ma(3), ma(9))
    matrix[18] = _b(matrix[18] - _sel(hash_[15], aux[5] // 15, _cube(hi(aux[6]) | 81)))
    aux[1] = _b(aux[1] + si(hi(aux[1])) // 3 - h(160))
    hash_[16] = _b(147 - _maj(aux[0], _maj(s(69), work[172], aux[2] - selected_square + 77), 0xc2 | (aux[0] & 5)))
    hash_[3] = _b(hash_[3] - _wide_seed(_maj(s(155), work[105], 141), _maj(s(168), h(29), 6) & 7))
    work[5] = _b(_rotate_or_zero(0x38, -(h(61) // 5) & 7) ^ (_b(~ma(8)) // 5))
    work[198] = _b(work[198] + work[3])
    wide = 162 | ma(9)
    work[164] = _b(work[164] + wide * wide // 5)
    aux[2] = _b(_maj(_rotate_or_zero(139, -aux[5] & 6), hi(aux[3]), 12)
                | _sel(95, _cube(seed9), hi(aux[7])))
    matrix[12] = _b(matrix[12] + (16 | ((work[103] | 60) & (aux[2] | (work[103] & 32)))) // 3)
    work[143] = _b(work[143] - (0x12 | (_sel(aux[9], _sel(matrix[8], work[35], aux[7]), aux[8] // 3)
                                        & (0x4d | ((work[172] >> 1) & 0x20)))))
    matrix[29] = 162
    hash_[15] = _b(hash_[15] + _maj(m(149) ^ _sq(work[43]), _sel(95, h(125), si(aux[1])) >> 1, 115))
    aux[9] = _b(aux[9] - hi(aux[7]))
    hash_[7] = _b(hash_[7] - _sq(_rotate_or_zero(ma(5), -m(17) * (m(17) & 1))))
    matrix[8] = _b(matrix[8] + _cube(s(202)) - work[184])
    hash_[16] = _b((m(102) << 1) & 0x84)
    aux[6] = _b(aux[6] ^ (si(aux[7]) >> 1))
    hash_[7] = _b(hash_[7] - (h(191) - _sel(177, si(si(aux[1])), s(80) << 1)))
    hash_[6] = h(119)
    hash_[12] = _b((hi(aux[8]) ^ (m(71) + m(15)))
                   & _maj((work[118] & ~0x2c) | 2, _sq(hi(aux[9])), 27))
    digest_index = _sel(0xa9, s(57) * 231, _maj(work[32], ma(1), 23)) // 5
    seed_sample = si(aux[6])
    aux[5] = _b(_maj((seed_sample & 0x1c) | (h(82) & 0xa2) | (si(digest_index) & 0x41),
                     _maj(_cube(hi(aux[7])), work[82], 92), 192) ^ digest_index)
    matrix[25] = _b(matrix[25] ^ (2 * hi(aux[9]) * work[5]
                                  - (_rotate_or_zero(aux[4], seed_sample & 7) & (aux[3] + 110))))


def sap_hash(block):
    """Hash SAP propietario de FairPlay sobre un bloque de 64 bytes -> 16 bytes."""
    hash_ = bytearray(_SAP_HASH0)
    matrix = bytearray(_SAP_MATRIX0)
    aux = bytearray(10)
    work = bytearray(block[(i & 63) ^ 3] for i in range(210))

    for i in range(840):
        x = work[((i - 155) & M32) % 210]
        y = work[((i - 57) & M32) % 210]
        z = work[((i - 13) & M32) % 210]
        w = work[i % 210]
        work[i % 210] = _b(_rotl8(y, 5) + (_rotl8(z, 3) ^ w) - _rotl8(x, 7))

    _nonlinear_circuit(hash_, matrix, aux, work)

    out = bytearray(16)
    out[0:3] = aux[0:3]
    out[4:11] = aux[3:10]
    for i in range(16):
        out[i] = _b(out[i] + 0xe1)
    out[3], out[11] = 0x3d, 0x3c
    out[10] ^= aux[3] ^ 133

    for i, value in enumerate(work):
        if i < len(matrix):
            value ^= matrix[i]
        if i < len(hash_):
            value ^= hash_[i]
        out[i & 15] ^= value

    for i in range(256):
        out[i & 15] ^= _rotl8(out[(i - 7) & 15], 1) ^ _rotl8(out[(i - 5) & 15], 6) ^ _rotl8(out[(i - 1) & 15], 5)
    return bytes(out)


# --- Cifrado de mensajes FPLY (AES con claves intermedias propias) ----------------------------

_MESSAGE_IV = [
    bytes.fromhex("5752f1b7549d8f870c10485a6088cadb"),
    bytes.fromhex("df7b1563f005587752a90402b9a39295"),
    bytes.fromhex("68b54611fb04de676c968efb8c9db0c9"),
    bytes.fromhex("27078b2123361e7adc9d0b115354690d"),
]
_ROUND_KEY0 = bytes.fromhex("d30dfdb563def72b012ead72884dca25")
_ROUND_KEY10 = bytes.fromhex("cd5ecf47e9af289b51188f68d085eb69")
_MIDDLE_KEYS = [[bytes.fromhex(k) for k in mode] for mode in (
    ("6af008a6ae5118268acccb4357d1e711", "c6b579e0dfce8114d41d91c868513be3", "bafc24065ebc9cf6131652de75afd0de",
     "81a8b516836ee6493cccc125db99e0ec", "781b6ceb300cfe8f3c0c54c92dc7e2a3", "594cc871ad4a3dd851a884b0ed048f5c",
     "afea245eae1f2de557bb511b17ddcc3d", "8e7a945569c09856533872ca6a3a00de", "f3ca31810cb6201b24b5c7e4ee108bc0"),
    ("1b9018bea49613e86ab413627c84a766", "c94c1dc55c8bbb04a940f640f420eba5", "d5664239df63981372b7a8b2360b160b",
     "462cd7af87b8005a47157e32644557cf", "f0c0ac26f01988a8573d39e298f97f6a", "9407abf947788ceada25ee558333edb6",
     "3f820f9ed823ce2f7722c4bab1d2e335", "71fca0ff354f8991109961878bfa7cfb",
     "566c98381eedcfe7954f7213f61fc87e"),
    ("2ab2ebe11496a9655dd1b28a9910ee30", "9cbc01d64fa41ca1f705a0ef7fb47934", "08f02908920c5b348d32d3470b07705b",
     "8735404470486fa4a42b04fbfde11e3c", "d8eba4780f69653dfc7d2b4fbfb1ac68", "a74a7a6696be42e1b884e1acff1311a4",
     "cc7a007cd8a3f330d3f17ba3437d52cd", "1edeff958a6254b20114cfddc02a48f8", "3a36f4e069e1907b2c8c779be7336c09"),
    ("f6a223b4f01d8a7927a7c965de554de0", "0cd1515d43246c87fc2f494364a4cfbc", "d66e3bdd6c7f1b811f90291984b5eaee",
     "530d7ae85b3040569f13130f18fbb1e5", "0784c47a9fe10f3d5ea5432ecdd9f585", "b9b0368eb7d134d90ebd1b94b255a4fb",
     "b2a4352534066fdec1ce38db3d8c3b85", "0b317a942520622d89578e9fe5a13fc3", "bdf569e6befc1d2fc08baf733f1d176a"),
)]

_INV_SBOX = bytes.fromhex(
    "52096ad53036a538bf40a39e81f3d7fb7ce339829b2fff87348e4344c4dee9cb"
    "547b9432a6c2233dee4c950b42fac34e082ea16628d924b2765ba2496d8bd125"
    "72f8f66486689816d4a45ccc5d65b6926c704850fdedb9da5e154657a78d9d84"
    "90d8ab008cbcd30af7e45805b8b34506d02c1e8fca3f0f02c1afbd0301138a6b"
    "3a9111414f67dcea97f2cfcef0b4e67396ac7422e7ad3585e2f937e81c75df6e"
    "47f11a711d29c5896fb7620eaa18be1bfc563e4bc6d279209adbc0fe78cd5af4"
    "1fdda8338807c731b11210592780ec5f60517fa919b54a0d2de57a9f93c99cef"
    "a0e03b4dae2af5b0c8ebbb3c83539961172b047eba77d626e169146355210c7d")
_SBOX = bytearray(256)
for _enc, _plain in enumerate(_INV_SBOX):
    _SBOX[_plain] = _enc


def _gf_mul(a, b):
    product = 0
    while b:
        if b & 1:
            product ^= a
        a = ((a << 1) ^ 0x1b) & 0xFF if a & 0x80 else a << 1
        b >>= 1
    return product


def _xor_key(state, key):
    for i in range(16):
        state[i] ^= key[i]


def _shift_rows(state, inverse):
    prev = bytes(state)
    for row in range(4):
        for col in range(4):
            src = (col - row) & 3 if inverse else (col + row) & 3
            state[4 * col + row] = prev[4 * src + row]


def _mix_columns(state, inverse):
    m = (14, 11, 13, 9) if inverse else (2, 3, 1, 1)
    for col in range(4):
        o = col * 4
        a, b, c, d = state[o:o + 4]
        state[o] = _gf_mul(a, m[0]) ^ _gf_mul(b, m[1]) ^ _gf_mul(c, m[2]) ^ _gf_mul(d, m[3])
        state[o + 1] = _gf_mul(a, m[3]) ^ _gf_mul(b, m[0]) ^ _gf_mul(c, m[1]) ^ _gf_mul(d, m[2])
        state[o + 2] = _gf_mul(a, m[2]) ^ _gf_mul(b, m[3]) ^ _gf_mul(c, m[0]) ^ _gf_mul(d, m[1])
        state[o + 3] = _gf_mul(a, m[1]) ^ _gf_mul(b, m[2]) ^ _gf_mul(c, m[3]) ^ _gf_mul(d, m[0])


def _decrypt_block(state, mode):
    _xor_key(state, _ROUND_KEY10)
    for rnd in range(9, 0, -1):
        _shift_rows(state, inverse=True)
        for i in range(16):
            state[i] = _INV_SBOX[state[i]]
        _xor_key(state, _MIDDLE_KEYS[mode][rnd - 1])
        _mix_columns(state, inverse=True)
    _shift_rows(state, inverse=True)
    for i in range(16):
        state[i] = _INV_SBOX[state[i]]
    _xor_key(state, _ROUND_KEY0)


def _encrypt_block(state, mode):
    _xor_key(state, _ROUND_KEY0)
    for i in range(16):
        state[i] = _SBOX[state[i]]
    _shift_rows(state, inverse=False)
    for rnd in range(9):
        _mix_columns(state, inverse=False)
        _xor_key(state, _MIDDLE_KEYS[mode][rnd])
        for i in range(16):
            state[i] = _SBOX[state[i]]
        _shift_rows(state, inverse=False)
    _xor_key(state, _ROUND_KEY10)


def decrypt_message(message):
    """Descifra el cuerpo SAP (bytes 16..144) de un mensaje FPLY; el modo va en el byte 12."""
    mode = message[12]
    plain = bytearray(128)
    for block in range(8):
        start = 16 + block * 16
        state = bytearray(message[start:start + 16])
        _decrypt_block(state, mode)
        chain = _MESSAGE_IV[mode] if block == 0 else message[start - 16:start]
        for i in range(16):
            plain[block * 16 + i] = state[i] ^ chain[i]
    return bytes(plain)


def encrypt_message(mode, plain):
    """Inverso de decrypt_message: cifra 128 bytes de SAP en CBC con el modo dado."""
    if mode >= len(_MESSAGE_IV) or len(plain) != 128:
        raise ValueError("modo o longitud de mensaje FairPlay no soportados")
    out = bytearray(128)
    chain = _MESSAGE_IV[mode]
    for block in range(8):
        state = bytearray(plain[block * 16 + i] ^ chain[i] for i in range(16))
        _encrypt_block(state, mode)
        out[block * 16:block * 16 + 16] = state
        chain = bytes(state)
    return bytes(out)


def _decrypt_body(mode, payload):
    if mode >= len(_MESSAGE_IV):
        raise ValueError(f"modo FairPlay {mode} no soportado")
    message = bytearray(144)
    message[12] = mode
    message[16:] = payload
    return decrypt_message(message)


# --- Red white-box del intercambio (m3) -------------------------------------------------------

_INITIAL_SESSION_KEY = bytes.fromhex("dcdcf3b90b74dcfb867ff76016729051")
_KDF_PREFIX = bytes.fromhex("fa9cad4d4b68268c7ff38899de922e951e")
_KDF_SUFFIX = bytes.fromhex("ec4e275efdf2e83097ae70fbe0003f1c39")
_DESCRIPTOR_PREFIX = bytes.fromhex("a0449c4d09e4bd7f6ec5d0cc359da7467a")
_DESCRIPTOR_SUFFIX = bytes.fromhex("97b50f84e2155a9c24991cf43a09635547")
_FIXED_BLOCK = bytes.fromhex("afc22ba049effcfbfe67ac5ebef6fbcb")
_FIRST_POSITIONS = [0, 5, 10, 15, 4, 9, 14, 3, 8, 13, 2, 7, 12, 1, 6, 11]
_SECOND_POSITIONS = [0, 13, 10, 7, 4, 1, 14, 11, 8, 5, 2, 15, 12, 9, 6, 3]
_M1_PAYLOAD = bytes([0x02, 0x00, 0x03, 0xbb])
_M3_LABEL = bytes([0x8f, 0x1a, 0x9c])


def _md5_padded(data, total):
    padded = bytearray(total)
    padded[:len(data)] = data
    padded[len(data)] = 0x80
    struct.pack_into("<Q", padded, total - 8, len(data) * 8)
    return padded


def descriptor_for_sap(m3_sap, m2_sap):
    padded = _md5_padded(_DESCRIPTOR_PREFIX + m3_sap + m2_sap + _DESCRIPTOR_SUFFIX, 320)
    state = _words_le(_INITIAL_SESSION_KEY)
    first_final = None
    for off in range(0, 320, 64):
        block = padded[off:off + 64]
        add = struct.unpack("<4I", sap_hash(block))
        state = [(state[i] + add[i]) & M32 for i in range(4)]
        state = md5_compress(state, block, CYCLE)
        if off == 320 - 64:
            first_final = state
            state = md5_compress(state, block, CYCLE)
    return struct.pack(">I", first_final[0]) + _bytes_be(state)


def _masks(seed):
    state = [0x1d4a4587, 0x92f39fcc, 0x1d87d836, 0xcdc86697]
    suffix = bytes.fromhex("57d8eecbdefbcf591c27a2cfbeb089")
    masks = []
    for i in range(9):
        block = bytearray(64)
        block[:20] = seed
        block[20] = i
        block[21:36] = suffix
        block[36] = 0x80
        struct.pack_into("<I", block, 56, 0x320)
        masks.append(_bytes_be(md5_compress(state, block, SWAP)))
    return masks


def _digest32(left, right):
    block = bytearray(64)
    block[:16] = left
    block[16:32] = right
    block[32] = 0x80
    struct.pack_into("<I", block, 56, 0x100)
    return _bytes_be(md5_compress([0xb9f3dcdc, 0xfbdc740b, 0x60f77f86, 0x51907216], block, SWAP))


def _lookup(ref, value, bases):
    table, in_xor, out_xor = ref
    return bases[table][value ^ in_xor] ^ out_xor


def _mix(tables, state, substituted):
    mix = tables[1]
    for word in range(4):
        o = word * 4
        for out_byte in range(4):
            mixed = 0
            for in_byte in range(4):
                mixed ^= _lookup(mix[in_byte][out_byte], substituted[o + in_byte], MIX_BASES)
            state[o + out_byte] = mixed


def _first_network(masks):
    rounds, _, final = FIRST_TABLES
    state = bytearray(a ^ b for a, b in zip(_FIXED_BLOCK, FIRST_INPUT_MASK))
    for bank in range(9):
        substituted = bytearray(16)
        for out, inp in enumerate(_FIRST_POSITIONS):
            substituted[out] = _lookup(rounds[bank][inp], state[inp], SUBSTITUTION_BASES)
        _mix(FIRST_TABLES, state, substituted)
        for i in range(16):
            state[i] ^= masks[bank][i]
    return bytes(_lookup(final[inp], state[inp], SUBSTITUTION_BASES) for inp in _FIRST_POSITIONS)


def _second_network(state, masks):
    rounds, _, final = SECOND_TABLES
    state = bytearray(state)
    for bank in range(8, -1, -1):
        substituted = bytearray(16)
        for out, inp in enumerate(_SECOND_POSITIONS):
            substituted[out] = _lookup(rounds[bank][out], state[inp], SUBSTITUTION_BASES) ^ masks[bank][out]
        _mix(SECOND_TABLES, state, substituted)
    return bytes(_lookup(final[out], state[inp], SUBSTITUTION_BASES) ^ SECOND_OUTPUT_MASK[out]
                 for out, inp in enumerate(_SECOND_POSITIONS))


def exchange_for_sap(m3_sap, m2_sap):
    """Los 20 bytes finales de m3 (y la confirmación que repite m4)."""
    masks = _masks(descriptor_for_sap(m3_sap, m2_sap))
    left = _digest32(_first_network(masks), _FIXED_BLOCK)
    whitebox = _second_network(left, masks)
    return whitebox[:4] + _digest32(left, whitebox)


def derive_wrapping_key(receiver_sap, message):
    material = _md5_padded(_KDF_PREFIX + decrypt_message(message) + receiver_sap + _KDF_SUFFIX, 320)
    state = _words_le(_INITIAL_SESSION_KEY)
    for off in range(0, 320, 64):
        block = material[off:off + 64]
        modified = md5_compress(state, block, KDF)
        hashed = struct.unpack("<4I", sap_hash(block))
        state = [(modified[i] + hashed[i]) & M32 for i in range(4)]
    return _bytes_be(state)


def _record(message_type, payload_length):
    return bytearray(b"FPLY" + bytes([3, 1, message_type, 0]) + struct.pack(">I", payload_length)
                     + bytes(payload_length))


def _validate_record(record, message_type, payload_length):
    if len(record) != 12 + payload_length:
        raise ValueError(f"mensaje FPLY de {len(record)} bytes, se esperaban {12 + payload_length}")
    if record[:4] != b"FPLY" or record[4:8] != bytes([3, 1, message_type, 0]):
        raise ValueError(f"cabecera FPLY inválida: {bytes(record[:8]).hex()}")
    if struct.unpack(">I", record[8:12])[0] != payload_length:
        raise ValueError("longitud declarada en FPLY incorrecta")


def wrap_key(receiver_sap, m3, raw_key, mask=None):
    """Registro ekey de 72 bytes que lleva raw_key cifrada para el receptor."""
    _validate_record(m3, 3, 152)
    if len(raw_key) != 16:
        raise ValueError("la clave AES debe tener 16 bytes")
    mask = mask if mask is not None else os.urandom(16)
    ekey = bytearray(b"FPLY\x01\x02\x01\x00\x00\x00\x00\x3c\x00\x00\x00\x00" + bytes(56))
    ekey[16:32] = mask
    struct.pack_into(">I", ekey, 32, len(raw_key))
    aes = Cipher(algorithms.AES(derive_wrapping_key(receiver_sap, m3)), modes.ECB()).encryptor()
    ekey[56:72] = aes.update(bytes(k ^ x for k, x in zip(raw_key, mask))) + aes.finalize()
    mac_key = descriptor_for_sap(decrypt_message(m3), receiver_sap)
    ekey[36:56] = hmac.new(mac_key, bytes(ekey[:36]) + bytes(raw_key), hashlib.sha1).digest()
    return bytes(ekey)


class FairPlaySession:
    """Un intento de autenticación FairPlay SAP: m1 -> m2 -> m3 -> m4 -> ekey."""

    def __init__(self, entropy=None):
        self.local_sap = b"\x00\x01" + (entropy if entropy is not None else os.urandom(126))
        self.remote_sap = None
        self.m3 = None

    @staticmethod
    def message1():
        record = _record(1, len(_M1_PAYLOAD))
        record[12:] = _M1_PAYLOAD
        return bytes(record)

    def message3(self, m2):
        _validate_record(m2, 2, 130)
        if m2[12] != 2:
            raise ValueError(f"marcador de m2 inválido: {m2[12]}")
        mode = m2[13]
        if mode >= len(_MESSAGE_IV):
            raise ValueError(f"m2 eligió el modo FairPlay {mode}, no soportado")
        m3 = _record(3, 152)
        m3[12] = mode
        m3[13:16] = _M3_LABEL
        m3[16:144] = encrypt_message(mode, self.local_sap)
        self.remote_sap = _decrypt_body(mode, m2[14:142])
        m3[144:] = exchange_for_sap(self.local_sap, self.remote_sap)
        self.m3 = bytes(m3)
        return self.m3

    def confirm(self, m4):
        _validate_record(m4, 4, 20)
        if m4[12:] != self.m3[144:]:
            raise ValueError("la confirmación m4 no coincide con m3")

    def wrap_key(self, raw_key, mask=None):
        return wrap_key(self.remote_sap, self.m3, raw_key, mask)
