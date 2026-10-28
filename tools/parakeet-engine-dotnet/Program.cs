using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using HyperWhisper.SharedCore;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;

internal static class Program
{
    public const int TargetSampleRate = 16000;

    public static int Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var options = DaemonOptions.Parse(args);
        if (options == null)
        {
            WriteJson(new ReadyError("Invalid arguments"));
            return 2;
        }

        try
        {
            using var engine = EngineSession.Create(options);
            WriteJson(new ReadyResponse("ready", engine.Provider));
            LogInfo("Daemon is ready");

            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                Request? request;
                try
                {
                    line = line.TrimStart('\uFEFF');
                    request = JsonSerializer.Deserialize<Request>(line, JsonDefaults.Options);
                }
                catch (Exception ex)
                {
                    LogWarn($"Invalid JSON request: {ex.Message}");
                    WriteJson(new ErrorResponse("Invalid JSON request"));
                    continue;
                }

                if (request?.Command == "quit")
                {
                    LogInfo("Received quit command");
                    break;
                }

                if (request?.Command == "start")
                {
                    try
                    {
                        engine.StartLive();
                        WriteJson(new LiveResponse("started"));
                    }
                    catch (Exception ex)
                    {
                        LogError($"Live session start failed: {ex.Message}");
                        WriteJson(new ErrorResponse("Live session could not be started"));
                    }
                    continue;
                }

                if (request?.Command == "audio")
                {
                    try
                    {
                        var pcm = DecodePcm16(request.Audio);
                        var update = engine.AcceptLiveAudio(pcm);
                        WriteJson(new LiveResponse("transcript", update.Preview, update.Committed));
                    }
                    catch (Exception ex)
                    {
                        LogError($"Live audio failed: {ex.Message}");
                        WriteJson(new ErrorResponse("Live audio could not be processed"));
                    }
                    continue;
                }

                if (request?.Command == "finish")
                {
                    try
                    {
                        var update = engine.FinishLive();
                        WriteJson(new LiveResponse("transcript", update.Preview, update.Committed, true));
                    }
                    catch (Exception ex)
                    {
                        LogError($"Live session finish failed: {ex.Message}");
                        WriteJson(new ErrorResponse("Live session could not be finished"));
                    }
                    continue;
                }

                if (request?.Command == "cancel")
                {
                    engine.CancelLive();
                    WriteJson(new LiveResponse("cancelled"));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(request?.AudioPath))
                {
                    WriteJson(new ErrorResponse("Missing audio_path"));
                    continue;
                }

                try
                {
                    var started = Stopwatch.StartNew();
                    var text = engine.Transcribe(request.AudioPath);
                    started.Stop();
                    WriteJson(new TranscribeResponse(text, started.ElapsedMilliseconds));
                }
                catch (Exception ex)
                {
                    LogError($"Transcription failed: {ex.Message}");
                    WriteJson(new ErrorResponse(ex.Message));
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            LogError($"Failed to initialize daemon: {ex.Message}");
            WriteJson(new ReadyError("Failed to load model"));
            return 1;
        }
    }

    public static void WriteJson<T>(T value)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonDefaults.Options));
        Console.Out.Flush();
    }

    public static void LogInfo(string message) => Console.Error.WriteLine($"[INFO] {message}");
    public static void LogWarn(string message) => Console.Error.WriteLine($"[WARN] {message}");
    public static void LogError(string message) => Console.Error.WriteLine($"[ERROR] {message}");

    public static string PickOnnx(string directory, string baseName)
    {
        var int8 = Path.Combine(directory, $"{baseName}.int8.onnx");
        return File.Exists(int8) ? int8 : Path.Combine(directory, $"{baseName}.onnx");
    }

    public static string Qwen3LanguageName(string code)
    {
        return code switch
        {
            "ja" => "Japanese",
            "en" => "English",
            "zh" => "Chinese",
            "ko" => "Korean",
            "yue" => "Cantonese",
            "es" => "Spanish",
            "fr" => "French",
            "de" => "German",
            "it" => "Italian",
            "pt" => "Portuguese",
            "ru" => "Russian",
            "ar" => "Arabic",
            _ => ""
        };
    }

    /// <summary>
    /// Whether the daemon joins transcription segments for <paramref name="code"/>
    /// without spaces.
    /// <para>
    /// This used to be a private <c>ja|zh|ko|yue</c> literal. It now reads the
    /// shared join policy out of <c>hw-text</c> (issue #286), which is the same
    /// table the Linux live-delivery path and <c>append_trailing_space</c> use.
    /// The daemon keeps <c>yue</c> — it was added to the shared table for this —
    /// and gains <c>th</c>, the explicit <c>zh-TW</c> / <c>zh-Hans</c> /
    /// <c>zh-Hant</c> spellings, case-insensitivity and the two-character prefix
    /// fallback (<c>"zh-CN"</c>).
    /// </para>
    /// <para>
    /// <c>tools/parakeet-engine/main.cpp</c> (the Windows x64 C++ daemon) has no
    /// .NET runtime to reach the core through, so the Windows host decides the
    /// join from the core and passes it to both daemons as <c>--segment-join</c>
    /// (#902), which wins over this lookup (<see cref="DaemonOptions.SegmentJoin"/>).
    /// </para>
    /// </summary>
    public static bool IsNoSpaceLanguage(string code) => SharedCoreBridge.IsNoSpaceLanguage(code);

    public static float Rms(float[] samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var sample in samples) sum += sample * sample;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    private static float[] DecodePcm16(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) throw new InvalidDataException("Missing audio");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length == 0 || bytes.Length > 128 * 1024 || (bytes.Length & 1) != 0)
            throw new InvalidDataException("PCM16 audio chunk is invalid");
        var samples = new float[bytes.Length / 2];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = (short)(bytes[index * 2] | bytes[index * 2 + 1] << 8) / 32768f;
        return samples;
    }

    public static AudioData ReadAudio(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found", path);

        using var reader = OpenAudio(path);
        ISampleProvider provider = reader as ISampleProvider ?? reader.ToSampleProvider();
        if (provider.WaveFormat.Channels > 1)
        {
            provider = new MonoFoldSampleProvider(provider);
        }

        if (provider.WaveFormat.SampleRate != TargetSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);
        }

        var samples = new List<float>();
        var buffer = new float[TargetSampleRate * Math.Max(1, provider.WaveFormat.Channels)];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++) samples.Add(buffer[i]);
        }

        return new AudioData(samples.ToArray(), TargetSampleRate);
    }

    // KSDATAFORMAT_SUBTYPE_PCM and KSDATAFORMAT_SUBTYPE_IEEE_FLOAT (ksmedia.h).
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// Opens <paramref name="path"/> the way <see cref="AudioFileReader"/> does, except that a
    /// WAVE_FORMAT_EXTENSIBLE WAV whose SubFormat is PCM or IEEE float is read directly
    /// (#1541). The same fix as the app's <c>AudioFileDecoder</c> (#1450, PR #1533), which
    /// this separate daemon cannot reference.
    /// </summary>
    /// <remarks>
    /// NAudio 2.2.1's <see cref="AudioFileReader"/> sends every .wav whose tag is not exactly
    /// Pcm or IeeeFloat through ACM, and no ACM driver takes 0xFFFE: the file fails with
    /// "NoDriver calling acmFormatSuggest". ffmpeg writes every WAV with more than two
    /// channels that way. The samples are plain interleaved PCM or float, so relabelling the
    /// stream with the plain tag lets NAudio's own converters read it. Anything else (a
    /// compressed WAV, another SubFormat, another file type) keeps the old path.
    /// </remarks>
    internal static WaveStream OpenAudio(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            WaveFileReader? wave = new WaveFileReader(path);
            try
            {
                var plain = TryGetPlainFormat(wave.WaveFormat);
                if (plain != null)
                {
                    var stream = new RelabelledWaveStream(wave, plain);
                    wave = null;
                    return stream;
                }
            }
            finally
            {
                wave?.Dispose();
            }
        }

        return new AudioFileReader(path);
    }

    /// <summary>
    /// The plain Pcm or IeeeFloat format that describes the same bytes as an Extensible
    /// PCM or float <paramref name="format"/>, or null for any other format.
    /// </summary>
    internal static WaveFormat? TryGetPlainFormat(WaveFormat format)
    {
        if (format.Encoding != WaveFormatEncoding.Extensible) return null;

        Guid? subFormat = format switch
        {
            WaveFormatExtensible extensible => extensible.SubFormat,
            // WaveFileReader parses the fmt chunk into a WaveFormatExtraData; its extra bytes
            // are the WAVEFORMATEXTENSIBLE tail (valid bits 2, channel mask 4, SubFormat 16).
            WaveFormatExtraData extra when format.ExtraSize >= 22 && extra.ExtraData.Length >= 22
                => new Guid(extra.ExtraData.AsSpan(6, 16)),
            _ => null
        };

        WaveFormatEncoding encoding;
        if (subFormat == PcmSubFormat) encoding = WaveFormatEncoding.Pcm;
        else if (subFormat == IeeeFloatSubFormat) encoding = WaveFormatEncoding.IeeeFloat;
        else return null;

        var bits = format.BitsPerSample;
        var supported = encoding == WaveFormatEncoding.Pcm ? bits is 8 or 16 or 24 or 32 : bits is 32 or 64;
        // wBitsPerSample is the container size, so a frame is channels * bits / 8 bytes. A
        // header that disagrees cannot be read sample by sample, so leave it alone.
        var blockAlign = format.Channels * (bits / 8);
        if (!supported || format.Channels < 1 || format.SampleRate < 1 || format.BlockAlign != blockAlign)
            return null;

        return WaveFormat.CreateCustomFormat(
            encoding, format.SampleRate, format.Channels, format.SampleRate * blockAlign, blockAlign, bits);
    }

    /// <summary>The same bytes as the source stream, described by an equivalent format.</summary>
    private sealed class RelabelledWaveStream(WaveStream source, WaveFormat format) : WaveStream
    {
        public override WaveFormat WaveFormat => format;
        public override long Length => source.Length;

        public override long Position
        {
            get => source.Position;
            set => source.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => source.Read(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Folds any channel count to mono by averaging every channel (#1541). NAudio's
/// <see cref="StereoToMonoSampleProvider"/> throws "Source must be stereo" on more than two;
/// for two channels the average is the same 0.5/0.5 mix it gave. The same fold as the app's
/// <c>MonoFoldSampleProvider</c> (#1450).
/// </summary>
internal sealed class MonoFoldSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _sourceBuffer = [];

    // Samples of an incomplete frame held over from the previous Read, at the start of
    // _sourceBuffer. Dropping them would rotate every later frame across the channels.
    private int _pending;

    internal MonoFoldSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count <= 0) return 0;

        var required = count * _channels;
        if (_sourceBuffer.Length < required) Array.Resize(ref _sourceBuffer, required);

        // A 0 reads as end-of-stream downstream, so keep pulling until a whole frame exists
        // or the source is really exhausted.
        var available = _pending;
        while (available < _channels)
        {
            var read = _source.Read(_sourceBuffer, available, required - available);
            if (read <= 0) break;
            available += read;
        }

        var frames = available / _channels;
        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            var start = frame * _channels;
            for (var channel = 0; channel < _channels; channel++) sum += _sourceBuffer[start + channel];
            buffer[offset + frame] = (float)(sum / _channels);
        }

        _pending = available - (frames * _channels);
        if (_pending > 0) Array.Copy(_sourceBuffer, frames * _channels, _sourceBuffer, 0, _pending);
        return frames;
    }
}

internal sealed record AudioData(float[] Samples, int SampleRate);

internal sealed class DaemonOptions
{
    public required string ModelDirectory { get; init; }
    public string Language { get; init; } = "en";
    public string? VadModelPath { get; init; }
    public string Engine { get; init; } = "nemo_transducer";

    /// <summary>
    /// The separator the host decided for the VAD segments (<c>--segment-join
    /// none|space</c> → <c>""</c> / <c>" "</c>), or null when the host passed none
    /// and the daemon resolves the join from <see cref="Language"/> itself (#902).
    /// </summary>
    public string? SegmentJoin { get; init; }

    public static DaemonOptions? Parse(string[] args)
    {
        string? model = null;
        string language = "en";
        string? vad = null;
        string engine = "nemo_transducer";
        string? segmentJoin = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model" when i + 1 < args.Length:
                    model = args[++i];
                    break;
                case "--language" when i + 1 < args.Length:
                    language = args[++i];
                    break;
                case "--vad-model" when i + 1 < args.Length:
                    vad = args[++i];
                    break;
                case "--engine" when i + 1 < args.Length:
                    engine = args[++i];
                    break;
                case "--segment-join" when i + 1 < args.Length:
                    segmentJoin = args[++i] switch
                    {
                        "none" => "",
                        "space" => " ",
                        _ => null,
                    };
                    if (segmentJoin == null) return null;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(model)) return null;
        if (engine is not ("nemo_transducer" or "qwen3" or "nemotron_ml")) return null;

        return new DaemonOptions
        {
            ModelDirectory = model,
            Language = string.IsNullOrWhiteSpace(language) ? "auto" : language,
            VadModelPath = vad,
            Engine = engine,
            SegmentJoin = segmentJoin
        };
    }
}

internal sealed class EngineSession : IDisposable
{
    private const float SilenceRmsFloor = 1e-3f;

    private readonly DaemonOptions _options;
    private readonly OfflineRecognizer? _offlineRecognizer;
    private readonly OnlineRecognizer? _onlineRecognizer;
    private readonly VoiceActivityDetector? _vad;
    private readonly string _segmentJoin;
    private readonly string _qwen3Language;
    private readonly bool _isQwen3;
    private readonly bool _isOnline;
    private LiveEngineSession? _live;

    public string Provider { get; }

    private EngineSession(
        DaemonOptions options,
        OfflineRecognizer? offlineRecognizer,
        OnlineRecognizer? onlineRecognizer,
        VoiceActivityDetector? vad,
        string provider)
    {
        _options = options;
        _offlineRecognizer = offlineRecognizer;
        _onlineRecognizer = onlineRecognizer;
        _vad = vad;
        _isQwen3 = options.Engine == "qwen3";
        _isOnline = options.Engine == "nemotron_ml";
        _qwen3Language = _isQwen3 ? Program.Qwen3LanguageName(options.Language) : "";
        _segmentJoin = options.SegmentJoin ?? (Program.IsNoSpaceLanguage(options.Language) ? "" : " ");
        Provider = provider;
    }

    public static EngineSession Create(DaemonOptions options)
    {
        Program.LogInfo($"Engine: {options.Engine}");
        Program.LogInfo($"Model directory: {options.ModelDirectory}");
        Program.LogInfo($"Language: {options.Language}");

        var vad = CreateVad(options.VadModelPath);
        if (options.Engine == "nemotron_ml")
        {
            ValidateNemotronModel(options.ModelDirectory);
            var online = new OnlineRecognizer(CreateOnlineConfig(options.ModelDirectory));
            return new EngineSession(options, null, online, vad, "cpu");
        }

        if (options.Engine == "qwen3")
        {
            var offline = new OfflineRecognizer(CreateQwen3Config(options.ModelDirectory));
            return new EngineSession(options, offline, null, vad, "cpu");
        }

        var providers = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "cpu" }
            : new[] { "directml", "cpu" };

        OfflineRecognizer? recognizer = null;
        var provider = "";
        foreach (var candidate in providers)
        {
            recognizer = TryCreateParakeet(options.ModelDirectory, candidate, out provider);
            if (recognizer != null) break;
        }

        if (recognizer == null)
        {
            throw new InvalidOperationException("Failed to load model");
        }

        return new EngineSession(options, recognizer, null, vad, provider);
    }

    public string Transcribe(string audioPath)
    {
        var audio = Program.ReadAudio(audioPath);
        Program.LogInfo($"Audio: {audio.Samples.Length} samples at {audio.SampleRate} Hz");

        if (_isOnline)
        {
            return DecodeOnline(audio);
        }

        if (_vad != null)
        {
            return DecodeOfflineWithVad(audio);
        }

        return DecodeOffline(audio.Samples, audio.SampleRate);
    }

    public void StartLive()
    {
        _live?.Dispose();
        _live = _isOnline
            ? LiveEngineSession.CreateOnline(_onlineRecognizer!, _options.Language)
            : LiveEngineSession.CreateRollingOffline(_offlineRecognizer!, _segmentJoin);
    }

    public LiveEngineUpdate AcceptLiveAudio(float[] samples) =>
        (_live ?? throw new InvalidOperationException("No live session is active")).Accept(samples);

    public LiveEngineUpdate FinishLive()
    {
        var live = _live ?? throw new InvalidOperationException("No live session is active");
        try { return live.Finish(); }
        finally { live.Dispose(); _live = null; }
    }

    public void CancelLive()
    {
        _live?.Dispose();
        _live = null;
    }

    private string DecodeOfflineWithVad(AudioData audio)
    {
        _vad!.Reset();
        const int vadWindowSize = 512;
        for (var offset = 0; offset + vadWindowSize <= audio.Samples.Length; offset += vadWindowSize)
        {
            var window = new float[vadWindowSize];
            Array.Copy(audio.Samples, offset, window, 0, vadWindowSize);
            _vad.AcceptWaveform(window);
        }

        var remainder = audio.Samples.Length % vadWindowSize;
        if (remainder > 0)
        {
            var window = new float[remainder];
            Array.Copy(audio.Samples, audio.Samples.Length - remainder, window, 0, remainder);
            _vad.AcceptWaveform(window);
        }
        _vad.Flush();

        var parts = new List<string>();
        while (!_vad.IsEmpty())
        {
            var segment = _vad.Front();
            if (segment.Samples.Length > 0 && (!_isQwen3 || Program.Rms(segment.Samples) >= SilenceRmsFloor))
            {
                var text = DecodeOffline(segment.Samples, audio.SampleRate);
                if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
            }
            _vad.Pop();
        }

        if (parts.Count == 0)
        {
            if (_isQwen3)
            {
                return "";
            }

            Program.LogWarn("VAD produced no transcribed segments; falling back to whole-file offline decode");
            return DecodeOffline(audio.Samples, audio.SampleRate);
        }

        return JoinSegments(parts);
    }

    /// <summary>
    /// Concatenate VAD segments under the shared join policy.
    /// <para>
    /// Resolved per boundary rather than from <see cref="_segmentJoin"/>, because
    /// the language the hosts pass is usually <c>"auto"</c> — that is what
    /// <c>ParakeetDaemonLiveTranscriber.NormalizeLanguage</c> sends for a mode
    /// with no language set, and <c>is_no_space_language("auto")</c> is false by
    /// design. Joining on the language alone therefore wedged spaces into every
    /// auto-language Japanese dictation, which is exactly the #286 defect.
    /// <see cref="SharedCoreBridge.SegmentSeparator"/> falls back to detecting a
    /// continuous script in the text on either side of the boundary, as
    /// <c>append_trailing_space</c> already does for the text it is given.
    /// </para>
    /// <para>
    /// The loop itself is <see cref="SharedCoreBridge.JoinSegments"/>, so the
    /// daemon and every host build the same string from the same code rather than
    /// from two copies of the same loop.
    /// </para>
    /// <para>
    /// <see cref="_segmentJoin"/> also feeds the rolling-offline live path's
    /// <c>BoundedWordAgreement</c>, which is now a thin adapter over the core's
    /// <c>HwBoundedAgreementSession</c> — so the join policy and the streaming
    /// agreement engine both come from <c>hw_text</c>, and the same string is
    /// the daemon's only remaining input to either.
    /// </para>
    /// <para>
    /// A separator the host decided (<see cref="DaemonOptions.SegmentJoin"/>)
    /// wins over the language (#902).
    /// </para>
    /// </summary>
    private string JoinSegments(List<string> parts) => JoinSegments(_options.Language, _options.SegmentJoin, parts);

    /// <summary>Test seam for <see cref="JoinSegments(List{string})"/>.</summary>
    internal static string JoinSegments(string? language, IEnumerable<string> parts)
        => SharedCoreBridge.JoinSegments(language, parts);

    /// <summary>Test seam for <see cref="JoinSegments(List{string})"/> with a host-decided separator.</summary>
    internal static string JoinSegments(string? language, string? segmentJoin, IEnumerable<string> parts)
        => segmentJoin == null
            ? SharedCoreBridge.JoinSegments(language, parts)
            : string.Join(segmentJoin, parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    private string DecodeOffline(float[] samples, int sampleRate)
    {
        using var stream = _offlineRecognizer!.CreateStream();
        if (_isQwen3 && !string.IsNullOrEmpty(_qwen3Language))
        {
            stream.SetOption("language", _qwen3Language);
        }

        stream.AcceptWaveform(sampleRate, samples);
        _offlineRecognizer.Decode(stream);
        return stream.Result.Text ?? "";
    }

    private string DecodeOnline(AudioData audio)
    {
        using var stream = _onlineRecognizer!.CreateStream();
        if (!string.IsNullOrWhiteSpace(_options.Language))
        {
            stream.SetOption("language", _options.Language);
        }

        const int chunkSize = Program.TargetSampleRate / 10;
        for (var offset = 0; offset < audio.Samples.Length; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, audio.Samples.Length - offset);
            var chunk = new float[count];
            Array.Copy(audio.Samples, offset, chunk, 0, count);
            stream.AcceptWaveform(audio.SampleRate, chunk);
            while (_onlineRecognizer.IsReady(stream))
            {
                _onlineRecognizer.Decode(stream);
            }
        }

        stream.AcceptWaveform(audio.SampleRate, new float[audio.SampleRate / 2]);
        stream.InputFinished();
        while (_onlineRecognizer.IsReady(stream))
        {
            _onlineRecognizer.Decode(stream);
        }

        return _onlineRecognizer.GetResult(stream).Text ?? "";
    }

    private static OfflineRecognizer? TryCreateParakeet(string modelDir, string provider, out string activeProvider)
    {
        activeProvider = provider;
        try
        {
            return new OfflineRecognizer(CreateParakeetConfig(modelDir, provider));
        }
        catch (Exception ex)
        {
            Program.LogWarn($"Parakeet provider {provider} failed: {ex.Message}");
            return null;
        }
    }

    private static OfflineRecognizerConfig CreateParakeetConfig(string modelDir, string provider)
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Transducer.Encoder = Path.Combine(modelDir, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelDir, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelDir, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Provider = provider;
        config.ModelConfig.ModelType = "nemo_transducer";
        config.DecodingMethod = "greedy_search";
        return config;
    }

    private static OfflineRecognizerConfig CreateQwen3Config(string modelDir)
    {
        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Qwen3Asr.ConvFrontend = Program.PickOnnx(modelDir, "conv_frontend");
        config.ModelConfig.Qwen3Asr.Encoder = Program.PickOnnx(modelDir, "encoder");
        config.ModelConfig.Qwen3Asr.Decoder = Program.PickOnnx(modelDir, "decoder");
        config.ModelConfig.Qwen3Asr.Tokenizer = Path.Combine(modelDir, "tokenizer");
        config.ModelConfig.Qwen3Asr.MaxTotalLen = 512;
        config.ModelConfig.Qwen3Asr.MaxNewTokens = 256;
        config.ModelConfig.Qwen3Asr.Temperature = 1e-6f;
        config.ModelConfig.Qwen3Asr.TopP = 0.8f;
        config.ModelConfig.Qwen3Asr.Seed = 42;
        config.ModelConfig.Qwen3Asr.Hotwords = "";
        config.ModelConfig.Tokens = "";
        config.ModelConfig.NumThreads = 4;
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.ModelType = "";
        config.DecodingMethod = "greedy_search";
        return config;
    }

    private static OnlineRecognizerConfig CreateOnlineConfig(string modelDir)
    {
        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = Program.TargetSampleRate;
        config.FeatConfig.FeatureDim = 128;
        config.ModelConfig.Transducer.Encoder = Path.Combine(modelDir, "encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = Path.Combine(modelDir, "decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = Path.Combine(modelDir, "joiner.int8.onnx");
        config.ModelConfig.Tokens = Path.Combine(modelDir, "tokens.txt");
        config.ModelConfig.NumThreads = 2;
        config.ModelConfig.Debug = 0;
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.ModelType = "";
        config.DecodingMethod = "greedy_search";
        config.MaxActivePaths = 4;
        config.EnableEndpoint = 0;
        return config;
    }

    private static VoiceActivityDetector? CreateVad(string? vadModelPath)
    {
        if (string.IsNullOrWhiteSpace(vadModelPath) || !File.Exists(vadModelPath)) return null;

        try
        {
            var config = new VadModelConfig();
            config.SileroVad.Model = vadModelPath;
            config.SileroVad.Threshold = 0.5f;
            config.SileroVad.MinSilenceDuration = 0.5f;
            config.SileroVad.MinSpeechDuration = 0.25f;
            config.SileroVad.WindowSize = 512;
            config.SampleRate = Program.TargetSampleRate;
            config.NumThreads = 1;
            config.Provider = "cpu";
            config.Debug = 0;
            return new VoiceActivityDetector(config, 30.0f);
        }
        catch (Exception ex)
        {
            Program.LogWarn($"Failed to load VAD model: {ex.Message}");
            return null;
        }
    }

    private static void ValidateNemotronModel(string modelDir)
    {
        var tokensPath = Path.Combine(modelDir, "tokens.txt");
        if (!File.Exists(tokensPath)) throw new FileNotFoundException("tokens.txt not found", tokensPath);
        var tokenLines = File.ReadLines(tokensPath).Count();
        if (tokenLines < 8000)
        {
            throw new InvalidOperationException($"tokens.txt has {tokenLines} lines; expected multilingual Nemotron vocab");
        }

        var decoder = Program.PickOnnx(modelDir, "decoder");
        if (!File.Exists(decoder)) throw new FileNotFoundException("decoder ONNX not found", decoder);
        var bytes = File.ReadAllBytes(decoder);
        var marker = "nemo_parakeet_unified_streaming"u8.ToArray();
        if (bytes.AsSpan().IndexOf(marker) >= 0)
        {
            throw new InvalidOperationException("decoder is unified streaming variant and ignores language prompt");
        }
    }

    public void Dispose()
    {
        _live?.Dispose();
        _offlineRecognizer?.Dispose();
        _onlineRecognizer?.Dispose();
        _vad?.Dispose();
    }
}

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed class Request
{
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("audio_path")]
    public string? AudioPath { get; set; }

    [JsonPropertyName("audio")]
    public string? Audio { get; set; }
}

internal sealed record ReadyResponse(string Status, string Provider);
internal sealed record ReadyError(string Error)
{
    public string Status => "error";
}
internal sealed record TranscribeResponse(string Text, long DurationMs);
internal sealed record LiveResponse(string Type, string? Text = null, string? Committed = null, bool IsFinal = false);
internal sealed record ErrorResponse(string Error);
