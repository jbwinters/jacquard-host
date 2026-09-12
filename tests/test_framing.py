import io
import os
import unittest

from jacquard_host.protocol.errors import CarrierLoss, LimitExceeded
from jacquard_host.protocol.framing import FrameReader, frame_bytes, write_frame


def reader_of(data: bytes, deadline: float | None = None) -> tuple[FrameReader, int]:
    read_fd, write_fd = os.pipe()
    os.write(write_fd, data)
    os.close(write_fd)
    return FrameReader(read_fd, deadline=deadline), read_fd


class FrameBytesTest(unittest.TestCase):
    def test_prefix_is_big_endian_payload_length(self) -> None:
        self.assertEqual(frame_bytes(b"{}", max_frame_bytes=1_048_576), b"\x00\x00\x00\x02{}")

    def test_empty_and_oversized_payloads_are_refused(self) -> None:
        with self.assertRaises(LimitExceeded):
            frame_bytes(b"", max_frame_bytes=16)
        with self.assertRaises(LimitExceeded):
            frame_bytes(b"x" * 17, max_frame_bytes=16)
        self.assertEqual(len(frame_bytes(b"x" * 16, max_frame_bytes=16)), 20)

    def test_write_frame_flushes_one_complete_frame(self) -> None:
        stream = io.BytesIO()
        write_frame(stream, b'{"a":1}', max_frame_bytes=1_048_576)
        self.assertEqual(stream.getvalue(), b'\x00\x00\x00\x07{"a":1}')


class FrameReaderTest(unittest.TestCase):
    def test_reads_consecutive_frames(self) -> None:
        reader, fd = reader_of(b"\x00\x00\x00\x02{}\x00\x00\x00\x03{ }")
        try:
            self.assertEqual(reader.read_frame(max_frame_bytes=1_048_576), b"{}")
            self.assertEqual(reader.read_frame(max_frame_bytes=1_048_576), b"{ }")
            with self.assertRaises(CarrierLoss):
                reader.read_frame(max_frame_bytes=1_048_576)
        finally:
            os.close(fd)

    def test_truncated_prefix_and_payload_are_carrier_loss(self) -> None:
        for data in (b"\x00\x00", b"\x00\x00\x00\x05{}"):
            reader, fd = reader_of(data)
            try:
                with self.subTest(data=data), self.assertRaises(CarrierLoss):
                    reader.read_frame(max_frame_bytes=1_048_576)
            finally:
                os.close(fd)

    def test_out_of_range_prefix_is_refused_before_the_payload(self) -> None:
        reader, fd = reader_of(b"\x00\x10\x00\x01" + b"x" * 8)
        try:
            with self.assertRaises(LimitExceeded):
                reader.read_frame(max_frame_bytes=1_048_576)
            # nothing of the refused payload was consumed
            self.assertEqual(reader.read_exactly(8), b"x" * 8)
        finally:
            os.close(fd)
        reader, fd = reader_of(b"\x00\x00\x00\x00")
        try:
            with self.assertRaises(LimitExceeded):
                reader.read_frame(max_frame_bytes=1_048_576)
        finally:
            os.close(fd)

    def test_deadline_raises_timeout_on_a_silent_pipe(self) -> None:
        read_fd, write_fd = os.pipe()
        try:
            reader = FrameReader(read_fd, deadline=0.05)
            with self.assertRaises(TimeoutError):
                reader.read_frame(max_frame_bytes=1_048_576)
        finally:
            os.close(read_fd)
            os.close(write_fd)


if __name__ == "__main__":
    unittest.main()
