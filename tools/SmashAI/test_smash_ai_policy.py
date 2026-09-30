import struct
import unittest

import smash_ai_env as env
import smash_ai_policy as policy


class SmashAiPolicyTests(unittest.TestCase):
    def make_observation(self):
        buf = bytearray(env.PROTOCOL_V2_SIZE)
        flags = env.FLAG_IN_MATCH
        struct.pack_into("<8sIIII", buf, 0, b"SSAI0001", 2, len(buf), 2, 2)
        struct.pack_into("<QI iII", buf, 24, 100, 3600, 12, flags, 0)
        fighter_fmt = "<QIiiiiIffffffQffii"
        struct.pack_into(
            fighter_fmt,
            buf,
            48,
            100,
            1,
            82,
            10,
            0,
            0,
            0,
            -20.0,
            15.0,
            1.0,
            0.0,
            1.0,
            25.0,
            0x1234,
            5.0,
            20.0,
            1,
            2,
        )
        struct.pack_into(
            fighter_fmt,
            buf,
            128,
            100,
            1,
            88,
            20,
            0,
            0,
            1,
            30.0,
            10.0,
            -1.0,
            0.5,
            -1.0,
            70.0,
            0x5678,
            10.0,
            40.0,
            0,
            2,
        )
        struct.pack_into("<IIII", buf, env.PROTOCOL_V1_SIZE, 1, 0, 5, 1)
        return env.SmashObservation.parse(bytes(buf))

    def test_action_table_is_stable(self):
        self.assertEqual(len(policy.ACTION_TABLE), 40)
        self.assertEqual(policy.ACTION_NAMES[0], "neutral")
        self.assertEqual(len(set(policy.ACTION_NAMES)), len(policy.ACTION_NAMES))

    def test_encoder_size_and_range(self):
        observation = self.make_observation()
        vector = policy.encode_observation(observation, 0)
        self.assertEqual(len(vector), policy.OBSERVATION_DIM)
        self.assertEqual(policy.OBSERVATION_DIM, 55)
        self.assertTrue(all(-1.0 <= value <= 2.0 for value in vector))

    def test_controlled_fighter_is_encoded_first(self):
        observation = self.make_observation()
        p1 = policy.encode_observation(observation, 0)
        p2 = policy.encode_observation(observation, 1)
        # First fighter record starts after four global values.
        self.assertAlmostEqual(p1[4 + 1], 0.82)
        self.assertAlmostEqual(p2[4 + 1], 0.88)
        # Relative X for self is always zero.
        self.assertAlmostEqual(p1[4 + 6], 0.0)
        self.assertAlmostEqual(p2[4 + 6], 0.0)

    def test_checkpoint_metadata_shape(self):
        metadata = policy.checkpoint_metadata(fighter_kind=82, controlled_slot=0)
        policy.validate_checkpoint_metadata(metadata)
        self.assertEqual(metadata["action_dim"], 40)
        self.assertEqual(metadata["observation_dim"], 55)


if __name__ == "__main__":
    unittest.main()
