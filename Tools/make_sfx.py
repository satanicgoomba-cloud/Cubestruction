#!/usr/bin/env python3
"""Synthesises the game's placeholder sound effects (bubbly Aero 'boom', 'hit', 'zap') as 16-bit WAVs."""
import math, os, random, struct, wave
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Assets", "Cubestruction", "Audio")
SR = 44100
random.seed(3)

def write(name, samples):
    os.makedirs(OUT, exist_ok=True)
    peak = max(1e-6, max(abs(s) for s in samples))
    with wave.open(os.path.join(OUT, name), "w") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(SR)
        w.writeframes(b"".join(struct.pack("<h", int(32000 * s / peak)) for s in samples))

def boom():
    n = int(SR * 1.1); out = []; lp = 0.0
    for i in range(n):
        t = i / SR
        noise = random.uniform(-1, 1)
        lp += (noise - lp) * (0.08 * math.exp(-t * 3) + 0.01)          # darkening noise
        thump = math.sin(2 * math.pi * (70 * math.exp(-t * 4)) * t) * math.exp(-t * 5)
        pop = math.sin(2 * math.pi * 520 * t) * math.exp(-t * 40) * 0.5     # bubbly 'pop' transient
        out.append((lp * 3 + thump * 1.2 + pop) * math.exp(-t * 2.5))
    return out

def hit():
    n = int(SR * 0.25)
    return [(math.sin(2 * math.pi * 330 * t) * 0.6 + math.sin(2 * math.pi * 990 * t) * 0.3 + random.uniform(-1, 1) * 0.5 * math.exp(-t * 60))
            * math.exp(-t * 22) for t in (i / SR for i in range(n))]

def zap():
    n = int(SR * 0.35)
    return [math.sin(2 * math.pi * (1800 * math.exp(-t * 9) + 200) * t) * math.exp(-t * 9) for t in (i / SR for i in range(n))]

write("boom.wav", boom()); write("hit.wav", hit()); write("zap.wav", zap())
print("wrote", os.listdir(OUT))
