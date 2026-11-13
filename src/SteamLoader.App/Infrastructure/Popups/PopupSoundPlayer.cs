using System.Collections.Concurrent;
using System.Media;

namespace SteamLoader.App.Infrastructure.Popups;

/// <summary>
/// Theme sounds for TFS Popups. Every sound is synthesized here (short tones
/// with soft envelopes), so TFS ships no audio files. Volume 0..200 % scales
/// the samples; above 100 % a soft limiter keeps peaks from clipping hard.
/// </summary>
internal sealed class PopupSoundPlayer
{
    private const int SampleRate = 44100;

    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly object _playGate = new();
    private SoundPlayer? _current;
    private MemoryStream? _currentStream;

    private enum Wave
    {
        Sine,
        Triangle,
        Bell,
        Soft,
    }

    private readonly record struct Note(double Frequency, double Start, double Duration, Wave Wave, double Gain);

    public void Play(string theme, string category, int volumePercent)
    {
        if (volumePercent <= 0)
        {
            return;
        }

        var key = $"{theme}|{category}|{volumePercent}";
        var wave = _cache.GetOrAdd(key, _ => Render(Notes(theme, category), volumePercent / 100d));
        lock (_playGate)
        {
            try
            {
                _current?.Stop();
                _current?.Dispose();
                _currentStream?.Dispose();
                _currentStream = new MemoryStream(wave, writable: false);
                _current = new SoundPlayer(_currentStream);
                _current.Play();
            }
            catch
            {
                // No audio device: popups stay silent.
            }
        }
    }

    /// <summary>Notes per theme; categories shift the pitch so they are told apart.</summary>
    private static Note[] Notes(string theme, string category)
    {
        var shift = category switch
        {
            PopupCategories.Achievements => 1.0,
            PopupCategories.Messages => 1.122,
            PopupCategories.Friends => 0.891,
            PopupCategories.Downloads => 0.944,
            PopupCategories.System => 0.794,
            PopupCategories.Volume => 1.335,
            _ => 1.0,
        };

        Note[] notes = theme switch
        {
            PopupThemeIds.Pill =>
            [
                new(784, 0, 0.11, Wave.Soft, 0.55),
                new(1175, 0.05, 0.16, Wave.Sine, 0.32),
            ],
            PopupThemeIds.Tile =>
            [
                new(523, 0, 0.09, Wave.Triangle, 0.42),
                new(659, 0.07, 0.09, Wave.Triangle, 0.42),
                new(784, 0.14, 0.18, Wave.Triangle, 0.42),
            ],
            PopupThemeIds.Glow =>
            [
                new(880, 0, 0.9, Wave.Bell, 0.42),
                new(1320, 0.0, 0.6, Wave.Bell, 0.16),
            ],
            PopupThemeIds.Light =>
            [
                new(1047, 0, 0.16, Wave.Triangle, 0.38),
                new(1568, 0.09, 0.24, Wave.Triangle, 0.30),
            ],
            PopupThemeIds.Minimal =>
            [
                new(1760, 0, 0.045, Wave.Sine, 0.32),
            ],
            _ =>
            [
                new(659, 0, 0.16, Wave.Sine, 0.45),
                new(880, 0.1, 0.32, Wave.Bell, 0.40),
            ],
        };

        if (category == PopupCategories.Achievements && theme != PopupThemeIds.Minimal)
        {
            // Achievements get one extra rising note on top.
            var last = notes[^1];
            notes = [.. notes, last with { Frequency = last.Frequency * 1.5, Start = last.Start + 0.12, Gain = last.Gain * 0.7 }];
        }

        return notes.Select(note => note with { Frequency = note.Frequency * shift }).ToArray();
    }

    private static byte[] Render(IReadOnlyList<Note> notes, double volume)
    {
        var totalSeconds = notes.Max(note => note.Start + note.Duration) + 0.05;
        var sampleCount = (int)(totalSeconds * SampleRate);
        var mix = new double[sampleCount];
        foreach (var note in notes)
        {
            var first = (int)(note.Start * SampleRate);
            var length = (int)(note.Duration * SampleRate);
            for (var i = 0; i < length && first + i < sampleCount; i++)
            {
                var t = i / (double)SampleRate;
                var progress = i / (double)length;
                var attack = Math.Min(1, t / 0.006);
                var envelope = note.Wave == Wave.Bell
                    ? attack * Math.Exp(-5.5 * progress)
                    : attack * Math.Pow(1 - progress, 1.8);
                var phase = 2 * Math.PI * note.Frequency * t;
                var sample = note.Wave switch
                {
                    Wave.Triangle => 2 / Math.PI * Math.Asin(Math.Sin(phase)),
                    Wave.Bell => Math.Sin(phase) + 0.35 * Math.Sin(phase * 2.76) * Math.Exp(-9 * progress),
                    Wave.Soft => Math.Sin(phase) * 0.8 + 0.2 * Math.Sin(phase * 2),
                    _ => Math.Sin(phase),
                };
                mix[first + i] += sample * envelope * note.Gain;
            }
        }

        using var stream = new MemoryStream(44 + sampleCount * 2);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + sampleCount * 2);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(sampleCount * 2);
        foreach (var value in mix)
        {
            var scaled = value * volume * 0.7;
            // Soft limiter: linear up to ±0.6, then bends towards ±1.
            var limited = Math.Abs(scaled) <= 0.6
                ? scaled
                : Math.Sign(scaled) * (0.6 + 0.4 * Math.Tanh((Math.Abs(scaled) - 0.6) / 0.4));
            writer.Write((short)Math.Round(limited * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
