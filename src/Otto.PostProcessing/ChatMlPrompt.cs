using System.Text;

namespace Otto.PostProcessing;

/// <summary>
/// Formats a conversation as ChatML — <c>&lt;|im_start|&gt;role\ncontent&lt;|im_end|&gt;</c>
/// per turn — and defensively trims a raw completion back down to just the
/// assistant's answer. Pure and LLamaSharp-free on purpose, unlike everything else
/// in this file's neighbourhood: <see cref="LlamaEngine"/> is the only class in this
/// project allowed to touch LLamaSharp types, so this is the one piece of the
/// correction pipeline testable without a GGUF or a GPU.
///
/// Qwen2.5-Instruct — the only model Otto ships — was fine-tuned on exactly this
/// literal shape, and nothing else. <see cref="LlamaEngine"/> used to hand it a
/// plain-text transcript instead: LLamaSharp's <c>ChatSession</c> defaults to
/// <c>LLamaTransforms.DefaultHistoryTransform</c>, which renders history as
/// "[Author]: [Message]" lines — a format the model never saw in training. Instead
/// of answering, it imitated the only pattern it *was* shown and kept the transcript
/// going, literally generating "Assistant: {answer}\nUser: " as output text. Measured
/// on the real corpus: 11 of 12 clips rejected by <see cref="EditGuard"/>, because
/// that leaked scaffolding reads as an edit far outside what a voseo fix should touch
/// — net-zero benefit over doing nothing. <c>ChatSession</c>/<c>InteractiveExecutor</c>
/// carried a second, independent bug on top of the wrong template: the executor is a
/// long-lived field reused across every dictation, and LLamaSharp's own interactive
/// executor stops treating a message as a fresh prompt after its first call ever —
/// every correction after Otto's first was silently appended to one ever-growing
/// conversation instead of standing on its own, which both leaks unrelated dictations
/// into each other's context and, left running long enough, was going to overflow
/// <see cref="PostProcessingOptions.ContextSize"/>. Formatting ChatML explicitly here
/// and running it through LLamaSharp's stateless executor — a fresh prompt every call,
/// by construction — fixes both at once.
///
/// <see cref="Build"/> also neutralizes the literal control substrings inside message
/// CONTENT (never inside the fixed <c>role</c> strings, which this project always
/// supplies itself). This matters because the last user turn — the text to correct —
/// is dictated speech: the one piece of every prompt this class builds that is fully
/// attacker/user-controlled. <see cref="LlamaEngine.ChatAsync"/> tokenizes the whole
/// composed prompt with special-token parsing on, which is what lets a legitimate
/// <c>&lt;|im_start|&gt;</c>/<c>&lt;|im_end|&gt;</c> turn marker work at all — but with
/// no escaping, the exact same substrings sitting inside dictated content would be read
/// by the tokenizer as genuine role-boundary tokens too, letting spoken text forge a
/// fake system or assistant turn from inside what is supposed to be inert content. A
/// dictation that legitimately contains those characters still comes through as
/// readable text after neutralizing — it is broken up, never dropped.
/// </summary>
public static class ChatMlPrompt
{
    // Both are recognized as literal special tokens by LLamaSharp's tokenizer
    // (LlamaEngine.ChatAsync tokenizes with special-token parsing on), not as
    // plain text the model has to spell out — so the model reads them as the
    // exact role-boundary tokens it was instruction-tuned on, the same tokens
    // llama.cpp's own end-of-generation check for <|im_end|> stops on before a
    // single character of it is ever decoded to text.
    private const string TurnStart = "<|im_start|>";
    private const string TurnEnd = "<|im_end|>";

    // A zero-width space breaks the exact contiguous substring the tokenizer's
    // special-token scanner matches on, without changing how the text reads to a
    // human — it renders invisibly wherever the corrected text ends up (a note, a
    // pasted document, a terminal). This is the whole fix for the injection this
    // class's own doc comment describes: without it, dictating the literal
    // characters "<|im_end|>" would close the user's turn early and open a forged
    // one, tokenized exactly like a real <|im_start|>system turn would be.
    private const char InertBreak = '\u200B';

    /// <summary>
    /// Builds a full ChatML prompt from a conversation, ending with an open
    /// "assistant" turn so the model completes it. Every message — system
    /// prompt, few-shot examples, the text to correct — goes through this exact
    /// same shape; nothing is special-cased, including this neutralization: it
    /// runs over every message's content, not just the final (dictated) turn,
    /// so a future caller can never reintroduce this bug by forgetting which
    /// turn was "the untrusted one."
    /// </summary>
    public static string Build(IReadOnlyList<(string Role, string Content)> messages)
    {
        var prompt = new StringBuilder();

        foreach (var (role, content) in messages)
            prompt.Append(TurnStart).Append(role).Append('\n').Append(Neutralize(content)).Append(TurnEnd).Append('\n');

        prompt.Append(TurnStart).Append("assistant\n");

        return prompt.ToString();
    }

    /// <summary>
    /// Breaks any literal occurrence of a ChatML control marker inside message
    /// content so the tokenizer can never read it as a role-boundary token — see
    /// this class's own doc comment for why content, specifically, needs this and
    /// the fixed <c>role</c> strings never do.
    /// </summary>
    private static string Neutralize(string content) =>
        content
            .Replace(TurnStart, "<|" + InertBreak + "im_start|>")
            .Replace(TurnEnd, "<|" + InertBreak + "im_end|>");

    /// <summary>
    /// Belt-and-braces, not the primary stop mechanism — that is the native
    /// end-of-generation check <see cref="LlamaEngine.ChatAsync"/> relies on, which
    /// fires the instant <c>&lt;|im_end|&gt;</c> is sampled, before it is decoded to
    /// text at all. This only cleans up what a malformed completion leaves behind:
    /// an anti-prompt match that stopped generation but kept its own marker text in
    /// the buffer, or a continuation turn the model started hallucinating instead of
    /// stopping cleanly.
    /// </summary>
    public static string Sanitize(string rawOutput)
    {
        var cut = rawOutput.Length;

        foreach (var marker in new[] { TurnEnd, TurnStart })
        {
            var index = rawOutput.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && index < cut) cut = index;
        }

        // InertBreak exists only to defuse a marker on the way IN to the model.
        // It has no business in the text Otto pastes into someone's document or
        // saves as a note, and the system prompt asks for near-verbatim echo —
        // so whenever a dictation did contain a literal marker, the model is
        // being instructed to hand the break straight back. EditGuard would not
        // catch it either: it treats a zero-width space as a word separator, so
        // an output differing only by one registers as zero words touched.
        return rawOutput[..cut].Replace(InertBreak.ToString(), string.Empty).Trim();
    }
}
