// AUDIO FILE MONO FOLD: A WAV WHOSE DATA ENDS MID-FRAME (issue #2868)
//
// A recorder killed mid-write leaves a data chunk that is not a whole number of
// frames. WaveFileReader hands that trailing partial frame back on its last read,
// MonoFoldSampleProvider carries it, and the next request used to be
// count * channels - pending samples: not whole blocks, so NAudio threw "Must read
// complete blocks" and Transcribe File and the no-speech diagnostic both failed on
// the file. These checks write such a WAV and drive the real decoder, the real
// file conversion and the real analyzer over it.

using System.IO;
using System.Text;
using HyperWhisper.Services;

namespace HyperWhisper.SmokeTests;

internal static class MonoFoldMidFrameChecks
{
    private const int SampleRate = 16000;
    private const int Frames = 16000;

    public static void RunAll(Action<string, Action> run)
    {
        run("audio decode: a WAV ending mid-frame folds every whole frame to mono (#2868)", () =>
        {
            foreach (var channels in new[] { 2, 6 })
            {
                var path = WriteMidFrameWav(channels, "decode");
                try
                {
                    using var decoded = AudioFileDecoder.Open(path);
                    var mono = AudioFileDecoder.ToMono(decoded.Samples);
                    var buffer = new float[1000];
                    var produced = new List<float>();
                    int read;
                    while ((read = mono.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        produced.AddRange(buffer.Take(read));
                    }

                    Assert(produced.Count == Frames,
                        $"{channels} ch: expected {Frames} whole frames, got {produced.Count}");
                    var expected = ExpectedMean(channels);
                    for (var i = 0; i < produced.Count; i++)
                    {
                        Assert(Math.Abs(produced[i] - expected) < 0.001,
                            $"{channels} ch: frame {i} should be {expected}, got {produced[i]} - channel-rotated or misread");
                    }
                }
                finally
                {
                    TryDelete(path);
                }
            }
        });

        run("audio decode: Transcribe File converts and the analyzer reads a WAV ending mid-frame (#2868)", () =>
        {
            foreach (var channels in new[] { 2, 6 })
            {
                var path = WriteMidFrameWav(channels, "convert");
                string? converted = null;
                try
                {
                    var result = FileTranscriptionService.ConvertToWhisperFormatAsync(path).GetAwaiter().GetResult();
                    Assert(result.IsSuccess, $"{channels} ch: conversion failed: {result.Error}");
                    converted = result.Value!;
                    using (var reader = new NAudio.Wave.WaveFileReader(converted))
                    {
                        Assert(reader.WaveFormat.Channels == 1 && reader.WaveFormat.SampleRate == SampleRate,
                            $"{channels} ch: converted file is {reader.WaveFormat.SampleRate} Hz {reader.WaveFormat.Channels}ch");
                        var frames = reader.SampleCount;
                        Assert(frames == Frames, $"{channels} ch: converted file holds {frames} frames, expected {Frames}");
                    }

                    var analysis = TranscriptionAudioAnalyzer.Analyze(path, null);
                    Assert(analysis.AnalysisSucceeded,
                        $"{channels} ch: the no-speech diagnostic could not read the file: {analysis.AnalysisError}");
                    Assert(Math.Abs(analysis.DurationSeconds - 1.0) < 0.01,
                        $"{channels} ch: analyzer duration {analysis.DurationSeconds} s, expected 1.0 s");
                }
                finally
                {
                    TryDelete(path);
                    if (converted != null && !string.Equals(converted, path, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(converted);
                    }
                }
            }
        });
    }

    // Channel c carries a constant (c + 1) / 10, so the average is (channels + 1) / 20 and a
    // fold that rotated the channels by one sample would read a different value.
    private static float ChannelValue(int channel) => (channel + 1) / 10f;

    private static double ExpectedMean(int channels) =>
        Enumerable.Range(0, channels).Average(c => (double)(short)(ChannelValue(c) * short.MaxValue) / 32768.0);

    /// <summary>
    /// A 16 kHz 16-bit PCM WAV of <see cref="Frames"/> frames plus ONE extra sample, with the
    /// RIFF and data sizes counting it. Two channels use the plain PCM tag; more use
    /// WAVE_FORMAT_EXTENSIBLE, as ffmpeg writes them.
    /// </summary>
    private static string WriteMidFrameWav(int channels, string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"HyperWhisper.SmokeTests.MidFrame.{label}.{channels}ch.{Guid.NewGuid():N}.wav");
        var extensible = channels > 2;
        var blockAlign = (short)(channels * 2);
        var dataBytes = (Frames * blockAlign) + 2;
        var fmtBytes = extensible ? 40 : 16;

        using var stream = new FileStream(path, FileMode.CreateNew);
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(4 + (8 + fmtBytes) + (8 + dataBytes) + (dataBytes % 2));
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(fmtBytes);
        writer.Write((short)(extensible ? 0xFFFE : 1));
        writer.Write((short)channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write((short)16);
        if (extensible)
        {
            writer.Write((short)22);
            writer.Write((short)16);
            writer.Write(channels == 6 ? 0x3F : 0);
            writer.Write(AudioFileDecoder.PcmSubFormat.ToByteArray());
        }

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        for (var frame = 0; frame < Frames; frame++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                writer.Write((short)(ChannelValue(channel) * short.MaxValue));
            }
        }

        // Half of one more frame's first sample: the data chunk ends mid-frame.
        writer.Write((short)0x2000);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file must not fail the suite.
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
