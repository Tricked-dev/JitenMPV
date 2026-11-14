#!/usr/bin/env python3
import sys
import webrtcvad

vad = webrtcvad.Vad(2)
frame_bytes = 640
speech = bytearray()
pending = b""
while True:
    block = sys.stdin.buffer.read(frame_bytes * 256)
    if not block:
        break
    pending += block
    available = len(pending) // frame_bytes * frame_bytes
    for offset in range(0, available, frame_bytes):
        speech.append(vad.is_speech(pending[offset:offset + frame_bytes], 16000))
    pending = pending[available:]
sys.stdout.buffer.write(speech)
