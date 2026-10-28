import logging
import unittest
from unittest.mock import Mock

from src.vcp_test import read_vcp_with_fallback, run_vcp_value_test


class TestVcpValue(unittest.TestCase):
    def setUp(self):
        self.logger = Mock(spec=logging.Logger)
        self.record = object()
        self.reacquire = Mock(return_value=self.record)
        self.wait = Mock()
        self.progress = []

    def run_case(self, state, write, read, **kwargs):
        return run_vcp_value_test(
            code=0x10,
            initial_value=25,
            target_value=0,
            reacquire=self.reacquire,
            write_value=write,
            read_value=read,
            wait=self.wait,
            logger=self.logger,
            progress=self.progress.append,
            **kwargs,
        )

    def test_brightness_zero_and_restore_to_25_both_read_back(self):
        state = {"value": 25}

        def write(_record, _code, value):
            state["value"] = value

        def read(_record, _code):
            return state["value"]

        result = self.run_case(state, write, read)

        self.assertTrue(result["passed"])
        self.assertTrue(result["test_passed"])
        self.assertTrue(result["restore_passed"])
        self.assertEqual(result["test_readback"], 0)
        self.assertEqual(result["restore_readback"], 25)
        self.assertEqual(self.wait.call_args_list, [unittest.mock.call(4.0), unittest.mock.call(4.0)])
        self.assertIn("TEST: writing VCP 0x10 = 0.", self.progress)
        self.assertIn("TEST: reading VCP 0x10.", self.progress)
        self.assertIn("TEST: read VCP 0x10 = 0 (expected 0).", self.progress)
        self.assertIn("RESTORE: read VCP 0x10 = 25 (expected 25).", self.progress)

    def test_write_api_error_does_not_override_matching_readbacks(self):
        state = {"value": 25}

        def write(_record, _code, value):
            state["value"] = value
            raise OSError("simulated write API error after applying value")

        def read(_record, _code):
            return state["value"]

        result = self.run_case(state, write, read)

        self.assertTrue(result["passed"])
        self.assertTrue(result["test_passed"])
        self.assertTrue(result["restore_passed"])
        self.assertEqual(result["test_readback"], 0)
        self.assertEqual(result["restore_readback"], 25)
        self.assertEqual(self.logger.exception.call_count, 2)

    def test_candidate_must_read_back_exactly(self):
        state = {"value": 25}

        def write(_record, _code, value):
            if value == 25:
                state["value"] = 25

        def read(_record, _code):
            return state["value"]

        result = self.run_case(state, write, read)

        self.assertFalse(result["passed"])
        self.assertFalse(result["test_passed"])
        self.assertTrue(result["restore_passed"])
        self.assertEqual(result["test_readback"], 25)
        self.assertEqual(result["restore_readback"], 25)

    def test_restore_must_read_back_initial_value(self):
        state = {"value": 25}

        def write(_record, _code, value):
            if value == 0:
                state["value"] = 0

        def read(_record, _code):
            return state["value"]

        result = self.run_case(state, write, read, restore_attempts=2)

        self.assertFalse(result["passed"])
        self.assertTrue(result["test_passed"])
        self.assertFalse(result["restore_passed"])
        self.assertEqual(result["test_readback"], 0)
        self.assertEqual(result["restore_readback"], 0)
        self.assertEqual(result["restore_attempts"], 2)


class ReadVcpFallbackTests(unittest.TestCase):
    def setUp(self):
        self.logger = Mock(spec=logging.Logger)
        self.progress = []

    def test_direct_read_is_used_when_available(self):
        direct = Mock(return_value=25)
        read_all = Mock()

        value = read_vcp_with_fallback(0x10, direct, read_all, self.logger, self.progress.append)

        self.assertEqual(value, 25)
        read_all.assert_not_called()

    def test_failed_direct_read_finds_code_in_full_scan(self):
        direct = Mock(side_effect=OSError("single read failed"))
        read_all = Mock(return_value={0x10: 25, 0x12: 75})

        value = read_vcp_with_fallback(0x10, direct, read_all, self.logger, self.progress.append)

        self.assertEqual(value, 25)
        read_all.assert_called_once()
        self.assertTrue(any("scanning all VCP codes" in message for message in self.progress))
        self.assertIn("Full scan found VCP 0x10 = 25.", self.progress)

    def test_code_missing_from_full_scan_reports_read_error(self):
        direct = Mock(side_effect=OSError("single read failed"))
        read_all = Mock(return_value={0x12: 75})

        with self.assertRaisesRegex(OSError, "could not be read directly or in the full VCP scan"):
            read_vcp_with_fallback(0x10, direct, read_all, self.logger, self.progress.append)

        self.assertIn("Full scan could not read VCP 0x10.", self.progress)


if __name__ == "__main__":
    unittest.main()
