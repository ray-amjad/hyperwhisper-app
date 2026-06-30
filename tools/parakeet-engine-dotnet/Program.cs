using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
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

    // Keep in sync with is_no_space_language() in tools/parakeet-engine/main.cpp.
    public static bool IsNoSpaceLanguage(string code) => code is "ja" or "zh" or "ko" or "yue";

    public static float Rms(float[] samples)
    {
        if (samples.Length == 0) return 0;
        double sum = 0;
        foreach (var sample in samples) sum += sample * sample;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    public static AudioData ReadAudio(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found", path);

        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        if (reader.WaveFormat.Channels > 1)
        {
            provider = new StereoToMonoSampleProvider(provider)
            {
                LeftVolume = 0.5f,
                RightVolume = 0.5f
            };
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
}

internal sealed record AudioData(float[] Samples, int SampleRate);

internal sealed class DaemonOptions
{
    public required string ModelDirectory { get; init; }
    public string Language { get; init; } = "en";
    public string? VadModelPath { get; init; }
    public string Engine { get; init; } = "nemo_transducer";

    public static DaemonOptions? Parse(string[] args)
    {
        string? model = null;
        string language = "en";
        string? vad = null;
        string engine = "nemo_transducer";

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
            }
        }

        if (string.IsNullOrWhiteSpace(model)) return null;
        if (engine is not ("nemo_transducer" or "qwen3" or "nemotron_ml")) return null;

        return new DaemonOptions
        {
            ModelDirectory = model,
            Language = string.IsNullOrWhiteSpace(language) ? "auto" : language,
            VadModelPath = vad,
            Engine = engine
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
        _segmentJoin = Program.IsNoSpaceLanguage(options.Language) ? "" : " ";
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

        return string.Join(_segmentJoin, parts);
    }

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
}

internal sealed record ReadyResponse(string Status, string Provider);
internal sealed record ReadyError(string Error)
{
    public string Status => "error";
}
internal sealed record TranscribeResponse(string Text, long DurationMs);
internal sealed record ErrorResponse(string Error);
