"""Brings the 7FDL16 engine loops up to a level the game can use.

    python Tools/Audio/level_engine_sounds.py

The 7FDL16 recordings came in some 10 dB under the other engines' (the idle about -22 dBFS RMS, against -12 to -15 for
the ES44AC's and SD40-2's old loops), and the turbo whine near -47. Each file below is read from its untouched copy in
Tools/Audio/7FDL16_originals, raised to the level given (RMS, dBFS), its peaks rounded off by a soft limiter so it can't
clip, and written over its copy in Content/Audio/Engines/7FDL16. Run it again after changing a level: it always starts
from the originals. The loops get louder with the revs, as the real engine does; low idle sits under idle. The startup,
chug, compressor and blowers are left as they came.
"""
import os
import wave

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
ORIGINALS = os.path.join(HERE, "7FDL16_originals")
OUT = os.path.join(HERE, "..", "..", "Content", "Audio", "Engines", "7FDL16")

LEVELS = {
    "FDL16_Idle02.wav": -14.0,
    "es44_idle03.wav": -16.0,   # low idle
    "7FDL16_577.wav": -13.0,
    "7FDL16_717.wav": -12.0,
    "7FDL16_992.wav": -11.0,
    "TurboWhine01.wav": -20.0,  # under the engine; the game sets how much of it's heard (turboVolume)
}

KNEE = 0.6      # the limiter leaves everything under this alone (full scale 1)
CEILING = 0.97  # and rounds the peaks above off to no more than this


def limit(x):
    over = np.abs(x) > KNEE
    room = CEILING - KNEE
    y = x.copy()
    y[over] = np.sign(x[over]) * (KNEE + room * np.tanh((np.abs(x[over]) - KNEE) / room))
    return y


def rms_db(x):
    return 20 * np.log10(np.sqrt(np.mean(x ** 2)) + 1e-12)


for name, level in LEVELS.items():
    with wave.open(os.path.join(ORIGINALS, name)) as w:
        params = w.getparams()
        data = np.frombuffer(w.readframes(w.getnframes()), np.int16).astype(np.float64) / 32768
    gain = 10 ** ((level - rms_db(data)) / 20)
    out = limit(data * gain)
    touched = np.mean(np.abs(data * gain) > KNEE) * 100
    with wave.open(os.path.join(OUT, name), "wb") as w:
        w.setparams(params)
        w.writeframes((np.clip(out, -1, 1) * 32767).astype(np.int16).tobytes())
    print(f"{name:22s} {rms_db(data):6.1f} -> {rms_db(out):6.1f} dBFS RMS (+{20 * np.log10(gain):.1f} dB), peak {20 * np.log10(np.abs(out).max()):5.1f}, {touched:.2f}% of samples limited")
