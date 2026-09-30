import struct
import unittest

import smash_ai_env as module


class SmashAiEnvTests(unittest.TestCase):
    def make_state(self, p1_percent=10.0, p2_percent=20.0, p1_stocks=3, p2_stocks=3):
        buf = bytearray(module.EXPECTED_STATE_SIZE)
        struct.pack_into("<8sIIII", buf, 0, b"SSAI0001", 1, len(buf), 2, 2)
        struct.pack_into("<QI iII", buf, 24, 100, 3600, 12, 1, 0)
        fighter_fmt = "<QIiiiiIffffffQffii"
        struct.pack_into(
            fighter_fmt,
            buf,
            48,
            100,
            1,
            1,
            10,
            0,
            p1_stocks,
            0,
            1.0,
            2.0,
            0.5,
            -0.25,
            1.0,
            p1_percent,
            0x1234,
            7.0,
            30.0,
            1,
            2,
        )
        struct.pack_into(
            fighter_fmt,
            buf,
            128,
            100,
            1,
            2,
            11,
            1,
            p2_stocks,
            1,
            -3.0,
            4.0,
            -0.5,
            0.25,
            -1.0,
            p2_percent,
            0x5678,
            8.0,
            40.0,
            0,
            2,
        )
        return module.SmashObservation.parse(bytes(buf))

    def test_layout_matches_guest_size(self):
        self.assertEqual(module._FIGHTER.size, 80)
        self.assertEqual(48 + 3 * module._FIGHTER.size, 288)

    def test_parse_observation(self):
        obs = self.make_state()
        self.assertEqual(obs.total_size, 288)
        self.assertTrue(obs.in_match)
        self.assertEqual(obs.fighter_count, 2)
        self.assertEqual(obs.fighters[0].fighter_kind, 1)
        self.assertAlmostEqual(obs.fighters[1].percent, 20.0)
        self.assertFalse(obs.fighters[2].present)
        self.assertEqual(len(obs.vector()), 55)

    def test_reward_damage(self):
        before = self.make_state(p1_percent=10, p2_percent=20)
        after = self.make_state(p1_percent=15, p2_percent=32)
        reward = module.compute_reward(before, after, controlled_slot=0)
        self.assertAlmostEqual(reward, 0.07, places=6)

    def test_reward_stock(self):
        before = self.make_state(p1_stocks=3, p2_stocks=3)
        after = self.make_state(p1_stocks=3, p2_stocks=2, p2_percent=0)
        reward = module.compute_reward(before, after, controlled_slot=0)
        self.assertAlmostEqual(reward, 1.0, places=6)

    def test_action_packet_layout(self):
        action = module.ControllerAction.from_buttons("a", "r", lx=1.0, ly=-1.0)
        packet = module._ACTION.pack(
            module.BRIDGE_MAGIC,
            module.MSG_ACTION,
            1,
            0,
            action.buttons,
            module._clamp_axis(action.lx),
            module._clamp_axis(action.ly),
            module._clamp_axis(action.rx),
            module._clamp_axis(action.ry),
        )
        self.assertEqual(len(packet), 24)
        self.assertEqual(packet[:4], b"SAI1")
        self.assertEqual(packet[4], 1)
        self.assertEqual(packet[5], 1)
        self.assertEqual(struct.unpack_from("<q", packet, 8)[0], (1 << 0) | (1 << 7))
        self.assertEqual(struct.unpack_from("<h", packet, 16)[0], 32767)
        self.assertEqual(struct.unpack_from("<h", packet, 18)[0], -32768)


if __name__ == "__main__":
    unittest.main()
