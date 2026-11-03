"""Pruebas de la resolución y el bitrate del duplicado (mirror.py): python -m unittest test_mirror"""

import unittest

import mirror

# SPS reales de x264 (baseline, superfast), con bytes de prevención de emulación (00 00 03).
SPS = [
    ((1280, 720), "6742c01fda014016ec0440000003004000000f23c60ca8"),
    ((1920, 1080), "6742c028da01e0089f970110000003001000000303c8f1832a"),  # 1088 recortado a 1080
    ((1920, 1200), "6742c032da01e0097b0110000003001000000303c8f1832a"),
    ((3840, 2160), "6742c033da00f0010fb0110000030001000003003c8f1832a0"),
    ((1366, 768), "6742c020da01581879bc0440000003004000000f23c60ca8"),  # 1376 recortado a 1366
    ((2880, 1800), "6742c033da00b4038fe5c044000003000400000300f23c60ca80"),
]


class SpsSizeTest(unittest.TestCase):
    def test_sizes(self):
        for size, sps in SPS:
            with self.subTest(size=size):
                self.assertEqual(mirror.sps_size(bytes.fromhex(sps)), size)


class MaxBitrateTest(unittest.TestCase):
    def test_scales_with_pixels_and_fps(self):
        self.assertEqual(mirror.max_bitrate(1280, 720, 30), 5.5)
        self.assertEqual(mirror.max_bitrate(1920, 1080, 30), 12.4)
        self.assertEqual(mirror.max_bitrate(1920, 1080, 60), 24.9)

    def test_limits(self):
        self.assertEqual(mirror.max_bitrate(1280, 720, 15), 4)
        self.assertEqual(mirror.max_bitrate(3840, 2160, 60), 25)
        self.assertEqual(mirror.max_bitrate(None, None, 30), 25)  # nativa sin tamaño conocido


class ScreenCaptureTest(unittest.TestCase):
    def test_rejects_unknown_values(self):
        with self.assertRaises(ValueError):
            mirror.screen_capture("/tmp", 30, "8k")
        with self.assertRaises(ValueError):
            mirror.screen_capture("/tmp", 25, "1080p")


if __name__ == "__main__":
    unittest.main()
