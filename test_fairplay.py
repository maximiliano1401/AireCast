# SPDX-License-Identifier: LGPL-3.0-or-later
"""Vectores de prueba de FairPlay SAP, traducidos de los tests de doubletake.

Ejecutar con:  .venv\\Scripts\\python -m unittest test_fairplay
"""

import hashlib
import struct
import unittest

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

import fairplay as fp
from fairplay_tables import (FIRST_INPUT_MASK, FIRST_TABLES, MIX_BASES, SECOND_TABLES,
                             SUBSTITUTION_BASES)

REFERENCE_RECEIVER_SAP = bytes.fromhex(
    "0001cc342a5e5b1a6773c20e21b8224df862481864ef810aae2e3703c8819c23"
    "539de5f5d749bc5b7a266c496283ce7f03937ae1f616de0c15ff338ccaffb09e"
    "aabbe40f5d5f558fb97f1731f8f7da60a0ec6579c33ea98312c3b67135a6694f"
    "f82305d9ba5c615fa254d2b1834583cee42d4426c835a7a5f6c8421c0da3f1c7")
REFERENCE_LOCAL_SAP = bytes.fromhex(
    "0001e4e3dd688293e6fa66b95ba41768e587c65f750218ff1be21543d573cefb"
    "087bd36e0c6363c3c8242f4abcfa6d660b801032015405eb4ab04dda7aeff38f"
    "fb36f4cfa48f0b5d92ae363f68b45925bbe6413ab6bdc4968f548d21e67d20f"
    "1912b6820e53f1013cde29df7350a9b9fa7c51320aea62d2949786c87642e34ba")


def unwrap_key(receiver_sap, m3, ekey):
    aes = Cipher(algorithms.AES(fp.derive_wrapping_key(receiver_sap, m3)), modes.ECB()).decryptor()
    plain = aes.update(bytes(ekey[56:72])) + aes.finalize()
    return bytes(a ^ b for a, b in zip(plain, ekey[16:32]))


def test_message(mode):
    message = bytearray(164)
    message[12] = mode
    for i in range(16, 144):
        message[i] = (i * 5 + 7) & 0xFF
    return message


class FairPlayVectors(unittest.TestCase):
    def test_table_checksum(self):
        digest = hashlib.sha256()
        written = 0

        def write(data):
            nonlocal written
            data = bytes(data)
            digest.update(data)
            written += len(data)

        write(FIRST_INPUT_MASK)
        for rounds, mix, final in (FIRST_TABLES, SECOND_TABLES):
            for rnd in rounds:
                for ref in rnd:
                    write(fp._lookup(ref, v, SUBSTITUTION_BASES) for v in range(256))
            for input_table in mix:
                write(fp._lookup(ref, v, MIX_BASES) for v in range(256) for ref in input_table)
            for ref in final:
                write(fp._lookup(ref, v, SUBSTITUTION_BASES) for v in range(256))
        self.assertEqual(written, 90128)
        self.assertEqual(digest.hexdigest(), "28d0986abebe30458348dfa2957aa1d52d6f3ad5a9468c5d8a9c4139b7ca2b43")

    def test_primitives(self):
        block = bytes((i * 3 + 1) & 0xFF for i in range(64))
        words = fp.md5_compress(fp._words_le(bytes(range(16))), block, fp.KDF)
        self.assertEqual(struct.pack("<4I", *words).hex(), "f6f728cb5a4397b675664f9291b859aa")
        self.assertEqual(fp.sap_hash(block).hex(), "75498a4e218773030e9cdf04f0c49367")

    def test_sap_hash_corpus(self):
        digest = hashlib.sha256()
        state = 0x6a09e667f3bcc909
        mask = (1 << 64) - 1
        for _ in range(64):
            block = bytearray(64)
            for i in range(64):
                state ^= (state << 13) & mask
                state ^= state >> 7
                state ^= (state << 17) & mask
                block[i] = state & 0xFF
            digest.update(fp.sap_hash(block))
        self.assertEqual(digest.hexdigest(), "36ad2a7920076af59452d9f0c91e3b7d1aebc53f9143bd6819e39119d4535c92")

    def test_message_vectors(self):
        cases = [
            (0, "b66a3295ffa6b56e02ed1b3d67fef74b90fe148570de65e6773669126a4905d8405644cae0b2f5ed6109c099c7aea7398dac8d623fbd69b87242b374d98f89502bb5a63e29c46a8ed0e98466966191ec1e6c8675087fde21337db1c8fab4c21db824026335f6fc37e2e5b6f53357d06994bd383d6029a0aff654fb1521bcdde4", "f7dd1ccb9e745f7951a6e325d73a1f5f"),
            (1, "0f95c6ddc8987eda18577da2db074e7c04715af8b3914a73be1b3d6c111953017ee0a39dfcab3e0d57f2f9fbd59c5e18101788c2ab8e3cbb403bcb48b53f3e5bf74f949e79fa5ca679df4bfcb33a69b1442675d03f948fe5bd0c5ffb64b73a5ab58f46d6baae097b599624147c2487991163ecffc4d966240f9526346a10fdb0", "b44ad891396f097aa309bc132f5b8889"),
            (2, "40f18751b44d733e0aa0416401a7d3f40375fad3ce56900602578bca14660909820e6ef3a5e943cafef5370f72c52177d9b82278b414811201a3d99202bedcca26a4d1ad08bc2669f4bae6ca54b8a120d0425edb6082f51f5aecdb547bfdb319099c9ea2729ae6a1c4480827ce9991e273843cf1c7d74ebbebc2657659bcea9f", "d38cd8efecdb20f333273c4312d9b236"),
            (3, "70a3c30edf0e1dfa1785ce4336ed547062672a47f714a0c1f89a83d95691103dfe5cf653d4cb8299793faf33fd0d4482ef5333b41ab094a90e1baf996bcf4989783f6918397fbacddaf00a2b97556dd8099841578bc5eb1444912b47298eaf356fdd6701bb3f64e725a80eb4c6f3556195de35c93e7cc703bdd24351468e9847", "769e2fe4c5ad7fbe6fd6772d00f529f4"),
        ]
        for mode, decrypted, aes_key in cases:
            with self.subTest(mode=mode):
                message = test_message(mode)
                plain = fp.decrypt_message(message)
                self.assertEqual(plain.hex(), decrypted)
                self.assertEqual(fp.encrypt_message(mode, plain), bytes(message[16:144]))
                self.assertEqual(fp.derive_wrapping_key(REFERENCE_RECEIVER_SAP, message).hex(), aes_key)

    def test_key_unwrap_vector(self):
        ekey = bytes((i * 7 + 3) & 0xFF for i in range(72))
        self.assertEqual(unwrap_key(REFERENCE_RECEIVER_SAP, test_message(3), ekey).hex(),
                         "903e5be94732428e9965afb262b193a4")

    def test_exchange_golden_vectors(self):
        captured_m2 = bytes.fromhex("46504c59030102000000008202034a114c26b77d4e2eec2c8f89fdb653b5b32d3576bc176816d110a14c3f53c08dbb936183bfdfe0a4f3c12e85216003b46f738c40c54da6c436d29d1b342d63c7b314309ae79a33bb1787709ef077cbfe4190117a3423e270fd1a2eac44da1a7934f59dc681d1b70783f228c4d077c2d495f5285c3bf8df586fc2ebfe17fb5b65")

        def sparse(index):
            payload = bytearray(128)
            payload[index] = 0x42
            return bytes(payload)

        cases = [
            (bytes(128), "6f627565f3e77f5b5ede91beee7baf92e4241e0b"),
            (b"\xff" * 128, "dc2cc74f2ed55484f59f95b96082f0f5c017dd17"),
            (captured_m2[14:142], "4b911e48af23d8406368aeafbb61bfcd569e3e55"),
            (sparse(0), "9bfb9556b8659c2ac94b7ef9e587d71e159ea624"),
            (sparse(63), "150d9fa4eb456e73ba48de5779c5c996b16b3b23"),
            (sparse(64), "a167db30424ff8890d085c0f1c92b2c5cc06fc45"),
            (sparse(127), "d246ec5e7adc8118994b8df77146529486ac7caf"),
        ]
        for payload, want in cases:
            with self.subTest(want=want):
                got = fp.exchange_for_sap(REFERENCE_LOCAL_SAP, fp._decrypt_body(3, payload))
                self.assertEqual(got.hex(), want)

    def test_descriptor(self):
        one = bytearray(128)
        one[0] = 1
        for payload, want in ((bytes(128), "7e38958ffe4ed433743919fe7eb16376afa4eb9e"),
                              (bytes(one), "ea46797d726c6a9be43ffa72385ff97ce1c54f1b")):
            got = fp.descriptor_for_sap(REFERENCE_LOCAL_SAP, fp._decrypt_body(3, payload))
            self.assertEqual(got.hex(), want)

    def test_session_exchange(self):
        m2 = bytearray(142)
        m2[:12] = b"FPLY" + bytes([3, 1, 2, 0]) + struct.pack(">I", 130)
        m2[12], m2[13] = 2, 3
        entropy = bytes(range(1, 127))
        session = fp.FairPlaySession(entropy)
        self.assertEqual(session.message1().hex(), "46504c590301010000000004020003bb")
        m3 = session.message3(bytes(m2))
        self.assertEqual(len(m3), 164)
        self.assertEqual(fp.decrypt_message(m3), b"\x00\x01" + entropy)
        receiver_sap = fp._decrypt_body(3, m2[14:142])
        self.assertEqual(session.remote_sap, receiver_sap)
        self.assertEqual(m3[144:], fp.exchange_for_sap(b"\x00\x01" + entropy, receiver_sap))
        raw_key = bytes(range(16))
        ekey = session.wrap_key(raw_key, mask=b"\x5a" * 16)
        self.assertEqual(unwrap_key(receiver_sap, m3, ekey), raw_key)
        session.confirm(b"FPLY" + bytes([3, 1, 4, 0]) + struct.pack(">I", 20) + m3[144:])

    def test_alternate_mac_vector(self):
        m2 = bytes.fromhex("46504c5903010200000000820201cf32a25714b2524f8aa0ad7af164e37bcf4424e200047efc0ad67afcd95ded1c2730bb591b962ed63a9c4ded88ba8fc78de64d91ccfd5c7b56da88e31f5cceafc7431995a01665a54e1939d25b94db64b9e45d8d063e1e6af07e9656162b0efa404275ea5a44d9591c7256b9fbe6513898b80227721988571650942ad946688a")
        m3 = bytes.fromhex("46504c590301030000000098018f1a9c5b9228300aafe0b41f28b66a62a6cd62bf84eb623273dead10b1f034a8d568126faa133f6ad5ab91acda3839817b4d9530b679fee43ac9e950f6e7aaf1381bd2d3d5198a03bf5648890d19234270a3583e4651893be09c6c75463c42e544fec9abc9f7722a2cc254364365ef91ded76b8c00f9674b08920fb9401e4be6d52a33f2f9ed6fadb672be45c3cde5ad94f3fea5b32ee4")
        receiver_sap = fp._decrypt_body(m2[13], m2[14:142])
        self.assertEqual(receiver_sap, REFERENCE_RECEIVER_SAP)
        ekey = fp.wrap_key(receiver_sap, m3, bytes(range(16)), mask=bytes.fromhex("c853e777b9b65e7652d768d97c974f15"))
        self.assertEqual(ekey[:56].hex(), "46504c59010201000000003c00000000c853e777b9b65e7652d768d97c974f15000000102a53a0008888fe26bfb1e1f825f38f50d6730059")
        self.assertEqual(fp.descriptor_for_sap(fp.decrypt_message(m3), receiver_sap).hex(),
                         "2fd95dc2c23122bc77c57b983a9188c4760db322")

    def test_key_decrypt_vector(self):
        m3 = bytes.fromhex("46504c590301030000000098018f1a9c7d0af257b31f21f5c2d2bc814c032d457835ad0b06250574bbc7ab4a58cca6eead2c911d7f3e1e7ed4c058955dff3d5ceef014387a985bdb34995015e3dfbdacc56047cb926e093b13e9fdb5e1eee317c018bbc87fc5453c7671647da686da3d564875d03f8aea9d60092de06110bc7be0c16f391c369c75344ae47f33acfcf10e63a9b58bfce215e96001c49e4be967c5067f2a")
        ekey = bytes.fromhex("46504c59010201000000003c0000000088e4f82c8178c18b4751ac24b27c0c2a00000010c899dc6965c1081de6a9d966e2ba3e34548cdbc651c322db18dc22f58fe154a60aecee18")
        self.assertEqual(unwrap_key(REFERENCE_RECEIVER_SAP, m3, ekey).hex(), "8e1214398d46d72e7b1b8e32f80c8bf0")


if __name__ == "__main__":
    unittest.main()
