using LLama;
using LLama.Exceptions;
using LLama.Transformers;

namespace HyperWhisper.LocalPostProcessing;

/// <summary>
/// Turns the post-processing system prompt and user message into the exact
/// prompt text a local GGUF model was trained on (issue #2559).
/// </summary>
/// <remarks>
/// LLamaSharp's <c>ChatSession</c> defaults to a generic
/// <c>System: … User: … Assistant:</c> transform, which no shipped model was
/// trained on: Gemma 4 ran off-template, and only stopped because it happened to
/// print a <c>User:</c> anti-prompt. The model's own chat template is used
/// instead. Gemma 4's template (<c>&lt;|turn&gt;</c> / <c>&lt;turn|&gt;</c>) is
/// newer than the llama.cpp bundled with LLamaSharp 0.27, whose
/// <c>llama_chat_apply_template</c> throws <see cref="MissingTemplateException"/>
/// for it, so that format is written here by hand, matching the GGUF's Jinja
/// template for one system turn and one user turn.
/// The Windows head keeps the same formatter in
/// <c>app/windows/HyperWhisper/Services/LocalLlmPromptFormatter.cs</c>; change both.
/// </remarks>
public static class LocalLlmPromptFormatter
{
    public const string ChatTemplateMetadataKey = "tokenizer.chat_template";

    /// <summary>Markers that only Gemma 4's chat template carries.</summary>
    private const string Gemma4TurnOpen = "<|turn>";
    private const string Gemma4TurnClose = "<turn|>";

    /// <summary>
    /// Stop strings for the Gemma 4 prompt: only its own turn markers, so text a
    /// user dictates (an HTML <c>&lt;/s&gt;</c>, say) can never end the answer.
    /// <c>&lt;turn|&gt;</c> must stay FIRST: on an end-of-generation token
    /// LLamaSharp's InteractiveExecutor feeds the first stop string back in, and
    /// that token is what then ends the stream.
    /// </summary>
    public static readonly IReadOnlyList<string> Gemma4AntiPrompts = [Gemma4TurnClose, Gemma4TurnOpen];

    /// <summary>
    /// Stop strings for a prompt built from another model's template. The model's
    /// end-of-generation token already stops the executor; these catch a model
    /// that prints an end or turn marker as text instead.
    /// </summary>
    public static readonly IReadOnlyList<string> TemplateAntiPrompts =
    [
        "<end_of_turn>",
        "<|im_end|>",
        "<|end|>",
        "<|eot_id|>",
        "</s>"
    ];

    /// <summary>
    /// The plain role prompt has no end token, so a new <c>User:</c> line is
    /// the only sign the model finished its turn.
    /// </summary>
    public static readonly IReadOnlyList<string> PlainAntiPrompts = [.. TemplateAntiPrompts, "\nUser:"];

    public static LocalLlmPrompt Format(LLamaWeights weights, string systemPrompt, string userMessage)
    {
        weights.Metadata.TryGetValue(ChatTemplateMetadataKey, out var chatTemplate);
        if (IsGemma4Template(chatTemplate))
        {
            return new LocalLlmPrompt(FormatGemma4(systemPrompt, userMessage), Gemma4AntiPrompts);
        }

        try
        {
            var template = new LLamaTemplate(weights) { AddAssistant = true };
            template.Add("system", systemPrompt);
            template.Add("user", userMessage);
            return new LocalLlmPrompt(PromptTemplateTransformer.ToModelPrompt(template), TemplateAntiPrompts);
        }
        catch (MissingTemplateException)
        {
            return new LocalLlmPrompt(FormatPlain(systemPrompt, userMessage), PlainAntiPrompts);
        }
    }

    public static bool IsGemma4Template(string? chatTemplate) =>
        chatTemplate != null
        && chatTemplate.Contains(Gemma4TurnOpen, StringComparison.Ordinal)
        && chatTemplate.Contains(Gemma4TurnClose, StringComparison.Ordinal);

    /// <summary>
    /// Gemma 4's template for a system turn, a user turn and the generation
    /// prompt, with thinking off. The leading <c>&lt;bos&gt;</c> is left out:
    /// the executor's tokenizer adds it.
    /// </summary>
    public static string FormatGemma4(string systemPrompt, string userMessage) =>
        $"{Gemma4TurnOpen}system\n{systemPrompt.Trim()}{Gemma4TurnClose}\n" +
        $"{Gemma4TurnOpen}user\n{userMessage.Trim()}{Gemma4TurnClose}\n" +
        $"{Gemma4TurnOpen}model\n";

    /// <summary>The format LLamaSharp's default history transform produced.</summary>
    public static string FormatPlain(string systemPrompt, string userMessage) =>
        $"System: {systemPrompt}\nUser: {userMessage}\nAssistant: ";
}

/// <summary>The prompt text to feed the executor, and the strings that end the model's turn.</summary>
public readonly record struct LocalLlmPrompt(string Text, IReadOnlyList<string> AntiPrompts);
